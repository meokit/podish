#!/usr/bin/env python3
"""Append workload-balanced encoding-only shapes from decoded v3 training plans."""
import argparse
import collections
import json
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "scripts"))
from operand_specialization_data import Operand, training_plan_match


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base', type=pathlib.Path, required=True, help='Version 3 encoding-only plan')
    parser.add_argument('--counts', type=pathlib.Path, action='append', required=True,
                        help='Version 3 decoded plan for one workload, repeatable')
    parser.add_argument('--output', type=pathlib.Path, required=True)
    parser.add_argument('--fused', type=int, default=64)
    parser.add_argument('--wrapper', type=int, default=128)
    args = parser.parse_args()
    if min(args.fused, args.wrapper) < 0:
        parser.error('shape budgets must be nonnegative')

    try:
        base = json.loads(args.base.read_text())
        if training_plan_match(base) != "encoding":
            raise ValueError("--base must be an encoding-only plan")
        scores = collections.Counter()
        counts = collections.Counter()
        for path in args.counts:
            workload = json.loads(path.read_text())
            if training_plan_match(workload) != "decoded":
                raise ValueError(f"{path}: --counts must contain a decoded plan")
            dispatches = collections.Counter()
            for row in workload["entries"]:
                count = row["dispatch_count"]
                if count > 0:
                    shapes = tuple(Operand(**fields).shape_key() for fields in row["operands"])
                    dispatches[row["kind"], row["originalId"], shapes] += count
            if dispatches:
                total = sum(dispatches.values())
                for key, count in dispatches.items():
                    scores[key] += count / total
                counts.update(dispatches)

        entries = base["entries"]
        seen = {(row["kind"], row["originalId"], tuple(row["shapeKeys"])) for row in entries}
        for kind, budget in [("fused", args.fused), ("wrapper", args.wrapper)]:
            candidates = [key for key in scores if key[0] == kind and key not in seen]
            for key in sorted(candidates, key=lambda key: (-scores[key], key))[:budget]:
                _, op_id, shapes = key
                entries.append(dict(originalId=op_id, kind=kind, width=len(shapes), shapeKeys=shapes, count=counts[key]))
        if len(entries) > 1024:
            raise ValueError("combined plan exceeds the bounded 1024-shape limit")
        args.output.write_text(json.dumps(base, indent=2) + "\n")
    except (OSError, ValueError, TypeError, KeyError) as error:
        parser.error(str(error))
    print(f"Frozen operand plan: {len(entries)} shapes")


if __name__ == "__main__":
    main()
