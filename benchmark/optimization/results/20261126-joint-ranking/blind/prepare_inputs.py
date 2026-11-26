#!/usr/bin/env python3
"""Regenerate the blind inputs and independently check every expected result."""
import argparse
import gzip
import hashlib
import io
import json
from pathlib import Path
import random


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    destination = parser.parse_args().destination
    lock_dir = Path(__file__).resolve().parent
    lock = json.loads((lock_dir / "preregistered.json").read_bytes())
    manifest = (lock_dir / "workloads.json").read_bytes()
    if hashlib.sha256(manifest).hexdigest() != lock["workloads_sha256"]:
        raise ValueError("blind manifest checksum mismatch")
    rng = random.Random(0x5A17DE42)
    rows = []
    counts = [0] * 97
    sums = [0] * 97
    for i in range(32768):
        group = rng.randrange(97)
        amount = rng.randrange(8192)
        token = "".join(rng.choices("abcdefghjkmnpqrstuvwxyz23456789", k=17))
        rows.append(f"{i:05d}|{group:02d}|{amount:04d}|{token}")
        if amount % 3:
            counts[group] += 1
            sums[group] += amount
    records = ("\n".join(rows) + "\n").encode("ascii")
    outputs = {
        "blind-perl-groups": "".join(f"{g:02d} {counts[g]} {sums[g]}\n" for g in range(97)).encode("ascii"),
        "blind-rev-lines": ("\n".join(row[::-1] for row in rows) + "\n").encode("ascii"),
        "codegen-gzip-o1": records,
        "codegen-gzip-o3": records,
    }
    for case in json.loads(manifest):
        if hashlib.sha256(outputs[case["id"]]).hexdigest() != case["sha256"]:
            raise ValueError(f"{case['id']}: reference output checksum mismatch")
    compressed = io.BytesIO()
    with gzip.GzipFile(filename="", mode="wb", fileobj=compressed, compresslevel=6, mtime=0) as archive:
        archive.write(records)
    destination.mkdir(parents=True, exist_ok=True)
    for name, data in {"records.txt": records, "records.gz": compressed.getvalue()}.items():
        if hashlib.sha256(data).hexdigest() != lock["inputs"][name]:
            raise ValueError(f"{name}: generated input checksum mismatch")
        (destination / name).write_bytes(data)


if __name__ == "__main__":
    main()
