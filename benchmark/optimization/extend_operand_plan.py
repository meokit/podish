#!/usr/bin/env python3
"""Append a bounded, workload-balanced GNU shape set to an existing frozen plan."""
import argparse
import collections
import json
import pathlib

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--base', type=pathlib.Path, required=True)
parser.add_argument('--counts', type=pathlib.Path, required=True,
                    help='Encoding-only dispatch estimates with workload/count fields')
parser.add_argument('--output', type=pathlib.Path, required=True)
parser.add_argument('--fused', type=int, default=64)
parser.add_argument('--wrapper', type=int, default=128)
args = parser.parse_args()
if min(args.fused, args.wrapper) < 0:
    parser.error('shape budgets must be nonnegative')
base = json.loads(args.base.read_text())
rows = json.loads(args.counts.read_text())
seen = {(row['original'], tuple(row['shapes'])) for row in base}
scores = collections.Counter()
entries = {}
for workload in sorted({row['workload'] for row in rows}):
    subset = [row for row in rows if row['workload'] == workload]
    total = sum(row['count'] for row in subset)
    if total <= 0:
        continue
    for row in subset:
        if row['kind'] not in ('wrapper', 'fused') or not row['original'].startswith(('DispatchWrapper<op::', 'SuperOpcode_')):
            continue
        key = row['original'], tuple(row['shapes'])
        scores[key] += row['count'] / total
        entries[key] = row
for kind, budget in [('fused', args.fused), ('wrapper', args.wrapper)]:
    candidates = [key for key in scores if key not in seen and entries[key]['kind'] == kind]
    for key in sorted(candidates, key=lambda key: (-scores[key], key))[:budget]:
        base.append({field: entries[key][field] for field in ('original', 'kind', 'width', 'shapes', 'count')})
        seen.add(key)
if len(base) > 1024:
    parser.error('combined plan exceeds the bounded 1024-shape limit')
args.output.write_text(json.dumps(base, indent=2) + '\n')
print(f'Frozen operand plan: {len(base)} shapes')
