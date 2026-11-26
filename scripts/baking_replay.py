"""Build overlap-aware per-family utility from ordered, execution-weighted block profiles."""

from collections import Counter, defaultdict
from math import fsum

from nested_baking_candidates import modrm_key


def arm_layouts(pools, ids, cases, profiles):
    exact = {row["word"]: i for row, i in zip(pools["entries"], ids["entries"], strict=True)}
    partial = dict(zip(pools["partials"], ids["partials"], strict=True))
    # The native resolver tries the longest sequence at each dispatch, then the pair.
    trie = {}
    for kind in ("pairs", "sequences"):
        for row, index in zip(pools[kind], ids[kind], strict=True):
            node = trie
            words = row["words"] if kind == "sequences" else (row["first"], row["second"])
            for word in words:
                node = node.setdefault(word, {})
            node[None] = index

    for case in cases:
        profile = profiles[case["id"]]
        if profile.get("version") != 2 or not profile.get("blocks"):
            raise ValueError(f"{case['id']}: ordered AArch64 blocks required; recapture with profile v2")
        for block in profile["blocks"]:
            words = block["words"]
            if not 0 < len(words) <= 24 or block["count"] <= 0:
                raise ValueError("invalid AArch64 weighted block")
            positions = []
            for start, word in enumerate(words):
                matches = []
                node = trie
                for pos in range(start, len(words)):
                    node = node.get(words[pos])
                    if node is None:
                        break
                    if None in node:
                        matches.append(node[None])
                matches.reverse()
                if word in exact:
                    matches.append(exact[word])
                shape = word & ~(0xfff << 10)
                if shape in partial:
                    matches.append(partial[shape])
                positions.append(tuple(matches))
            yield case["id"], block["count"], tuple(positions)


# Normalize scalar and fusion profile utility to estimated dispatches saved.
SCALAR_CREDIT = {"modrm": .12, "policies": .20, "exact": .35, "entries": .35,
                 "partials": .23, "dynamic-both": .20, "dynamic-imm": .26, "dynamic-disp": .26}
SPECIFICITY = {"modrm": .0, "policies": .25, "exact": 1., "entries": 1.,
               "partials": .4, "dynamic-both": .35, "dynamic-imm": .6, "dynamic-disp": .6}


class Replay:
    def __init__(self, classes, pools, cases, source, aarch64):
        self.classes = classes
        self.pools = pools
        self.ids = {}
        self.rows = []
        self.widths = []
        self.parents = []
        self.units = []
        parent_ids = {}
        for kind, name in enumerate(classes):
            ids = self.ids[name] = []
            for row_index, row in enumerate(pools[name]):
                index = len(self.rows)
                ids.append(index)
                if name == "modrm":
                    parent_ids[modrm_key(row)] = index
                self.parents.append(parent_ids[modrm_key(row)] if name == "policies" else -1)
                self.rows.append((kind, row_index))
                width = (len(row["words"]) if name == "sequences" else
                         2 if name == "pairs" else row["width"] if name == "fusion" else 1)
                self.widths.append(width)
                self.units.append(SCALAR_CREDIT.get(name, width - 1))

        if aarch64:
            stream = arm_layouts(pools, self.ids, cases, source)
        else:
            from baking_replay_i386 import layouts
            stream = layouts(pools, self.ids, cases, source)
        histograms = defaultdict(Counter)
        totals = Counter()
        for case, count, positions in stream:
            totals[case] += count * len(positions)
            if any(positions):
                histograms[case][positions] += count

        self.families = sorted({case.get("family", case["id"]) for case in cases})
        self.layouts = [Counter() for _ in self.families]
        case_counts = Counter(case.get("family", case["id"]) for case in cases)
        for case in cases:
            cid = case["id"]
            family = case.get("family", cid)
            if not totals[cid]:
                raise ValueError(f"{cid}: no executed instructions")
            weight = 1 / (totals[cid] * case_counts[family])
            histogram = self.layouts[self.families.index(family)]
            for layout, visits in histograms.pop(cid, {}).items():
                histogram[layout] += weight * visits

    def by_family(self, selected):
        """Replay a fixed selection using raw dispatch credit for a per-family audit."""
        gains = {}
        for family, histogram in zip(self.families, self.layouts, strict=True):
            gain = 0.
            for positions, weight in histogram.items():
                cursor = 0
                while cursor < len(positions):
                    for index in positions[cursor]:
                        if index in selected:
                            gain += self.units[index] * weight
                            cursor += self.widths[index]
                            break
                    else:
                        cursor += 1
            gains[family] = gain
        return gains

    def score(self, selected):
        return fsum(self.by_family(selected).values()) / len(self.families)

    def selected_pools(self, selected):
        result = {name: [] for name in self.classes}
        for index in sorted(selected):
            kind, row = self.rows[index]
            name = self.classes[kind]
            result[name].append(self.pools[name][row])
        return result
