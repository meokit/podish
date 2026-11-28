"""Calibrate static marginal text costs from archived, linked Mach-O baking plans.

The cache contains logical decoder bindings. Local clone ordinals are resolved
against the generating plan before they leave a single binary.
"""

import argparse
from collections import Counter, defaultdict
from dataclasses import dataclass
from functools import lru_cache
import hashlib
import json
import math
from pathlib import Path
import re
from statistics import median
import struct

from aarch64_baked_words import canonicalize_baked_word
from gen_instruction_catalog import read_catalog
from operand_specialization_data import generated_fusions, resolve_training_plan, training_handlers


CLASSES = {"i386": ("modrm", "policies", "exact", "fusion", "dynamic-disp", "dynamic-imm", "dynamic-both"),
           "aarch64": ("entries", "partials", "pairs", "sequences")}
PRIORS = dict(zip(CLASSES["i386"] + CLASSES["aarch64"], (260, 240, 155, 1200, 200, 165, 220, 240, 210, 500, 1100)))
OPERAND = ("modrm", "prefixes", "meta", "length", "ea_desc", "disp", "imm")
VERSION = 1


def read(path):
    return json.loads(path.read_bytes())


def arm_key(kind, words):
    return kind + "/" + ".".join(f"{canonicalize_baked_word(w):08x}" for w in words)


def x86_key(kind, *fields):
    return kind + "/" + ",".join("_" if x is None else str(x) for x in fields)


@lru_cache(maxsize=None)
def scalar_name_ids():
    return {h.name: idx for idx, h in training_handlers(read_catalog(), {}).items()}


def binding(kind, row):
    if kind in CLASSES["aarch64"]:
        if kind == "partials":
            words = (row,)
        elif kind == "entries":
            words = (row["word"],)
        elif kind == "pairs":
            words = (row["first"], row["second"])
        else:
            words = row["words"]
        return arm_key(kind, words), (kind, len(words), canonicalize_baked_word(words[0]) >> 24)
    if kind in ("modrm", "policies"):
        fields = (row["training_id"], row["slots"], row["modrm"])
        policy = (row["width"], row["flags"]) if kind == "policies" else ()
        return x86_key(kind, *fields, *policy), (kind, row["modrm"] >> 6, row["training_id"])
    operands = row["operands"]
    fields = [row["originalId"]]
    if kind.startswith("dynamic"):
        fields.append(row["slots"])
    for op in operands:
        fields.extend(op[k] for k in OPERAND)
    return x86_key(kind, *fields), (kind, len(operands), row["originalId"])


class MachText:
    """Apple function-start spans include alignment. Count each address once."""
    def __init__(self, path):
        b = self.data = path.read_bytes()
        if b[:4] != bytes.fromhex("cffaedfe"):
            raise ValueError(f"expected thin little-endian Mach-O 64: {path}")
        self.strings = self.functions = self.section = None
        pos = 32
        for _ in range(struct.unpack_from("<I", b, 16)[0]):
            cmd, size = struct.unpack_from("<II", b, pos)
            if cmd == 2:
                self.strings = struct.unpack_from("<4I", b, pos + 8)
            if cmd == 0x26:
                self.functions = struct.unpack_from("<II", b, pos + 8)
            if cmd == 0x19 and b[pos + 8:pos + 24].rstrip(b"\0") == b"__TEXT":
                self.base = struct.unpack_from("<Q", b, pos + 24)[0]
                for j in range(struct.unpack_from("<I", b, pos + 64)[0]):
                    q = pos + 72 + 80 * j
                    if b[q:q + 16].rstrip(b"\0") == b"__text":
                        self.section = struct.unpack_from("<QQI", b, q + 32)
            pos += size
        if not self.section or not self.strings or not self.functions:
            raise ValueError(f"missing Mach-O text, symbols or function starts: {path}")

    def symbols(self):
        b = self.data
        start, length, _ = self.section
        end = start + length
        offset, size = self.functions
        addr = self.base
        starts = []
        shift = delta = 0
        for byte in b[offset:offset + size]:
            delta |= (byte & 127) << shift
            if byte & 128:
                shift += 7
                continue
            if not delta:
                break
            addr += delta
            if start <= addr < end:
                starts.append(addr)
            shift = delta = 0
        spans = {a: z - a for a, z in zip(starts, starts[1:] + [end])}
        offset, count, strings, _ = self.strings
        for i in range(count):
            ix, typ, _, _, addr = struct.unpack_from("<IBBHQ", b, offset + i * 16)
            if typ & 0xe0 or (typ & 0x0e) != 0x0e or addr not in spans:
                continue
            s = strings + ix
            yield addr, spans[addr], b[s:b.index(b"\0", s)].decode("ascii", errors="replace")


def codegen_digest():
    """Invalidate prices when native handlers or binding generators change."""
    repo = Path(__file__).resolve().parents[1]
    paths = [p for p in (repo / "libfibercpu/src").rglob("*")
             if p.suffix in {".cpp", ".h", ".inc", ".def", ".json"}]
    paths.extend((repo / "Podish.PerfTools").glob("Program.Superopcode*.cs"))
    paths.extend((repo / "scripts" / name) for name in (
        "gen_instruction_catalog.py", "gen_operand_specializations.py", "scalar_modrm.py",
        "operand_specialization_data.py", "gen_handler_reflection.py", "aarch64_baked_words.py"))
    paths.extend((repo / "libfibercpu/cmake").glob("*.cmake"))
    paths.append(repo / "libfibercpu/CMakeLists.txt")
    digest = hashlib.sha256()
    for path in sorted(paths):
        digest.update(str(path.relative_to(repo)).encode() + b"\0")
        digest.update(hashlib.sha256(path.read_bytes()).digest())
    return digest.hexdigest()


def fingerprint(build):
    build = Path(build)
    image = MachText(build / "libfibercpu.dylib")
    _, size, off = image.section
    settings = {}
    for line in (build / "CMakeCache.txt").read_text().splitlines():
        m = re.match(r"(\w+):(?:STRING|BOOL|PATH)=(.*)$", line)
        if m and (m[1].startswith(("FIBERCPU_", "CMAKE_CXX_FLAGS", "CMAKE_OSX_")) or m[1] == "CMAKE_BUILD_TYPE"):
            settings[m[1]] = m[2]
    for cmake, names in (("CMakeCXXCompiler", ("CMAKE_CXX_COMPILER_ID", "CMAKE_CXX_COMPILER_VERSION")),
                         ("CMakeSystem", ("CMAKE_SYSTEM_NAME", "CMAKE_SYSTEM_PROCESSOR"))):
        path, = sorted((build / "CMakeFiles").glob(f"*/{cmake}.cmake"))
        content = path.read_text()
        for name in names:
            settings[name] = re.search(r'set\(' + name + r' "([^"]+)"\)', content)[1]
    return {"codegen_sha256": codegen_digest(), "baseline_text_sha256": hashlib.sha256(image.data[off:off + size]).hexdigest(), "settings": settings}


UINT = re.compile(r"Lj(\d+)E")
BAKED_ARM = re.compile(r"10BakedLogicILj\d+ELb([01])EJ((?:Lj\d+E)+)")
PROFILE = re.compile(r"13baked_operand\d+(\w+?)INS_11OperandSpec(7Profile|14PartialProfile)IXtlNS_19DecodedOperandShapeE((?:Lj\d+E)+)EELj\d+E")
MODRM = re.compile(r'ZNS_(?:2op)?\d+(Op\w+?)EP.*?9WithModRMI\w*?9SlotBoundILj(\d+)ELb[01]EEELh(\d+)E')
CLONE = re.compile(r"OperandClone_(\d+)_(SuperOpcode_\w+?)EP")
DYNAMIC = {1: "dynamic-disp", 2: "dynamic-imm", 3: "dynamic-both"}


def arm_symbol(symbol):
    if "DispatchWrapper" not in symbol:
        return None
    m = BAKED_ARM.search(symbol)
    if not m:
        return None
    words = tuple(map(int, UINT.findall(m[2])))
    partial = m[1] == "1"
    kind = "partials" if partial else {1: "entries", 2: "pairs"}.get(len(words), "sequences")
    return arm_key(kind, words)


def x86_symbol(symbol, clones):
    if "OperandClone_" in symbol:
        m = CLONE.search(symbol)
        return clones.get((int(m[1]), m[2])) if m else None
    if "DispatchWrapper" not in symbol:
        return None
    if "DecodedOperandShape" in symbol:
        m = PROFILE.search(symbol)
        if not m or m[1] not in scalar_name_ids():
            return None
        values = list(map(int, UINT.findall(m[3])))
        encoding, ea, disp, imm = (values + [0] * 4)[:4]
        fields = [encoding >> shift & 255 for shift in (0, 8, 16, 24)] + [ea, disp, imm]
        idx = scalar_name_ids()[m[1]]
        if m[2] == "7Profile":
            return x86_key("exact", idx, *fields)
        extra = re.match(r"Lj(\d+)ELj([123])E", symbol[m.end():])
        if extra:
            slots, mask = map(int, extra.groups())
            fields[5] = None if mask & 1 else disp
            fields[6] = None if mask & 2 else imm
            return x86_key(DYNAMIC[mask], idx, slots, *fields)
    if "WithModRM" in symbol:
        m = MODRM.search(symbol)
        if m:
            name, slots, modrm = m.groups()
            name, _, policy = name.partition("_Baked")
            idx = scalar_name_ids().get(name)
            if idx is not None:
                if not policy:
                    return x86_key("modrm", idx, slots, modrm)
                width, _, flags = policy.partition("_")
                return x86_key("policies", idx, slots, modrm, width or None, flags.lower() or None)
    return None


def fusion_clones(directory):
    plan = read(directory / "fusion-operands.json")
    if not plan["entries"]:
        return {}
    source = (directory / "fusion.cpp").read_text()
    rows = resolve_training_plan(plan, read_catalog(), generated_fusions(source))
    return {(i, row["original"]): binding("fusion", row)[0] for i, row in enumerate(rows)}


@dataclass
class Trial:
    method: str
    name: str
    keys: tuple
    unique: str
    spans: dict
    delta: int


def collect(root, guest):
    trials, features = [], {}
    signature = None
    # Accept one trainer output (possibly scoped to the guest) or sibling
    # runs. Both rank-* links and earlier bNNN-* archives have build.json.
    root = Path(root)
    if (root / "pools.json").is_file():
        runs = [root]
    elif (root / guest / "pools.json").is_file():
        runs = [root / guest]
    else:
        runs = sorted(p.parent for p in root.glob(f"*/{guest}/pools.json"))
    for base in runs:
        method = base.parent.name if base.name == guest else base.name
        current = fingerprint(base / "baseline")
        guest_option = {"i386": "FIBERCPU_GUEST_I386", "aarch64": "FIBERCPU_GUEST_AARCH64"}[guest]
        if current["settings"].get(guest_option) != "ON":
            raise ValueError(f"{base} baseline does not enable the {guest} guest")
        if signature is not None and current != signature:
            raise ValueError("incompatible historical baseline: " + str(base))
        signature = current
        baseline = MachText(base / "baseline/libfibercpu.dylib")
        generic = {sym for _, _, sym in baseline.symbols()}
        pools = read(base / "pools.json")
        bindings = [binding(k, row) for k in CLASSES[guest] for row in pools.get(k, ())]
        features.update(bindings)
        for build in sorted(base.glob("*/build.json")):
            directory = build.parent
            trial = read(build)
            lib = directory / "libfibercpu.dylib"
            if not lib.exists() or trial.get("text_delta_bytes", 0) <= 0:
                continue
            keys = {bindings[i][0] for i in trial["selection"]}
            if guest == "i386":  # materialization can introduce required ModRM parents
                keys.update(binding("modrm", row)[0] for row in read(directory / "modrm.json")["entries"])
            keys = tuple(sorted(keys))
            selected = set(keys)
            owners = defaultdict(set)
            image = MachText(lib)
            if (image.section[1] != trial["text_bytes"]
                    or image.section[1] - baseline.section[1] != trial["text_delta_bytes"]
                    or hashlib.sha256(image.data).hexdigest() != trial["library_sha256"]):
                raise ValueError(f"archived text or artifact checksum differs: {lib}")
            clones = fusion_clones(directory) if guest == "i386" else {}
            sizes = {}
            baseline_addresses = set()
            for addr, size, symbol in image.symbols():
                if symbol in generic:
                    baseline_addresses.add(addr)
                    continue
                key = arm_symbol(symbol) if guest == "aarch64" else x86_symbol(symbol, clones)
                if key in selected:
                    owners[addr].add(key)
                    sizes[addr] = size
            spans = Counter()
            for addr, group in owners.items():
                if addr in baseline_addresses:
                    continue
                for key in group:
                    spans[key] += sizes[addr] / len(group)
            digest = hashlib.sha256("\n".join(keys).encode()).hexdigest()
            trials.append(Trial(method, directory.name, keys, digest, spans, trial["text_delta_bytes"]))
    if not trials:
        raise ValueError(f"no linked archives in {root}")
    return trials, features, signature


def unique_trials(trials):
    return list({t.unique: t for t in trials}.values())


def raw_cost(key, feature, model):
    if key in model["spans"]:
        return model["spans"][key]
    kind, width, op = feature
    groups = model["groups"]
    for name in (f"{kind}:{width}:{op}", f"{kind}:{width}", kind):
        if name in groups:
            return groups[name]
    return PRIORS[kind]


def fit(trials, features, classes):
    # First measure handlers directly. Unseen shapes shrink to neighboring
    # shapes of the same operation/width, then to their whole class.
    samples = defaultdict(list)
    for trial in trials:
        for key, size in trial.spans.items():
            samples[key].append(size)
    spans = {key: median(sizes) for key, sizes in samples.items()}
    groups = defaultdict(list)
    for key, size in spans.items():
        kind, width, op = features[key]
        for group in (f"{kind}:{width}:{op}", f"{kind}:{width}", kind):
            groups[group].append(size)
    model = {"spans": spans, "groups": {k: median(v) for k, v in groups.items() if len(v) >= 3}}
    # Fit class multipliers to text growth, including resolver code, shared
    # recipes and code folded by LTO. Lookup tables in data are outside the
    # native text budget. A relative error loss keeps the
    # small cap represented. A ridge prior toward observed spans stabilizes
    # classes that co-occur across the available historical plans.
    X, residual = [], []
    index = {name: i for i, name in enumerate(classes)}
    prices = {}
    for trial in trials:
        row = [0.] * len(classes)
        for key in trial.keys:
            if key not in prices:
                prices[key] = raw_cost(key, features[key], model)
            row[index[features[key][0]]] += prices[key] / trial.delta
        X.append(row)
        residual.append(1 - sum(row))
    ridge = 0.002 * len(trials)
    coeff = [1.] * len(classes)
    for _ in range(1000):
        change = 0.
        for i in range(len(coeff)):
            direction = (sum(row[i] * err for row, err in zip(X, residual)) - ridge * (coeff[i] - 1))
            curvature = sum(row[i] ** 2 for row in X) + ridge
            step = max(0.05 - coeff[i], direction / curvature)
            coeff[i] += step
            change = max(change, abs(step))
            for j, row in enumerate(X):
                residual[j] -= row[i] * step
        if change < 1e-7:
            break
    model["multipliers"] = dict(zip(classes, coeff))
    return model


def predict(key, feat, model):
    return max(1, round(raw_cost(key, feat, model) * model["multipliers"][feat[0]]))


def compile_cache(root, guest):
    trials, features, signature = collect(root, guest)
    classes = CLASSES[guest]
    # With independent runs, hold out all plans from one run together. If
    # there is a single run (or every other run repeats its selections),
    # validate on one distinct table at a time. Never train on the same
    # table's measured symbols when predicting its linked growth.
    distinct = unique_trials(trials)
    if len(distinct) < 2:
        raise ValueError("calibration requires at least two distinct linked plans")
    groups = defaultdict(list)
    for trial in trials:
        groups[trial.method].append(trial)
    held_groups = [unique_trials(group) for group in groups.values()]
    validation_method = "leave one run out, purge identical tables"
    if any(len(held) == len(distinct) for held in held_groups):
        held_groups = [[trial] for trial in distinct]
        validation_method = "leave one distinct table out"
    predictions = []
    for held in held_groups:
        excluded = {t.unique for t in held}
        train = unique_trials([t for t in trials if t.unique not in excluded])
        model = fit(train, features, classes)
        for trial in held:
            estimated = sum(predict(key, features[key], model) for key in trial.keys)
            predictions.append(dict(method=trial.method, plan=trial.name, identity=trial.unique, actual=trial.delta,
                                    predicted=estimated, residual=trial.delta - estimated))
    # One observation per distinct table. If multiple strategies share a
    # table use its most optimistic held-out estimate for the guard.
    residuals = {}
    for p in predictions:
        key = p["identity"]
        residuals[key] = max(residuals.get(key, p["residual"]), p["residual"])
    errors = sorted(residuals.values())
    reserve = max(0, errors[math.ceil(0.9 * len(errors)) - 1])
    model = fit(unique_trials(trials), features, classes)
    return dict(version=VERSION, guest=guest, toolchain=signature, model=model, reserve=reserve,
                validation=dict(method=validation_method,
                                linked=len(trials), distinct=len(errors), quantile=0.9,
                                median_abs_error=median(map(abs, errors)), max_underprediction=max(errors),
                                folds=predictions))


def rank_costs(cache_path, guest, classes, pools, baseline_build_dir):
    cache = read(Path(cache_path))
    if cache["version"] != VERSION or cache["guest"] != guest:
        raise ValueError("incompatible cost cache version or guest")
    if fingerprint(baseline_build_dir) != cache["toolchain"]:
        raise ValueError("cost cache baseline code, host/compiler or CMake flags differ; recalibrate")
    model = cache["model"]
    costs = []
    coverage = {}
    for kind in classes:
        matches = 0
        for row in pools[kind]:
            key, feature = binding(kind, row)
            matches += key in model["spans"]
            costs.append(predict(key, feature, model))
        coverage[kind] = {"linked_bindings": matches, "total": len(pools[kind])}
    diagnostics = {k: v for k, v in cache["validation"].items() if k != "folds"}
    diagnostics.update(model="Mach-O binding spans and calibrated class multipliers", coverage=coverage,
                       cache_sha256=hashlib.sha256(Path(cache_path).read_bytes()).hexdigest())
    return costs, cache["reserve"], diagnostics


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("archive", type=Path, help="Trainer output or directory containing multiple training runs")
    ap.add_argument("guest", choices=tuple(CLASSES))
    ap.add_argument("output", type=Path)
    args = ap.parse_args()
    cache = compile_cache(args.archive, args.guest)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    tmp = args.output.with_suffix(".tmp")
    tmp.write_text(json.dumps(cache, separators=(",", ":"), sort_keys=True) + "\n")
    tmp.replace(args.output)
    print(args.guest, "reserve", cache["reserve"], {k:v for k,v in cache["validation"].items() if k != "folds"})


if __name__ == "__main__":
    main()
