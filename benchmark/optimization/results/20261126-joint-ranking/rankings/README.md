# Per-candidate ranking data

Each compressed JSON is the trainer's original ranking for one guest and one algorithm. A is standalone gain per byte, B uses marginal replay, C adds family robustness and specificity discounts, and D searches near C's three cutoffs at depth 3. The two cost-cache files contain the toolchain signature, measured binding spans and cross-validation residuals used in the experiment.

To read the top ten AArch64 candidates from method C:

```sh
xz -dc aarch64-C.json.xz | jq '.entries[:10] | map({rank,kind,binding,cost,gain_at_insert,families})'
```

The rank is one-based. Estimated and verified cutoffs are prefix lengths. A shared dependency immediately precedes the candidate that first paid for it when they were admitted together. `group_end` identifies a legal cutoff. A candidate outside the largest selected prefix still has an audit entry. The per-entry gain is computed in insertion order and can be negative. Estimated first tier comes from the pre-link budget. Consult the verified cutoffs in the [measurement archive](../measurements.json) to recover the exact membership of each linked tier.
