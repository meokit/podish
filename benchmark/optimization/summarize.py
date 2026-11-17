#!/usr/bin/env python3
"""Summarize per-workload throughput; keep paired ranges visible instead of a geomean."""
import argparse
import json
import pathlib
import statistics

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('input', type=pathlib.Path)
parser.add_argument('--baseline', default='base')
args = parser.parse_args()
results = json.loads(args.input.read_text())
assert all(row['validated'] for row in results)
for case in dict.fromkeys(row['case'] for row in results):
    variants = list(dict.fromkeys(row['variant'] for row in results if row['case'] == case))
    samples = {variant: {row['round']: row.get('score', row['iterations']/row['seconds']) for row in results if row['case'] == case and row['variant'] == variant} for variant in variants}
    baseline = samples[args.baseline]
    base_median = statistics.median(baseline.values())
    print(case)
    for variant in variants:
        values = samples[variant]
        median = statistics.median(values.values())
        paired = [100*(value/baseline[round_index]-1) for round_index, value in values.items() if round_index in baseline]
        print(f'  {variant:14s} n={len(values)} median={median:.6f} delta={100*(median/base_median-1):+.3f}% paired={min(paired):+.3f}..{max(paired):+.3f}%')
