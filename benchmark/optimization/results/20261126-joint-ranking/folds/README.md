# Leave one training family out

Each of the four folds excludes every capture from one software family (coreutils, gzip, sed or tar). The fold builds a fresh pool from the other three families and uses that same pool for A, B, C and D. The replay used for ranking sees training captures only. A second replay applies the resulting prefixes to the excluded captures using identical candidate IDs and native lookup precedence. The shared byte estimator uses prior build sizes and symbol spans. It contributes no held-family visits or timing to ranking.

A held-family proxy is dispatch-equivalent credit per original guest instruction. A fused span of width `w` earns `w - 1`. Scalar bindings earn the fixed class-specific credits in `scripts/baking_replay.py`. Each case is normalized by its own instruction count and each case within the held family has equal weight. Each family in the summary has equal weight. The proxy has no specificity discount on evaluation. A/B use their native unpenalized scoring, C/D use risk 0.5 and D uses depth 3. The three caps use estimated growth after deducting the calibration reserve. These folds use no native preset builds.

Each guest's JSON files record its capture hashes, pool hash, cost cache hash, cut lengths, selected-ID checksums, predicted growth, held proxy, and runtime per ranking.
