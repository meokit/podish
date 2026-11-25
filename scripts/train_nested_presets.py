#!/usr/bin/env python3
"""Build and time nested instruction presets using only a training suite."""

import argparse
from collections import defaultdict
from concurrent.futures import ThreadPoolExecutor
from itertools import product
import json
import math
from pathlib import Path
import re
import shutil
from statistics import mean, median
import subprocess

from gen_operand_specializations import aarch64_imm12_shapes
from nested_baking_candidates import PARTIAL_FIELDS, build_pools, modrm_key, write_plans
from nested_baking_workloads import (
    benchmark_lock, digest, file_digest, read_json, run_workloads, stage_cli, write_json,
)


REPO = Path(__file__).resolve().parents[1]
MIB = 1 << 20
TIERS = (1.75, 3.5, 10)
CLASSES = ("modrm", "policies", "exact", "fusion")
INITIAL_BYTES = (260, 240, 155, 1200)
PARTIAL_BYTES = (200, 165, 220)
MIXES = (
    (0.42, 0.22, 0.36, 0.00),
    (0.47, 0.25, 0.13, 0.15),
    (0.10, 0.10, 0.78, 0.02),
    (0.12, 0.13, 0.35, 0.40),
    (0.22, 0.05, 0.73, 0.00),
)
PARTIAL_MIXES = (
    (0.23, 0.12, 0.24, 0.05, 0.22, 0.12, 0.02),
    (0.16, 0.08, 0.14, 0.06, 0.49, 0.06, 0.01),
    (0.18, 0.10, 0.16, 0.08, 0.11, 0.35, 0.02),
    (0.13, 0.08, 0.22, 0.12, 0.14, 0.13, 0.18),
)


def command(argv, path, timeout=600):
    with path.open("wb") as log:
        subprocess.run(argv, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=timeout)


def text_size(cfg, library):
    if cfg.get("library_format", "macho") == "macho":
        args = [cfg.get("size", "/usr/bin/size"), "-m"]
        pattern = r"^\s*Section __text:\s+(\d+)"
    else:
        args = [cfg.get("size", "size"), "-A", "-d"]
        pattern = r"^\.text[ \t]+(\d+)"
    output = subprocess.check_output([*args, library], text=True)
    return max(map(int, re.findall(pattern, output, re.MULTILINE)))


def build(cfg, build_dir, logs, jobs, plans=None, fusion_count=0, profile=False):
    logs.mkdir(parents=True, exist_ok=True)
    build_dir.mkdir(parents=True, exist_ok=True)
    args = [
        cfg["cmake"], "-G", "Ninja", "-S", REPO / "libfibercpu", "-B", build_dir,
        *cfg["cmake_args"], "-DCMAKE_BUILD_TYPE=Release", "-DFIBERCPU_BAKING_PRESET=custom",
        "-DFIBERCPU_ENABLE_LLVM_JIT=OFF", "-DFIBERCPU_LINKED_BLOCK_CACHE_FILE=",
        "-DFIBERCPU_ENABLE_DYNAMIC_SPECIALIZATION=OFF", "-DFIBERCPU_GPR_PIN=WRITE_BACK",
        f"-DFIBERCPU_ENABLE_BLOCK_PROFILE={'ON' if profile else 'OFF'}",
        f"-DFIBERCPU_STATIC_SPECIALIZATION={'ON' if plans else 'OFF'}",
        f"-DFIBERCPU_MODRM_PLAN_FILE={plans / 'modrm.json' if plans else ''}",
        f"-DFIBERCPU_STATIC_OPERAND_PLAN_FILE={plans / 'scalar.json' if plans else ''}",
        f"-DFIBERCPU_ENABLE_SUPEROPCODES={'ON' if fusion_count else 'OFF'}",
        f"-DFIBERCPU_OPERAND_BUDGET={fusion_count}",
    ]
    if cfg.get("guest") == "aarch64":
        args.extend(["-DFIBERCPU_GUEST_AARCH64=ON", "-DFIBERCPU_GUEST_I386=OFF",
                     "-DFIBERCPU_MODRM_PLAN_FILE=", "-DFIBERCPU_STATIC_OPERAND_PLAN_FILE=",
                     f"-DFIBERCPU_AARCH64_OPERAND_PLAN_FILE={plans / 'scalar.json' if plans else ''}",
                     "-DFIBERCPU_AARCH64_OPERAND_BUDGET=1024", "-DFIBERCPU_AARCH64_PAIR_BUDGET=1024",
                     "-DFIBERCPU_AARCH64_SEQUENCE_BUDGET=1024", "-DFIBERCPU_AARCH64_PARTIAL_BUDGET=1024"])
    else:
        args.extend(["-DFIBERCPU_GUEST_I386=ON", "-DFIBERCPU_GUEST_AARCH64=OFF"])
    if fusion_count:
        recipes = plans / "recipes.json"
        source = logs / "fusion.cpp"
        top = len(read_json(recipes)["candidates"])
        command([cfg.get("dotnet", "dotnet"), cfg["perf_tools"], "gen-superopcodes",
                 "--input", recipes, "--top", str(top), "--output", source], logs / "generator.log")
        target = plans / "fusion.cpp"
        if not target.exists() or file_digest(source) != file_digest(target):
            shutil.copyfile(source, target)
        args.extend([f"-DFIBERCPU_GENERATED_SUPEROPCODES_FILE={plans / 'fusion.cpp'}",
                     f"-DFIBERCPU_OPERAND_PLAN_FILE={plans / 'fusion-operands.json'}"])
    else:
        args.extend(["-DFIBERCPU_GENERATED_SUPEROPCODES_FILE=", "-DFIBERCPU_OPERAND_PLAN_FILE="])
    command(args, logs / "configure.log")
    command([cfg["cmake"], "--build", build_dir, "--target", "fibercpu", "-j", str(jobs)],
            logs / "build.log")
    return build_dir / cfg.get("library_name", "libfibercpu.dylib")


def capture_training(cfg, root, jobs):
    captures = root / "captures"
    outstanding = [case for case in cfg["workloads"]
                   if not (captures / case["id"] / "verified.json").is_file()]
    if not outstanding:
        return captures
    library = build(cfg, root / "profile", root / "profile-logs", jobs, profile=True)
    cli = stage_cli(cfg["managed_cli"], root / "profile-cli", library)
    with benchmark_lock(cfg["benchmark_lock"]):
        for case in outstanding:
            directory = captures / case["id"]
            directory.mkdir(parents=True, exist_ok=True)
            run_workloads(cfg, cli, [case], directory / "guest.log", directory)
            write_json(directory / "verified.json", {"blocks_sha256": file_digest(directory / "blocks.bin")})
            print(f"Captured {case['id']}", flush=True)
    return captures


def size_model(history, base_text, initial_bytes):
    # Use a regularized linear fit in units of estimated bytes. Cap the update
    # so one feature interaction cannot turn a class into a negative cost.
    samples = [(row["counts"], row["text_bytes"] - base_text)
               for row in history if "text_bytes" in row]
    weights = list(initial_bytes)
    for _ in range(12):
        for i, initial in enumerate(initial_bytes):
            numerator = float(initial)
            denominator = 1.0
            for counts, size in samples:
                weight = max(0.1, (MIB / max(size, MIB)) ** 2)
                residual = size - sum(c * w for j, (c, w) in enumerate(zip(counts, weights)) if j != i)
                numerator += weight * counts[i] * residual
                denominator += weight * counts[i] ** 2
            weights[i] = max(initial * 0.3, min(initial * 4, numerator / denominator))
    return weights


def inside(lower, higher):
    return tuple(lower) != tuple(higher) and all(a <= b for a, b in zip(lower, higher))


def choose_chains(history):
    by_tier = [[r for r in history if r.get("score") and r["tier"] == tier] for tier in TIERS]
    return sorted(
        (chain for chain in product(*by_tier)
         if all(inside(a["actual_counts"], b["actual_counts"]) and inside(a["counts"], b["counts"])
                for a, b in zip(chain, chain[1:]))),
        key=lambda chain: -sum(math.log(row["score"]) for row in chain),
    )


def propose(tier, round_index, history, lower, pools, base_text):
    partial = all(name in pools for name in PARTIAL_FIELDS)
    classes = (*CLASSES, *PARTIAL_FIELDS) if partial else CLASSES
    costs = size_model(history, base_text, (*INITIAL_BYTES, *PARTIAL_BYTES) if partial else INITIAL_BYTES)
    limit = tier * MIB
    ceiling = [len(pools[name]) for name in classes]
    floor = tuple(lower)
    seen = {tuple(row["counts"]) for row in history if row["tier"] == tier}
    valid = sorted((row for row in history if row.get("score") and row["tier"] == tier),
                   key=lambda row: -row["score"])
    mixes = list(PARTIAL_MIXES if partial else MIXES)
    exploration_rounds = len(mixes)
    champion = next((row for row in valid if inside(floor, row["counts"])), None)
    if round_index >= exploration_rounds and champion:
        # Search locally around the best measured mix by shifting budget
        # toward one feature class. Keep broader mixes as alternatives.
        spent = [c * w for c, w in zip(champion["counts"], costs)]
        shares = [x / sum(spent) for x in spent]
        favored = len(CLASSES) + TIERS.index(tier) if partial else (round_index - exploration_rounds) % len(classes)
        mixes.insert(0, tuple(0.76 * share + (0.24 if i == favored else 0)
                              for i, share in enumerate(shares)))
    else:
        pivot = round_index % exploration_rounds
        mixes = mixes[pivot:] + mixes[:pivot]
    # The first large proposals reserve some space for fusion in all but the
    # explicitly unfused mix. Small variants remain free to choose zero.
    if round_index == 0 and tier != TIERS[0] and not partial:
        mixes.insert(0, (0.18, 0.12, 0.56, 0.14))
    gross = limit - base_text - (24_000 if tier == TIERS[0] else 95_000)
    reserved = sum(n * cost for n, cost in zip(floor, costs))
    for fill in (0.91, 0.97, 0.80, 0.69, 0.55):
        if round_index >= exploration_rounds and fill == 0.91:
            fill = 0.95
        remaining = max(0, gross * fill - reserved)
        for mix in mixes:
            proposed = [min(cap, n + int(remaining * share / cost))
                        for cap, n, share, cost in zip(ceiling, floor, mix, costs)]
            # Redistribute unused space when a ranked list has been exhausted.
            unused = remaining - sum((n - a) * cost for n, a, cost in zip(proposed, floor, costs))
            for i in sorted(range(len(mix)), key=lambda i: -mix[i]):
                if mix[i] and unused >= costs[i]:
                    more = min(ceiling[i] - proposed[i], int(unused / costs[i]))
                    proposed[i] += more
                    unused -= more * costs[i]
            point = tuple(proposed)
            if point not in seen and inside(floor, point):
                return point
    raise RuntimeError(f"no untried {tier} MiB proposal extends {floor}")


def compile_trial(cfg, root, pools, result, jobs):
    directory = root / result["name"]
    directory.mkdir(exist_ok=True)
    # All candidates have distinct archived plans. Only the active plans and
    # build tree for one tier are reused to keep Ninja's incremental state.
    active = root / f"build-{result['tier']}"
    plans = active / "active-plans"
    plans.mkdir(parents=True, exist_ok=True)
    result["actual_counts"] = write_plans(directory, result["counts"], pools)
    for filename in ("modrm.json", "scalar.json", "fusion-operands.json", "recipes.json"):
        origin = directory / filename
        target = plans / filename
        if origin.is_file():
            if not target.exists() or file_digest(origin) != file_digest(target):
                shutil.copyfile(origin, target)
        else:
            target.unlink(missing_ok=True)
    try:
        library = build(cfg, active, directory, jobs, plans, result["counts"][3])
        result["text_bytes"] = text_size(cfg, library)
        if result["counts"][3]:
            lowered = (active / "generated/operand_specializations.inc").read_text()
            rows = re.findall(r"^    \{\w+_handlers\[0\], OperandClone_", lowered, re.MULTILINE)
            if len(rows) != result["counts"][3]:
                raise RuntimeError("linked fusion variants do not match the trial manifest")
        if any(result["counts"][4:]):
            generated = defaultdict(int)
            for shard in (active / "generated").glob("scalar_bindings_*.cpp"):
                for count, mask in re.findall(
                        r"std::array<PartialScalarBinding, (\d+)> \w+_partial_([123])", shard.read_text()):
                    generated[int(mask)] += int(count)
            if tuple(generated[mask] for mask in (1, 2, 3)) != tuple(result["counts"][4:]):
                raise RuntimeError("generated partial scalar bindings do not match the trial manifest")
        if result["text_bytes"] <= result["tier"] * MIB:
            destination = directory / library.name
            shutil.copy2(library, destination)
            result["library"] = str(destination)
        else:
            result["error"] = "over text budget"
    except (OSError, RuntimeError, subprocess.SubprocessError) as error:
        result["error"] = str(error)
    write_json(directory / "build.json", result)
    print(f"{result['name']} built: {result.get('text_bytes')} bytes, {result.get('error', 'eligible')}", flush=True)
    return result


def score_ratios(samples):
    cases = {key: median(values) for key, values in samples.items()}
    return dict(score=math.exp(mean(math.log(value) for value in cases.values())),
                worst=min(cases.values()), cases=cases, samples=samples)


def measure(cfg, directory, reference_cli, trials, repeat, offset=0):
    directory.mkdir(parents=True, exist_ok=True)
    cases = cfg["workloads"]
    prepared = [(trial, stage_cli(cfg["managed_cli"], directory / trial["name"], trial["library"]))
                for trial in trials if "library" in trial]
    ratios = {trial["name"]: defaultdict(list) for trial, _ in prepared}
    measurements = []
    with benchmark_lock(cfg["benchmark_lock"]):
        for i in range(repeat):
            order = prepared if (i + offset) % 2 == 0 else list(reversed(prepared))
            first = run_workloads(cfg, reference_cli, cases, directory / f"{i}-reference-before.log")
            batch = []
            for trial, cli in order:
                try:
                    values = run_workloads(cfg, cli, cases, directory / f"{i}-{trial['name']}.log")
                    batch.append((trial, values))
                except (OSError, RuntimeError, subprocess.SubprocessError) as error:
                    trial["error"] = str(error)
                    trial.pop("score", None)
            last = run_workloads(cfg, reference_cli, cases, directory / f"{i}-reference-after.log")
            measurements.append(dict(reference_before=first, reference_after=last,
                                     candidates={trial["name"]: values for trial, values in batch}))
            write_json(directory / "measurements.json", measurements)
            for index, (trial, values) in enumerate(batch, start=1):
                fraction = index / (len(batch) + 1)
                for key, value in values.items():
                    paired = (1 - fraction) * first[key] + fraction * last[key]
                    ratios[trial["name"]][key].append(paired / value)
    for trial, _ in prepared:
        if "error" not in trial:
            trial.update(score_ratios(ratios[trial["name"]]))
            print(f"{trial['name']} training: {trial['score']:.4f}x, worst {trial['worst']:.4f}x", flush=True)


def plan_keys(directory):
    modrm = read_json(directory / "modrm.json")
    exact = read_json(directory / "scalar.json")["entries"]
    fusions = read_json(directory / "fusion-operands.json")["entries"]
    recipe = directory / "recipes.json"
    return [
        {modrm_key(row) for row in modrm["entries"]},
        {(*modrm_key(row), row["width"], row["flags"]) for row in modrm["scalar_policies"]},
        {(row["originalId"], row.get("slots"), digest(json.dumps(row["operands"], sort_keys=True).encode()))
         for row in exact},
        {(row["originalId"], digest(json.dumps(row["operands"], sort_keys=True).encode()))
         for row in fusions},
        {tuple(row["pair"]) for row in read_json(recipe)["candidates"]} if recipe.is_file() else set(),
    ]


def freeze(root, reference, chain, provenance):
    frozen = root / "frozen"
    frozen.mkdir()
    names = ("modrm.json", "scalar.json", "fusion-operands.json", "recipes.json", "fusion.cpp")
    rows = {}
    baseline = frozen / "baseline"
    baseline.mkdir()
    baseline_library = baseline / reference.name
    shutil.copy2(reference, baseline_library)
    rows["baseline"] = {"library": str(baseline_library), "sha256": file_digest(baseline_library)}
    previous = None
    for trial in chain:
        name = f"{trial['tier']}m"
        directory = frozen / name
        directory.mkdir()
        source = root / trial["name"]
        keys = plan_keys(source)
        if previous and (previous == keys or any(not old <= new for old, new in zip(previous, keys))):
            raise RuntimeError(f"{name} is not a strict superset")
        previous = keys
        library = directory / Path(trial["library"]).name
        shutil.copy2(trial["library"], library)
        for filename in names:
            if (source / filename).is_file():
                target_name = "superopcodes.generated.cpp" if filename == "fusion.cpp" else filename
                shutil.copy2(source / filename, directory / target_name)
        rows[name] = {**trial, "library": str(library), "sha256": file_digest(library),
                      "files": {path.name: file_digest(path) for path in sorted(directory.iterdir())},
                      "key_counts": [len(item) for item in keys]}
    write_json(frozen / "manifest.json", {"presets": rows, "training_provenance": provenance})
    return rows


def aarch64_plan(profiles, budget, partial_budget=32):
    # Give each workload equal weight, regardless of how long its capture ran.
    plan = {"version": 1, "architecture": "aarch64"}
    for group, fields in (("entries", ("word",)), ("pairs", ("first", "second")), ("sequences", ("words",))):
        weights = defaultdict(int)
        for profile in profiles:
            rows = profile.get(group, [])
            total = sum(row["count"] for row in rows)
            for row in rows:
                key = tuple(tuple(row[f]) if f == "words" else row[f] for f in fields)
                weights[key] += max(1, round(row["count"] * 100_000_000 / total))
        def priority(key):
            saved = len(key[0]) - 1 if group == "sequences" else 1
            return -weights[key] * saved, key
        ranked = sorted(weights, key=priority)[:budget]
        plan[group] = [dict(zip(fields, key), count=weights[key]) for key in ranked]
        if group == "entries":
            counts = {word: count for (word,), count in weights.items()}
            plan["partials"] = aarch64_imm12_shapes(counts, {word for (word,) in ranked}, partial_budget)
    return plan


def train_aarch64(cfg, root, jobs, partial):
    partial_budget = cfg.get("aarch64_partial_budget", 32) if partial else 0
    if type(partial_budget) is not int or not 0 <= partial_budget <= 1024:
        raise ValueError("aarch64_partial_budget must be an integer in 0..1024")
    budgets = sorted(set(cfg.get("aarch64_operand_budgets", [32, 128, 512])))
    if not budgets or any(type(n) is not int or not 1 <= n <= 1024 for n in budgets):
        raise ValueError("aarch64_operand_budgets must contain integers in 1..1024")
    reference = build(cfg, root / "baseline", root / "baseline-logs", jobs)
    cli = stage_cli(cfg["managed_cli"], root / "baseline-cli", reference)
    profiles = []
    with benchmark_lock(cfg["benchmark_lock"]):
        for case in cfg["workloads"]:
            capture = root / "captures" / case["id"]
            capture.mkdir(parents=True, exist_ok=True)
            run_workloads(cfg, cli, [case], capture / "guest.log", capture)
            profile = read_json(capture / "words.json")
            if profile["architecture"] != "aarch64" or not profile["entries"]:
                raise ValueError(f"no AArch64 instructions captured for {case['id']}")
            profiles.append(profile)
    candidates = []
    for budget in budgets:
        name = f"words-{budget}"
        directory = root / name
        directory.mkdir(exist_ok=True)
        plan = aarch64_plan(profiles, budget, partial_budget)
        write_json(directory / "scalar.json", plan)
        library = build(cfg, root / "trial-build", directory, jobs, plans=directory)
        archived = directory / library.name
        shutil.copy2(library, archived)
        candidates.append(dict(name=name, words=budget, library=str(archived), text_bytes=text_size(cfg, archived)))
    measure(cfg, root / "measure", cli, candidates, cfg.get("final_repeats", 3))
    write_json(root / "trials.json", candidates)
    valid = [trial for trial in candidates if "score" in trial]
    if not valid:
        raise RuntimeError("all AArch64 benchmark candidates failed validation")
    best = max(valid, key=lambda trial: trial["score"])
    frozen = root / "frozen"
    frozen.mkdir(exist_ok=True)
    if best["score"] > 1:
        shutil.copy2(root / best["name"] / "scalar.json", frozen)
    else:
        best = dict(name="baseline", words=0, score=1.0, library=str(reference))
        write_json(frozen / "scalar.json", aarch64_plan([], 0))
    shutil.copy2(best["library"], frozen)
    write_json(frozen / "measurement.json", best)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--rounds", type=int, default=10)
    parser.add_argument("--partial-operands", action=argparse.BooleanOptionalAction, default=True,
                        help="Include scalar forms with dynamic displacement or immediate (default)")
    parser.add_argument("--jobs", type=int, default=14, help="Total build jobs across the three tiers")
    args = parser.parse_args()
    if args.rounds < 1 or args.jobs < 3:
        parser.error("need at least one round and three build jobs")
    cfg = read_json(args.config)
    root = args.output.resolve()
    root.mkdir(exist_ok=True, parents=True)
    if (root / "frozen").exists():
        parser.error("output already contains frozen presets")
    guest = cfg.get("guest", "i386")
    if guest == "aarch64":
        train_aarch64(cfg, root, args.jobs, args.partial_operands)
        return
    if guest != "i386":
        parser.error(f"unknown guest {guest}")
    captures = capture_training(cfg, root, args.jobs)
    provenance = {"config_sha256": file_digest(args.config), "profiles": {
        case["id"]: file_digest(captures / case["id"] / "blocks.bin") for case in cfg["workloads"]}}
    if args.partial_operands:
        provenance["partial_operands"] = True
    meta_file, pool_file = root / "provenance.json", root / "pools.json"
    if meta_file.is_file() and read_json(meta_file) != provenance:
        parser.error("training workloads or captures differ from the previous run")
    if not pool_file.is_file():
        write_json(pool_file, build_pools(cfg["workloads"], captures, include_partial=args.partial_operands))
    pools = read_json(pool_file)
    write_json(meta_file, provenance)
    print("Candidate pools:", {name: len(rows) for name, rows in pools.items()}, flush=True)
    reference = build(cfg, root / "baseline", root / "baseline-logs", args.jobs)
    reference_cli = stage_cli(cfg["managed_cli"], root / "baseline-cli", reference)
    base_text = text_size(cfg, reference)
    history_file = root / "rounds.json"
    history = read_json(history_file) if history_file.is_file() else []
    for i in range(len(history) // len(TIERS), args.rounds):
        chain = next(iter(choose_chains(history)), None)
        lower = (0,) * (len(CLASSES) + (len(PARTIAL_FIELDS) if args.partial_operands else 0))
        trials = []
        for j, tier in enumerate(TIERS):
            if j and chain and i % 2 == 0:
                lower = chain[j - 1]["counts"]
            point = propose(tier, i, history, lower, pools, base_text)
            print(f"Round {i + 1:02d}, {tier} MiB: {point}", flush=True)
            trials.append({"name": f"r{i + 1:02d}-{tier}m", "tier": tier, "counts": point})
            lower = point
        with ThreadPoolExecutor(max_workers=3) as pool:
            trials = list(pool.map(
                lambda trial: compile_trial(cfg, root, pools, trial, args.jobs // 3), trials))
        measure(cfg, root / f"round-{i + 1:02d}-timing", reference_cli, trials, 1, i)
        history.extend(trials)
        write_json(history_file, history)
    chains = choose_chains(history)
    if not chains:
        raise RuntimeError("no measured feasible nested chain")
    shortlist = {trial["name"]: dict(trial) for chain in chains[:4] for trial in chain}
    measure(cfg, root / "finalists-timing", reference_cli, list(shortlist.values()), repeat=3)
    write_json(root / "finalists.json", list(shortlist.values()))
    verified = choose_chains(shortlist.values())
    if not verified:
        raise RuntimeError("no finalist chain passed repeated verification")
    print(freeze(root, reference, verified[0], provenance).keys(), flush=True)


if __name__ == "__main__":
    main()
