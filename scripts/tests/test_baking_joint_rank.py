import sys
import unittest
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from baking_joint_rank import joint_rank


def profile(units, histograms, parents=None, widths=None, kinds=None):
    n = len(units)
    return SimpleNamespace(classes=kinds or ['exact'] * n, rows=list(zip(range(n), range(n))),
                           widths=widths or [1] * n, parents=parents or [-1] * n,
                           families=[f'f{i}' for i in range(len(histograms))],
                           units=units, layouts=histograms)


def order(result):
    return [e['id'] for e in result['entries']]


class JointRankTests(unittest.TestCase):
    def test_native_priority_changes_the_next_selection(self):
        replay = profile([6, 7, 3], [{((1, 0),): 1., ((0,),): 1., ((2,),): 1.}])
        b = joint_rank(replay, [1, 1, 1], [1, 2, 3], risk=0)
        self.assertEqual(order(b), [0, 2, 1])
        self.assertEqual(b['stats']['tier_values'], [12., 15., 16.])
        self.assertEqual(b['entries'][-1]['families'], {'f0': 1.})

    def test_atomic_admission_includes_the_shared_parent_once(self):
        replay = profile([.2, 3, 1.5], [{((1, 0),): 1., ((2,),): 1.}], [-1, 0, -1])
        b = joint_rank(replay, [1, 1, 2], [1, 2, 4], risk=0)
        self.assertEqual(order(b), [0, 1, 2])
        self.assertEqual([e['group_end'] for e in b['entries']], [False, True, True])
        self.assertEqual(b['cuts'], [0, 2, 3])
        self.assertEqual(b['stats']['tier_values'], [0., 3., 4.5])

    def test_single_family_peak_loses_to_transferable_gain(self):
        replay = profile([1, 1], [{((0,),): 1., ((1,),): .5}, {((1,),): .5}])
        c = joint_rank(replay, [1, 1], [1])
        self.assertEqual(order(c)[0], 1)
        self.assertEqual(order(joint_rank(replay, [1, 1], [1], risk=0))[0], 0)

    def test_empirical_coverage_deduplicates_shared_contexts(self):
        hist = {((0, 2), (1, 2)): .5}
        replay = profile([1, 1, 1], [hist, hist], kinds=['exact', 'exact', 'partials'])
        entries = {r['id']: r for r in joint_rank(replay, [1] * 3, [3])['entries']}
        evidence = entries[2]['evidence']
        self.assertEqual(evidence['exact_shapes_capped32'], 2)
        self.assertEqual(evidence['contexts_capped32'], 2)
        self.assertGreater(evidence['transfer_factor'], entries[0]['evidence']['transfer_factor'])

    def test_self_overlapping_fusion_and_inactive_tail(self):
        replay = profile([3, 1], [{((0,), (0,), ()): 1.}], widths=[2, 1])
        r = joint_rank(replay, [1, 1], [1, 2], risk=0)
        self.assertEqual(r['cuts'], [1, 1])
        self.assertEqual(r['stats']['tier_values'], [3, 3])
        self.assertEqual([e['cumulative_bytes'] for e in r['entries']], [1, 2])
        self.assertEqual(order(r), [0, 1])

    def test_impossible_bundle_does_not_block_a_smaller_admission(self):
        replay = profile([200., 1.], [{((0,),): 1., ((1,),): 1.}])
        ranked = joint_rank(replay, [100, 1], [1, 2], depth=3)
        self.assertEqual(order(ranked), [1, 0])
        self.assertEqual(ranked['cuts'], [1, 1])

    def test_depth_refreshes_marginal_prices_without_changing_tier_count(self):
        n = 12
        replay = profile([1] * n, [{((i,),): 1. for i in range(n)}])
        default = joint_rank(replay, [1] * n, [3, 6, 12])
        deeper = joint_rank(replay, [1] * n, [3, 6, 12], depth=3)
        self.assertEqual(default['cuts'], deeper['cuts'])
        self.assertEqual(order(default), order(deeper))
        self.assertGreater(deeper['stats']['marginal_evaluations'], default['stats']['marginal_evaluations'])

    def test_rejects_ambiguous_budgets_and_invalid_costs(self):
        replay = profile([1], [{((0,),): 1.}])
        for costs, budgets in [([0], [1]), ([1], [2, 1]), ([1], []), ([], [1]), ([1], [1, 1])]:
            with self.subTest(costs=costs, budgets=budgets), self.assertRaises(ValueError):
                joint_rank(replay, costs, budgets)


if __name__ == '__main__':
    unittest.main()
