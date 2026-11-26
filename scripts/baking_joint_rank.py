"""Offline, dependency-closed ranking for every increment of a baking preset."""

from bisect import bisect_right
from collections import defaultdict, deque
from heapq import heapify, heappop, heappush
from math import fsum, isfinite, log2

from baking_replay import SPECIFICITY


def _robust(gains, risk):
    mean = fsum(gains) / len(gains)
    if not risk or len(gains) == 1:
        return mean
    without_best = (fsum(gains) - max(gains)) / (len(gains) - 1)
    return (1 - risk) * mean + risk * without_best


class _Index:
    """A posting contains all positions of a candidate in one native layout."""

    def __init__(self, replay, costs, risk):
        self.costs = costs
        self.parents = replay.parents
        self.widths = replay.widths
        self.families = replay.families
        self.risk = risk
        n = len(costs)
        kinds = [replay.classes[k] for k, _ in replay.rows]
        self.children = defaultdict(list)
        for i, parent in enumerate(self.parents):
            if parent >= 0:
                self.children[parent].append(i)

        layouts = {}
        for family, histogram in enumerate(replay.layouts):
            for layout, weight in histogram.items():
                if layout not in layouts:
                    layouts[layout] = []
                layouts[layout].append((family, weight))
        self.lengths = []
        self.weights = []
        self.postings = [[] for _ in costs]
        contexts = [set() for _ in costs]
        operands = [set() for _ in costs]
        exact = [k in ('exact', 'entries') for k in kinds]
        for block, (layout, weights) in enumerate(layouts.items()):
            self.lengths.append(len(layout))
            self.weights.append(weights)
            # Exact IDs are stable across families. Similar neighbouring encoded
            # instructions give one context even when several binaries hit libc.
            signatures = [next((i for i in slot if exact[i]), -1) for slot in layout]
            changes = defaultdict(list)
            for pos, options in enumerate(layout):
                for priority, i in enumerate(options):
                    changes[i].append((pos, priority))
                    if len(operands[i]) < 32 and signatures[pos] >= 0:
                        operands[i].add(signatures[pos])
                    if len(contexts[i]) < 32:
                        end = pos + self.widths[i]
                        left = signatures[pos - 1] if pos else -2
                        right = signatures[end] if end < len(layout) else -2
                        contexts[i].add((left, right))
            for i, edits in changes.items():
                self.postings[i].append((block, edits))

        self.evidence = []
        self.credits = []
        for i in range(n):
            context_count = len(contexts[i])
            operand_count = len(operands[i])
            specificity = SPECIFICITY.get(kinds[i], 1.)
            fragile = specificity + (self.widths[i] - 1) / 4
            fragile /= 1 + log2(max(1, context_count)) / 4
            if self.widths[i] == 1 and not exact[i]:
                fragile /= 1 + log2(max(1, operand_count)) / 2
            transfer = 1 / (1 + risk * fragile)
            self.credits.append(replay.units[i] * transfer)
            self.evidence.append(dict(contexts_capped32=context_count,
                                      exact_shapes_capped32=operand_count,
                                      transfer_factor=transfer))

    def group(self, chosen, i):
        parent = self.parents[i]
        return (parent, i) if parent >= 0 and not chosen[parent] else (i,)


class _State:
    """Trial additions copy only the per-block changes, then restore the dispatch slots."""

    def __init__(self, index):
        self.index = index
        self.chosen = bytearray(len(index.costs))
        self.dispatch = [[-1] * n for n in index.lengths]
        self.priority = [[len(index.costs) + 1] * n for n in index.lengths]
        self.values = [0.] * len(index.lengths)
        self.calls = 0
        self.block_visits = 0

    def change(self, group, commit=False):
        idx = self.index
        self.calls += 1
        changed = {}
        for i in group:
            for block, edits in idx.postings[i]:
                slots, ranks = self.dispatch[block], self.priority[block]
                before = None
                for pos, priority in edits:
                    if priority < ranks[pos]:
                        if before is None:
                            before = changed.setdefault(block, [])
                        before.append((pos, ranks[pos], slots[pos]))
                        ranks[pos], slots[pos] = priority, i
        gains = [0.] * len(idx.families)
        self.block_visits += len(changed)
        for block, before in changed.items():
            slots = self.dispatch[block]
            pos = 0
            total = 0.
            while pos < len(slots):
                i = slots[pos]
                if i < 0:
                    pos += 1
                else:
                    total += idx.credits[i]
                    pos += idx.widths[i]
            delta = total - self.values[block]
            for family, weight in idx.weights[block]:
                gains[family] += weight * delta
            if commit:
                self.values[block] = total
            else:
                ranks = self.priority[block]
                for pos, priority, i in reversed(before):
                    ranks[pos], slots[pos] = priority, i
        if commit:
            for i in group:
                self.chosen[i] = 1
        return gains


def _greedy(index, budgets, depth):
    # Dispatch priorities and shared parents can increase a candidate's marginal
    # density. Stale heap keys therefore form a shortlist, not certified upper
    # bounds. Refresh all remaining keys at the byte checkpoints below.
    state = _State(index)
    initial = []
    group_values = []
    n = len(index.costs)
    empty = state.chosen
    for i in range(n):
        value = _robust(state.change((i,)), index.risk)
        initial.append(value)
        group = index.group(empty, i)
        group_values.append(value if len(group) == 1 else _robust(state.change(group), index.risk))
    scores = []
    queue = []

    def price(i):
        group = index.group(state.chosen, i)
        cost = sum(index.costs[j] for j in group)
        if cost > budgets[-1]:
            return 0.
        return _robust(state.change(group), index.risk) / cost

    def refresh():
        queue.clear()
        for i in range(n):
            if not state.chosen[i]:
                density = price(i)
                scores[i] = density
                queue.append((-density, i, stamp))
        heapify(queue)

    for i in range(n):
        cost = sum(index.costs[j] for j in index.group(empty, i))
        density = group_values[i] / cost if cost <= budgets[-1] else 0.
        scores.append(density)
        queue.append((-density, i, 0))
    heapify(queue)
    stamp = 0
    groups = []
    spent = 0
    # Depth 1 refreshes at each tier midpoint and cutoff. Finer intervals
    # revisit stale marginals more frequently without requiring another link.
    intervals = min(depth, max(1, n)) + 1
    checkpoints = deque(sorted({int(cap * step // intervals) for cap in budgets
                                for step in range(1, intervals)} | set(budgets[:-1])))
    refreshed = 0
    while queue and spent < budgets[-1]:
        negative, i, evaluated = heappop(queue)
        if state.chosen[i] or -negative != scores[i]:
            continue
        if evaluated != stamp:
            updated = price(i)
            scores[i] = updated
            if queue and updated < -queue[0][0]:
                heappush(queue, (-updated, i, stamp))
                continue
            negative = -updated
        if negative >= 0:
            if refreshed != stamp:
                refresh()
                refreshed = stamp
                continue
            break
        group = index.group(state.chosen, i)
        groups.append(group)
        spent += sum(index.costs[j] for j in group)
        state.change(group, commit=True)
        stamp += 1
        for j in group:
            for child in index.children.get(j, ()):
                if not state.chosen[child]:
                    density = price(child)
                    scores[child] = density
                    heappush(queue, (-density, child, stamp))
        if checkpoints and spent >= checkpoints[0]:
            while checkpoints and spent >= checkpoints[0]:
                checkpoints.popleft()
            refresh()
            refreshed = stamp
    active = len(groups)
    # Keep every candidate addressable, including unprofitable rows. These rows
    # are scored on insertion for auditing but cannot fill an unused tier budget.
    for i in sorted(range(n), key=lambda j: (-initial[j] / index.costs[j], j)):
        if not state.chosen[i]:
            group = index.group(state.chosen, i)
            groups.append(group)
            for j in group:
                state.chosen[j] = 1
    return groups, active, state.calls, state.block_visits


def _cut_groups(groups, active, costs, budgets):
    ends = []
    used = 0
    for group in groups[:active]:
        used += sum(costs[i] for i in group)
        ends.append(used)
    return tuple(bisect_right(ends, cap) for cap in budgets)


def joint_rank(replay, costs, budgets, *, depth=1, risk=0.5):
    """Return one full ranked list, its eligible prefix lengths, and offline diagnostics."""
    if not budgets or any(not isfinite(b) or b <= 0 for b in budgets) or list(budgets) != sorted(set(budgets)):
        raise ValueError('joint ranking requires positive increasing budgets')
    if len(costs) != len(replay.rows) or any(not isfinite(c) or c <= 0 for c in costs):
        raise ValueError('every candidate requires a finite positive cost')
    if type(depth) is not int or depth < 1 or not isfinite(risk) or not 0 <= risk <= 1:
        raise ValueError('invalid search depth or family risk')
    index = _Index(replay, costs, risk)
    groups, active, calls, visits = _greedy(index, budgets, depth)
    group_cuts = _cut_groups(groups, active, costs, budgets)
    cuts = [sum(map(len, groups[:cut])) for cut in group_cuts]

    state = _State(index)
    entries = []
    spent = 0
    totals = [0.] * len(index.families)
    cutoff_set = set(cuts)
    scores = {0: 0.}
    for group in groups:
        for i in group:
            gains = state.change((i,), commit=True)
            gain = _robust(gains, index.risk)
            spent += costs[i]
            rank = len(entries) + 1
            entries.append(dict(id=i, rank=rank, group_end=i == group[-1], cost=costs[i],
                                cumulative_bytes=spent, gain_at_insert=gain,
                                families=dict(zip(index.families, gains)), evidence=index.evidence[i]))
            totals = [a + b for a, b in zip(totals, gains)]
            if rank in cutoff_set:
                scores[rank] = _robust(totals, index.risk)
    tier_values = [scores[cut] for cut in cuts]
    return dict(entries=entries, cuts=cuts, objective=fsum(tier_values) / len(budgets),
                stats=dict(marginal_evaluations=calls, affected_blocks=visits, tier_values=tier_values))
