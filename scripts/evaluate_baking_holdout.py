#!/usr/bin/env python3
"""Evaluate all frozen baking tiers on the families reserved before training."""

import argparse
import math
from pathlib import Path
import subprocess

from nested_baking_workloads import file_digest, read_json, split_families, stage_cli, write_json
from train_nested_presets import TIERS, guest_configs, load_training_config, measure


def holdout_config(path, provenance):
    cfg, config_hash = load_training_config(path)
    if config_hash != provenance["config_sha256"]:
        raise ValueError("the supplied configuration or workload manifest differs from training")
    search = provenance.get("search", {})
    guest = search.get("guest")
    if guest not in ("aarch64", "i386"):
        raise ValueError("frozen search provenance must identify a single guest")
    holdout = search.get("holdout_cases")
    if not isinstance(holdout, list) or not holdout:
        raise ValueError("no workload families were held out before training")
    held_families = {c.get("family", c["id"]) for c in holdout}
    training, reserved = split_families(cfg["workloads"], held_families)
    if training != search.get("train_cases") or reserved != holdout:
        raise ValueError("frozen training/holdout split does not match the original workload families")
    if set(provenance["profiles"]) != {case["id"] for case in training}:
        raise ValueError("captured profiles differ from the declared training set")
    selected, = guest_configs(cfg, guest)
    return selected | {"workloads": reserved}


def frozen_artifact(frozen, name, row):
    # A copied archive can have a new root, so resolve inside the frozen tier.
    library = frozen / name / Path(row["library"]).name
    checksum = file_digest(library)
    if checksum != row["sha256"]:
        raise ValueError(f"{name} frozen library checksum mismatch")
    for filename, expected in row.get("files", {}).items():
        if Path(filename).name != filename or file_digest(library.parent / filename) != expected:
            raise ValueError(f"{name} frozen artifact checksum mismatch: {filename}")
    return {"library": str(library), "sha256": checksum, "text_bytes": row["text_bytes"]}


def checked_result(trial, cases, repeats):
    if "error" in trial:
        raise ValueError(f"{trial['name']} failed: {trial['error']}")
    ids = {c["id"] for c in cases}
    families = {c.get("family", c["id"]) for c in cases}
    samples = trial.get("samples", {})
    if (set(samples) != ids or set(trial.get("cases", {})) != ids
            or set(trial.get("families", {})) != families
            or any(len(values) != repeats for values in samples.values())):
        raise ValueError(f"{trial['name']} did not complete every held-out case and repetition")
    values = [trial["score"], trial["worst_family"], *trial["cases"].values(), *trial["families"].values()]
    values.extend(value for sample in samples.values() for value in sample)
    if not all(math.isfinite(x) and x > 0 for x in values):
        raise ValueError(f"{trial['name']} contains invalid timing results")
    return {key: trial[key] for key in ("score", "worst_family", "families", "cases", "samples")}


def evaluate(config, frozen, output, reference_library=None, repeats=3):
    if repeats < 1:
        raise ValueError("repeats must be positive")
    manifest_file = (frozen / "manifest.json" if frozen.is_dir() else frozen).resolve()
    directory = manifest_file.parent
    if output.resolve().is_relative_to(directory):
        raise ValueError("holdout output must be outside the frozen archive")
    manifest = read_json(manifest_file)
    provenance = manifest["training_provenance"]
    cfg = holdout_config(config, provenance)
    names = [f"{tier}m" for tier in TIERS]
    artifacts = {name: frozen_artifact(directory, name, manifest["presets"][name])
                 for name in ("baseline", *names)}
    if reference_library is None:
        reference = {**artifacts["baseline"], "source": "frozen-baseline"}
    else:
        reference = {"library": str(reference_library.resolve()), "sha256": file_digest(reference_library),
                     "source": "external-reference"}
    report = {
        "guest": cfg["guest"], "manifest_sha256": file_digest(manifest_file),
        "config_sha256": provenance["config_sha256"], "repeats": repeats,
        "holdout_cases": cfg["workloads"], "reference": reference, "artifacts": artifacts,
    }
    output.mkdir(parents=True, exist_ok=False)
    try:
        reference_cli = stage_cli(cfg["managed_cli"], output / "reference", reference["library"])
        # A fresh record keeps the training scores in the archive separate from this evaluation.
        trials = [dict(name=name, library=artifacts[name]["library"]) for name in names]
        measure(cfg, output / "timing", reference_cli, trials, repeats)
        results = {trial["name"]: checked_result(trial, cfg["workloads"], repeats) for trial in trials}
    except (OSError, RuntimeError, ValueError, KeyError, subprocess.SubprocessError) as error:
        write_json(output / "report.json", {**report, "status": "failed", "error": str(error)})
        raise
    write_json(output / "report.json", {**report, "status": "complete", "results": results})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, required=True, help="Original training configuration")
    parser.add_argument("--frozen", type=Path, required=True, help="Frozen directory or manifest.json")
    parser.add_argument("--output", type=Path, required=True, help="New directory for timings and report.json")
    parser.add_argument("--reference-library", type=Path, help="Measure against this build (default: frozen baseline)")
    parser.add_argument("--repeats", type=int, default=3)
    args = parser.parse_args()
    try:
        evaluate(args.config, args.frozen, args.output, args.reference_library, args.repeats)
    except (OSError, RuntimeError, ValueError, KeyError, subprocess.SubprocessError) as error:
        parser.exit(1, f"{parser.prog}: {error}\n")


if __name__ == "__main__":
    main()
