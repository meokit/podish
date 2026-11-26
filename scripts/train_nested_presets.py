#!/usr/bin/env python3
"""Build and time nested instruction presets using only a training suite."""

import argparse
from collections import Counter, defaultdict
import json
import math
from pathlib import Path
import re
import shutil
from statistics import mean, median
import subprocess
import tempfile
import time

from baking_joint_rank import joint_rank
from baking_rank_builds import materialize
from baking_rank_cost import PRIORS, rank_costs
from baking_replay import Replay
from check_native_text_budget import native_text_size
from gen_operand_specializations import aarch64_imm12_shapes
from nested_baking_candidates import PARTIAL_FIELDS, build_pools, modrm_key, write_plans
from nested_baking_workloads import (
    benchmark_lock, digest, file_digest, read_json, run_workloads, split_families, stage_cli, write_json,
)


RANKING_METHOD = "joint-general"
REPO = Path(__file__).resolve().parents[1]
MIB = 1 << 20
TIERS = (1.5, 3, 10)
CLASSES = ("modrm", "policies", "exact", "fusion")
ARM_CLASSES = ("entries", "partials", "pairs", "sequences")


def command(argv, path, timeout=600):
    with path.open("wb") as log:
        subprocess.run(argv, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=timeout)


def text_size(cfg, library):
    library_format = cfg.get("library_format", "macho")
    if library_format == "macho":
        return native_text_size(library, cfg.get("size", "/usr/bin/size"), None)
    if library_format == "pe":
        return native_text_size(library, None, None)
    return native_text_size(library, None, cfg.get("size", "size"))


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
        arm_plan = plans / "scalar.json" if plans else None
        counts = read_json(arm_plan) if arm_plan else {}
        args.extend(["-DFIBERCPU_GUEST_AARCH64=ON", "-DFIBERCPU_GUEST_I386=OFF",
                     "-DFIBERCPU_MODRM_PLAN_FILE=", "-DFIBERCPU_STATIC_OPERAND_PLAN_FILE=",
                     f"-DFIBERCPU_AARCH64_OPERAND_PLAN_FILE={arm_plan or ''}",
                     f"-DFIBERCPU_AARCH64_OPERAND_BUDGET={len(counts.get('entries', []))}",
                     f"-DFIBERCPU_AARCH64_PARTIAL_BUDGET={len(counts.get('partials', []))}",
                     f"-DFIBERCPU_AARCH64_PAIR_BUDGET={len(counts.get('pairs', []))}",
                     f"-DFIBERCPU_AARCH64_SEQUENCE_BUDGET={len(counts.get('sequences', []))}"])
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


def setup_build(cfg, root, jobs, kind):
    # Setup and candidate builds have separate budgets. Keep setup invocations
    # visible even if Ninja could reuse an existing native library.
    path = root / "setup-builds.json"
    log = read_json(path) if path.exists() else []
    row = dict(kind=kind, status="started")
    log.append(row)
    write_json(path, log)
    start = time.monotonic()
    try:
        options = {"profile": True} if kind == "profile" else {}
        result = build(cfg, root / kind, root / (kind + "-logs"), jobs, **options)
        row["status"] = "complete"
        return result
    finally:
        row["seconds"] = time.monotonic() - start
        write_json(path, log)


def capture_training(cfg, root, jobs):
    captures = cfg.get("capture_source", root) / "captures"
    outstanding = [case for case in cfg["workloads"]
                   if not (captures / case["id"] / "verified.json").is_file()]
    if outstanding and "capture_source" in cfg:
        raise ValueError(f"incomplete capture source: {captures}")
    if not outstanding:
        return captures
    library = setup_build(cfg, root, jobs, "profile")
    cli = stage_cli(cfg["managed_cli"], root / "profile-cli", library)
    with benchmark_lock(cfg["benchmark_lock"]):
        for case in outstanding:
            directory = captures / case["id"]
            directory.mkdir(parents=True, exist_ok=True)
            run_workloads(cfg, cli, [case], directory / "guest.log", directory)
            write_json(directory / "verified.json", {"blocks_sha256": file_digest(directory / "blocks.bin")})
            print(f"Captured {case['id']}", flush=True)
    return captures


def archive_linked_trial(result, directory, library):
    # Selections are deduplicated across tiers. A size underestimate can produce
    # a useful large-tier library during an attempt targeted at a smaller tier.
    requested = result["tier"]
    result["requested_tier"] = requested
    tier = next((t for t in TIERS if t >= requested and result["text_delta_bytes"] <= t * MIB), None)
    if tier is None:
        result["error"] = "over text growth budget"
        return
    destination = directory / library.name
    shutil.copy2(library, destination)
    result.update(tier=tier, library=str(destination), library_sha256=file_digest(destination))


def compile_trial(cfg, root, pools, result, jobs, base_text):
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
        result["text_delta_bytes"] = result["text_bytes"] - base_text
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
        archive_linked_trial(result, directory, library)
    except (OSError, RuntimeError, subprocess.SubprocessError) as error:
        result["error"] = str(error)
    write_json(directory / "build.json", result)
    print(f"{result['name']} built: {result.get('text_bytes')} bytes, {result.get('error', 'eligible')}", flush=True)
    return result


def score_ratios(samples, workloads):
    cases = {key: median(values) for key, values in samples.items()}
    groups = defaultdict(list)
    for workload in workloads:
        groups[workload.get("family", workload["id"])].append(math.log(cases[workload["id"]]))
    families = {group: math.exp(mean(values)) for group, values in groups.items()}
    return dict(score=math.exp(mean(map(math.log, families.values()))), worst=min(cases.values()),
                worst_family=min(families.values()), families=families, cases=cases, samples=samples)


def measure(cfg, directory, reference_cli, trials, repeat, offset=0):
    directory.mkdir(parents=True, exist_ok=True)
    cases = cfg["workloads"]
    prepared = [(trial, stage_cli(cfg["managed_cli"], directory / trial["name"], trial["library"]))
                for trial in trials if "library" in trial]
    if not prepared:
        return
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
            trial.update(score_ratios(ratios[trial["name"]], cases))
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


def arm_plan_keys(directory):
    plan = read_json(directory / "scalar.json")
    return [
        {row["word"] for row in plan["entries"]},
        set(plan["partials"]),
        {(row["first"], row["second"]) for row in plan["pairs"]},
        {tuple(row["words"]) for row in plan["sequences"]},
    ]


def freeze(root, reference, chain, provenance, aarch64=False):
    with tempfile.TemporaryDirectory(dir=root, prefix=".freezing-") as tmp:
        frozen = Path(tmp)
        destination = root / "frozen"
        names = ("modrm.json", "scalar.json", "fusion-operands.json", "recipes.json", "fusion.cpp")
        rows = {}
        baseline = frozen / "baseline"
        baseline.mkdir()
        baseline_library = baseline / reference.name
        shutil.copy2(reference, baseline_library)
        rows["baseline"] = {"library": str(destination / "baseline" / reference.name), "sha256": file_digest(baseline_library),
                            "text_bytes": chain[0]["text_bytes"] - chain[0]["text_delta_bytes"]}
        previous = None
        for trial in chain:
            name = f"{trial['tier']}m"
            directory = frozen / name
            directory.mkdir()
            source = root / trial["name"]
            keys = arm_plan_keys(source) if aarch64 else plan_keys(source)
            if previous and any(not old <= new for old, new in zip(previous, keys, strict=True)):
                raise RuntimeError(f"{name} dropped bindings from a smaller tier")
            previous = keys
            library = directory / Path(trial["library"]).name
            shutil.copy2(trial["library"], library)
            if file_digest(library) != trial["library_sha256"]:
                raise ValueError(f"{trial['name']}: library changed since the candidate build")
            for filename in names:
                if (source / filename).is_file():
                    target_name = "superopcodes.generated.cpp" if filename == "fusion.cpp" else filename
                    shutil.copy2(source / filename, directory / target_name)
            rows[name] = {**trial, "library": str(destination / name / library.name), "sha256": file_digest(library),
                          "files": {path.name: file_digest(path) for path in sorted(directory.iterdir())},
                          "key_counts": [len(item) for item in keys]}
        shutil.copy2(root / "ranking.json", frozen / "ranking.json")
        write_json(frozen / "manifest.json", {"presets": rows, "training_provenance": provenance})
        frozen.rename(destination)
        return rows


def rank_arm_profiles(profiles, families=None):
    profiles = list(profiles)
    families = list(range(len(profiles))) if families is None else families
    sizes = Counter(families)
    pools = {}
    for group, fields in (("entries", ("word",)), ("pairs", ("first", "second")), ("sequences", ("words",))):
        weights = defaultdict(float)
        short_windows = defaultdict(float)
        for family, profile in zip(families, profiles, strict=True):
            rows = profile.get(group, [])
            total = sum(row["count"] for row in rows)
            for row in rows:
                key = tuple(tuple(row[f]) if f == "words" else row[f] for f in fields)
                weight = row["count"] / (total * sizes[family])
                weights[key] += weight
                if group == "sequences" and profile.get("version") == 2:
                    # Also offer portable inner runs when a build changes surrounding
                    # code. Native profiles only emit legally bakeable maximal runs.
                    words, = key
                    for width in (3, 4):
                        for start in range(len(words) - width + 1):
                            short_windows[words[start:start + width]] += weight
        if short_windows:
            for width in (3, 4):
                shortlist = sorted((w for w in short_windows if len(w) == width),
                                   key=lambda w: (-short_windows[w], w))[:4096]
                for words in shortlist:
                    # The marginal replay later excludes overlap with all longer matches.
                    weights[words,] = short_windows[words]
        def priority(key):
            saved = len(key[0]) - 1 if group == "sequences" else 1
            return -weights[key] * saved, key
        pools[group] = [dict(zip(fields, key), count=max(1, round(weights[key] * 100_000_000)))
                        for key in sorted(weights, key=priority)]
    return pools


def arm_partial_pool(entries, exact, budget):
    counts = {row["word"]: row["count"] for row in entries}
    return aarch64_imm12_shapes(counts, {row["word"] for row in entries[:exact]}, budget)


def arm_plan(pools, counts):
    return {"version": 1, "architecture": "aarch64",
            **{name: pools[name][:count] for name, count in zip(ARM_CLASSES, counts, strict=True)}}


def compile_arm_trial(cfg, root, pools, result, jobs, base_text):
    directory = root / result["name"]
    directory.mkdir(exist_ok=True)
    plan = arm_plan(pools, result["counts"])
    write_json(directory / "scalar.json", plan)
    result["actual_counts"] = [len(plan[name]) for name in ARM_CLASSES]
    try:
        active = root / f"build-{result['tier']}"
        library = build(cfg, active, directory, jobs, plans=directory)
        result["text_bytes"] = text_size(cfg, library)
        result["text_delta_bytes"] = result["text_bytes"] - base_text
        archive_linked_trial(result, directory, library)
    except (OSError, RuntimeError, subprocess.SubprocessError) as error:
        result["error"] = str(error)
    write_json(directory / "build.json", result)
    print(f"{result['name']} built: {result.get('text_bytes')} bytes, {result.get('error', 'eligible')}", flush=True)
    return result


def collect_arm_profiles(cfg, root, jobs):
    captures = cfg.get("capture_source", root) / "captures"
    missing = [case for case in cfg["workloads"]
               if not (captures / case["id"] / "verified.json").is_file()]
    if missing and "capture_source" in cfg:
        raise ValueError(f"incomplete capture source: {captures}")
    if missing:
        reference = setup_build(cfg, root, jobs, "baseline")
        cfg["_capture_baseline"] = reference
        cli = stage_cli(cfg["managed_cli"], root / "baseline-cli", reference)
        with benchmark_lock(cfg["benchmark_lock"]):
            for case in missing:
                capture = captures / case["id"]
                capture.mkdir(parents=True, exist_ok=True)
                run_workloads(cfg, cli, [case], capture / "guest.log", capture)
                write_json(capture / "verified.json", {"words_sha256": file_digest(capture / "words.json")})
                print(f"Captured {case['id']}", flush=True)
    profiles = {}
    hashes = {}
    for case in cfg["workloads"]:
        capture = captures / case["id"]
        path = capture / "words.json"
        profile = read_json(path)
        checksum = file_digest(path)
        if (profile["architecture"] != "aarch64" or not profile["entries"]
                or read_json(capture / "verified.json")["words_sha256"] != checksum):
            raise ValueError(f"invalid AArch64 capture for {case['id']}")
        profiles[case["id"]] = profile
        hashes[case["id"]] = checksum
    return profiles, hashes


def check_capture_source(cfg, provenance):
    if "capture_source" not in cfg:
        return
    original = read_json(cfg["capture_source"] / "provenance.json")
    if (original.get("config_sha256") != provenance["config_sha256"] or
            any(original["profiles"].get(case) != checksum for case, checksum in provenance["profiles"].items())):
        raise ValueError("external captures differ from the specified workloads or recorded hashes")


def train_aarch64(cfg, root, jobs, max_builds, partial, config_hash):
    partial_budget = cfg.get("aarch64_partial_budget", 1024) if partial else 0
    if type(partial_budget) is not int or partial_budget < 0:
        raise ValueError("aarch64_partial_budget must be a nonnegative integer")
    profiles, hashes = collect_arm_profiles(cfg, root, jobs)
    provenance = {"config_sha256": config_hash, "text_growth_tiers_mib": list(TIERS),
                  "profiles": hashes, "aarch64_partial_budget": partial_budget}
    provenance["search"] = cfg["search"]
    provenance["trainer_sha256"] = cfg["trainer_sha256"]
    check_capture_source(cfg, provenance)
    meta_file, pool_file = root / "provenance.json", root / "pools.json"
    if meta_file.is_file() and read_json(meta_file) != provenance:
        raise ValueError("training workloads or captures differ from the previous run")
    if not pool_file.is_file() or not meta_file.is_file():
        pools = rank_arm_profiles(profiles.values(), [c.get("family", c["id"]) for c in cfg["workloads"]])
        # Keep one ranking of residual shapes so larger tiers can retain the smaller prefixes.
        pools["partials"] = arm_partial_pool(pools["entries"], 128, partial_budget)
        write_json(pool_file, pools)
    write_json(meta_file, provenance)
    ranked_presets(cfg, root, jobs, max_builds, read_json(pool_file), provenance, profiles, aarch64=True)


def train_i386(cfg, root, jobs, max_builds, partial, config_hash):
    captures = capture_training(cfg, root, jobs)
    provenance = {"config_sha256": config_hash, "text_growth_tiers_mib": list(TIERS), "profiles": {
        case["id"]: file_digest(captures / case["id"] / "blocks.bin") for case in cfg["workloads"]}}
    for case, checksum in provenance["profiles"].items():
        if read_json(captures / case / "verified.json")["blocks_sha256"] != checksum:
            raise ValueError(f"{case}: i386 capture checksum mismatch")
    if partial:
        provenance["partial_operands"] = True
    provenance["search"] = cfg["search"]
    provenance["trainer_sha256"] = cfg["trainer_sha256"]
    check_capture_source(cfg, provenance)
    meta_file, pool_file = root / "provenance.json", root / "pools.json"
    if meta_file.is_file() and read_json(meta_file) != provenance:
        raise ValueError("training workloads or captures differ from the previous run")
    if not pool_file.is_file() or not meta_file.is_file():
        write_json(pool_file, build_pools(cfg["workloads"], captures, include_partial=partial))
    pools = read_json(pool_file)
    write_json(meta_file, provenance)
    ranked_presets(cfg, root, jobs, max_builds, pools, provenance, captures)


def ranked_presets(cfg, root, jobs, max_builds, pools, provenance, source, aarch64=False):
    search = cfg["search"]
    reference = cfg.pop("_capture_baseline", None) or setup_build(cfg, root, jobs, "baseline")
    base_text = text_size(cfg, reference)
    classes = ARM_CLASSES if aarch64 else (*CLASSES, *(k for k in PARTIAL_FIELDS if k in pools))
    replay = Replay(classes, pools, cfg["workloads"], source, aarch64)
    rank_path = root / "ranking.json"
    pools_sha = file_digest(root / "pools.json")
    if rank_path.exists():
        ranking = read_json(rank_path)
        if ranking["pools_sha256"] != pools_sha:
            raise ValueError("stored ranking refers to a different candidate pool")
    else:
        if cfg.get("cost_cache"):
            costs, reserve, diagnostics = rank_costs(cfg["cost_cache"], cfg["guest"], classes, pools,
                                                     root / "baseline")
        else:
            costs = [PRIORS[classes[kind]] for kind, _ in replay.rows]
            reserve = 0
            diagnostics = {"model": "uncalibrated class priors"}
        budgets = [max(0, int(t * MIB) - reserve) for t in TIERS]
        if not all(b > 0 for b in budgets):
            raise ValueError("size-model reserve consumes the smallest requested tier")
        start = time.monotonic()
        ranking = joint_rank(replay, costs, budgets, depth=search["depth"], risk=search["risk"])
        ranking.update(search_seconds=time.monotonic()-start, method=search["algorithm"], guest=cfg["guest"],
                       pools_sha256=pools_sha, cost_model=diagnostics, reserve_bytes=reserve,
                       estimated_budgets=budgets, tiers_mib=list(TIERS))
        for entry in ranking["entries"]:
            kind, row = replay.rows[entry["id"]]
            name = classes[kind]
            binding = pools[name][row]
            if isinstance(binding, dict):
                binding = {k: v for k, v in binding.items() if k not in ("count", "estimated_visits", "active_groups")}
            entry.update(kind=name, binding=binding, estimated_first_tier=next(
                (tier for tier, cut in zip(TIERS, ranking["cuts"]) if entry["rank"] <= cut), None))
        write_json(rank_path, ranking)
    print(f"One ranking for {len(ranking['entries'])} candidates; cutoffs {ranking['cuts']}", flush=True)
    if cfg.get("rank_only"):
        return

    def compile_prefix(trial, selection):
        selected = replay.selected_pools(selection)
        trial.update(selection=sorted(selection), counts=[len(selected[name]) for name in classes])
        compile_candidate = compile_arm_trial if aarch64 else compile_trial
        compile_candidate(cfg, root, selected, trial, jobs, base_text)

    chain, history = materialize(root, ranking, TIERS, max_builds, compile_prefix)
    unique = {row["name"]: row.copy() for row in chain}
    reference_cli = stage_cli(cfg["managed_cli"], root / "baseline-cli", reference)
    measure(cfg, root / "ranked-verification", reference_cli, list(unique.values()), cfg.get("final_repeats", 3))
    verified = []
    for row in chain:
        measured = unique[row["name"]]
        if measured.get("error") or measured.get("worst_family", 0) < search["min_family_ratio"]:
            raise RuntimeError(f"{row['name']} failed final workload verification")
        verified.append(measured | {"tier": row["tier"]})
    budget = dict(candidate_builds=len(history), max_builds=max_builds, unbaked_text_bytes=base_text,
                  setup_build_invocations=read_json(root / "setup-builds.json"))
    record = dict(sha256=file_digest(rank_path), estimated_cuts=ranking["cuts"],
                  verified_cuts=[r["cut"] for r in verified], offline_seconds=ranking["search_seconds"])
    write_json(root / "search-summary.json", budget | record)
    freeze(root, reference, verified, provenance | {"ranking": record, "budget": budget}, aarch64)


def guest_configs(cfg, selected=None):
    rootfs = cfg["rootfs"]
    if isinstance(rootfs, dict):
        if set(rootfs) != {"i386", "aarch64"} or any(
            not isinstance(path, str) or not path for path in rootfs.values()
        ):
            raise ValueError("shared rootfs must map i386 and aarch64 to their rootfs paths")
        if "guest" in cfg:
            raise ValueError("use --guest to select from a shared rootfs configuration")
        guests = ("i386", "aarch64") if selected in (None, "both") else (selected,)
        return [cfg | {"guest": guest, "rootfs": rootfs[guest]} for guest in guests]

    guest = cfg.get("guest", "i386")
    if guest not in ("i386", "aarch64"):
        raise ValueError(f"unknown guest {guest}")
    if selected and selected != guest:
        raise ValueError("selecting another guest requires a shared rootfs configuration")
    return [cfg | {"guest": guest}]


def load_training_config(path):
    cfg = read_json(path)
    config_hash = file_digest(path)
    if isinstance(cfg["workloads"], str):
        manifest = path.parent / cfg["workloads"]
        cfg["workloads"] = read_json(manifest)
        config_hash = digest((config_hash + file_digest(manifest)).encode())
    if not isinstance(cfg["workloads"], list) or not cfg["workloads"]:
        raise ValueError("workloads must be a nonempty array of workload definitions")
    return cfg, config_hash


def trainer_fingerprint():
    # Stable per checkout, including uncommitted changes to training and native code.
    paths = sorted(p for base in ("libfibercpu/src", "libfibercpu/cmake", "scripts")
                   for p in (REPO / base).rglob("*") if p.is_file() and p.suffix in
                   (".cpp", ".h", ".inc", ".txt", ".py", ".cmake", ".json", ".def"))
    paths.append(REPO / "libfibercpu/CMakeLists.txt")
    return digest("".join(f"{p.relative_to(REPO)} {file_digest(p)}\n" for p in paths).encode())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--guest", choices=("i386", "aarch64", "both"),
                        help="Guest to train when the config has both rootfs paths (default: both)")
    builds = parser.add_mutually_exclusive_group()
    builds.add_argument("--max-builds", type=int, help="Lifetime candidate build attempts per guest (default 6)")
    builds.add_argument("--rounds", type=int, help="Compatibility spelling for three candidate builds per round")
    parser.add_argument("--algorithm", choices=(RANKING_METHOD,), default=RANKING_METHOD)
    parser.add_argument("--search-depth", type=int, default=1, help="Number of intermediate marginal-price refreshes per tier (default 1)")
    parser.add_argument("--rank-cost-cache", type=Path, help="Joint ranking cost JSON or directory with <guest>.json")
    parser.add_argument("--rank-only", action="store_true", help="Write the joint ranking before linking any preset")
    parser.add_argument("--risk", type=float, default=.5, help="Family concentration and specificity discount (0..1, default 0.5)")
    parser.add_argument("--min-family-ratio", type=float, default=0.,
                        help="Minimum repeated finalist family speedup vs unbaked (0 disables)")
    parser.add_argument("--holdout-family", action="append", default=[], help="Reserve a whole software family (repeatable)")
    parser.add_argument("--capture-source", type=Path, help="Reuse verified captures from a prior trainer output")
    parser.add_argument("--partial-operands", action=argparse.BooleanOptionalAction, default=True,
                        help="Include scalar forms with dynamic displacement or immediate (default)")
    parser.add_argument("--jobs", type=int, default=14, help="Parallel compiler jobs per candidate")
    args = parser.parse_args()
    max_builds = args.max_builds if args.max_builds is not None else 3 * (2 if args.rounds is None else args.rounds)
    if min(max_builds, args.jobs, args.search_depth) < 1 or not 0 <= args.risk <= 1:
        parser.error("build count, depth and jobs must be positive; risk must be in [0, 1]")
    if not math.isfinite(args.min_family_ratio) or args.min_family_ratio < 0:
        parser.error("--min-family-ratio must be finite and nonnegative")
    try:
        cfg, config_hash = load_training_config(args.config)
        configurations = guest_configs(cfg, args.guest)
        training, reserved = split_families(cfg["workloads"], args.holdout_family)
        if type(cfg.get("final_repeats", 3)) is not int or cfg.get("final_repeats", 3) < 1:
            raise ValueError("final_repeats must be a positive integer")
    except (OSError, ValueError) as error:
        parser.error(str(error))

    root = args.output.resolve()
    shared = isinstance(cfg["rootfs"], dict)
    runs = [(config, root / config["guest"] if shared else root) for config in configurations]
    fingerprint = trainer_fingerprint()
    for config, directory in runs:
        guest = config["guest"]
        config["workloads"] = training
        config["trainer_sha256"] = fingerprint
        config["search"] = dict(algorithm=args.algorithm, depth=args.search_depth, guest=guest,
                                risk=args.risk, min_family_ratio=args.min_family_ratio,
                                train_cases=training, holdout_cases=reserved)
        config["rank_only"] = args.rank_only
        if args.rank_cost_cache:
            cache = args.rank_cost_cache.resolve()
            cache = cache / f"{guest}.json" if cache.is_dir() else cache
            if not cache.is_file():
                parser.error(f"missing rank cost cache: {cache}")
            config["cost_cache"] = cache
            config["search"]["cost_cache_sha256"] = file_digest(cache)
        if args.capture_source:
            source = args.capture_source.resolve()
            config["capture_source"] = source / guest if shared else source
        directory.mkdir(exist_ok=True, parents=True)
        print(f"Training {guest} with {len(config['workloads'])} workloads", flush=True)
        trainer = train_aarch64 if guest == "aarch64" else train_i386
        # Serialize trainers sharing an output root; compare identity before touching
        # any capture, pool or interrupted candidate from an earlier invocation.
        with benchmark_lock(directory / ".trainer.lock"):
            identity = dict(config_sha256=config_hash, partial_operands=args.partial_operands,
                            search=config["search"], sources=fingerprint)
            identity_file = directory / "search-config.json"
            if identity_file.exists():
                if read_json(identity_file) != identity:
                    parser.error(f"{directory}: configuration, sources or search algorithm changed; use a new output")
            elif any((directory / name).exists() for name in ("captures", "pools.json", "rank-builds.json", "rounds.json")):
                parser.error(f"{directory}: older trainer output; reuse verified data through --capture-source")
            if (directory / "frozen").exists() and not identity_file.exists():
                parser.error(f"{directory} already contains frozen presets from another run")
            archive = directory / "frozen"
            if archive.exists():
                print(f"{guest}: already frozen at the requested budget in {directory}", flush=True)
                continue
            write_json(identity_file, identity)
            trainer(config, directory, args.jobs, max_builds, args.partial_operands, config_hash)


if __name__ == "__main__":
    main()
