"""Materialize byte cutoffs of one immutable ranking, including interrupted or oversize links."""

from bisect import bisect_right
from hashlib import sha256
from pathlib import Path
from time import monotonic

from nested_baking_workloads import file_digest, read_json, write_json


def trim_prefix(entries, boundaries, cut, floor, limit, actual):
    """Use the observed excess to move to an earlier dependency-closed boundary."""
    positions = [n for n in boundaries if floor <= n < cut]
    if not positions:
        raise ValueError("no smaller dependency-closed prefix")
    predicted = entries[cut - 1]["cumulative_bytes"]
    target = predicted * limit / actual * .97
    sizes = [entries[n - 1]["cumulative_bytes"] if n else 0 for n in positions]
    return positions[max(0, bisect_right(sizes, target) - 1)]


def materialize(root, ranking, tiers, max_builds, compile_prefix):
    """Keep a single build ledger across all cutoffs. Never consult timings here."""
    entries = ranking["entries"]
    cuts = ranking["cuts"]
    if len(cuts) != len(tiers) or cuts != sorted(cuts):
        raise ValueError("invalid ranked tier cutoffs")
    boundaries = [0, *(n for n, e in enumerate(entries, 1) if e["group_end"])]
    if any(cut not in boundaries for cut in cuts):
        raise ValueError("ranked cutoff splits a dependency group")
    ledger = root / "rank-builds.json"
    history = read_json(ledger) if ledger.exists() else []
    order_hash = sha256(",".join(str(entry["id"]) for entry in entries).encode()).hexdigest()
    if any(row["order_sha256"] != order_hash for row in history):
        raise ValueError("stored builds refer to a different candidate order")
    if len(history) > max_builds:
        raise ValueError("existing attempts exceed --max-builds")
    for row in history:
        if row["status"] == "reserved":
            record = root / row["name"] / "build.json"
            if record.exists():
                recovered = read_json(record)
                if (recovered["cut"], recovered["name"], recovered["order_sha256"]) != (row["cut"], row["name"], order_hash):
                    raise ValueError(f"archive does not match reserved prefix: {record}")
                row.update(recovered)
            else:
                row["error"] = "interrupted before archive"
            row["status"] = "finished"
            write_json(ledger, history)

    chain = []
    for tier, cut in zip(tiers, cuts, strict=True):
        limit = round(tier * (1 << 20))
        floor = chain[-1]["cut"] if chain else 0
        while True:
            row = next((r for r in history if r["cut"] == cut and "text_delta_bytes" in r
                        and (r.get("library") or r.get("error") == "over text growth budget")), None)
            if row is None:
                if len(history) >= max_builds:
                    raise RuntimeError(f"no verified prefix for {tier} MiB in {max_builds} builds; "
                                       "increase --max-builds to resume the same ranking")
                row = dict(name=f"rank-{len(history)+1:02}-{tier}m", tier=tier, cut=cut, status="reserved",
                           order_sha256=order_hash)
                history.append(row)
                write_json(ledger, history)
                start = monotonic()
                compile_prefix(row, [e["id"] for e in entries[:cut]])
                row.update(status="finished", build_seconds=monotonic() - start)
                write_json(ledger, history)
                print(f"{row['name']} prefix {cut}, linked growth {row.get('text_delta_bytes')}", flush=True)
            if row.get("error") and row["error"] != "over text growth budget":
                raise RuntimeError(f"{row['name']}: {row['error']}")
            if row["text_delta_bytes"] <= limit:
                lib = Path(row["library"])
                if file_digest(lib) != row["library_sha256"]:
                    raise ValueError(f"linked prefix changed: {lib}")
                chain.append(row | {"tier": tier})
                break
            if not cut:
                raise RuntimeError(f"even the empty prefix exceeds {tier} MiB")
            cut = trim_prefix(entries, boundaries, cut, floor, limit, row["text_delta_bytes"])
    return chain, history
