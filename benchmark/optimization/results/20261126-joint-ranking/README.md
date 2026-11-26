# Joint ranking of baked instruction variants

Apple M5 Max, arm64 macOS 27.2.1, Apple Clang 21.0.0, Release `-O3`, LTO. Each guest uses a single-ISA native library. LLVM JIT and dynamic specialization were disabled. The tiers limit native code growth over the unbaked interpreter, in MiB.

A uses standalone gain per byte, B prices the marginal contribution under native match priority, C adds a family-robust objective and observed transfer evidence, and D searches around the three cutoffs of C with beam depth 3. C and D use risk 0.5. Each algorithm performed one search per guest. The control is the frozen legacy prefix table trained on the same eight cases. CoreMark was excluded.

## Findings

- On AArch64 the new-family blind speedup of C over B was 4.3%, 5.3% and 7.0% across the three tiers. C won all five paired rounds at each tier. The largest effect was Perl, especially at 10 MiB (1.804× for C, 1.566× for B). At that tier rev measured 1.061× versus 1.067×. C also reduced training speedup from 1.624× to 1.500×. The compiler-transfer panel showed little separation.
- On i386 the static order A gave 1.534× and 1.602× on the blind panel at 1.5 and 3 MiB. At 10 MiB, A and the legacy prefix selected every candidate and generated identical native text. At 3 MiB, C traded some of A's blind speedup (1.539× versus 1.602×) for a smaller linked increment (1.657 versus 2.992 MiB).
- D produced the same i386 memberships and native code as C. On AArch64 its new-family scores were within 0.7% of C at every tier with mixed signs. The data favor the cheaper C search when optimizing for transfer to these two unseen families. B remains the strongest AArch64 training choice at 1.5 and 3 MiB. At the time of the comparison, the trainer defaulted to the legacy prefix.
- Across eight rankings the search itself took 0.59–2.41 seconds per guest and method, excluding profile and cost-index preparation. All 24 verified prefixes fit their budgets. The 36 candidate links comprise 12 i386 and 24 AArch64 attempts. Nine additional setup calls built or checked the unbaked baselines.

## Measured speedups

All 15 binaries per guest ran together against a common unbaked reference. Each round measured the reference before and after a fixed-seed shuffled cohort, reversing the cohort every other round. Ratios use linearly interpolated reference times. Each case uses the median of five ratios, then geometric means give equal weight to cases within a family and to the families within each panel. Every invocation checked the output against the predeclared SHA-256.

Training comprises gzip, sed, coreutils and tar. Regression comprises grep, Node, mawk and diffutils. The new-family blind panel comprises Perl and util-linux rev. The compiler-transfer panel comprises gzip 1.12 decompression on two distinct guest binaries built at O1 and O3. Both belong to a training family.

| Guest | Tier | Method | Growth MiB | Training | Regression | Blind | Compiler | Worst blind family | Slowest family across panels |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| i386 | 1.5m | prefix | 1.357 | 1.655 | 1.712 | 1.486 | 1.646 | util-linux 1.242 | tar 0.765 |
| i386 | 1.5m | A | 1.498 | 1.716 | 1.711 | 1.534 | 1.653 | util-linux 1.217 | tar 0.738 |
| i386 | 1.5m | B | 1.455 | 1.674 | 1.630 | 1.495 | 1.588 | util-linux 1.232 | tar 0.716 |
| i386 | 1.5m | C | 1.455 | 1.751 | 1.700 | 1.531 | 1.636 | util-linux 1.238 | tar 0.747 |
| i386 | 1.5m | D | 1.455 | 1.733 | 1.706 | 1.524 | 1.629 | util-linux 1.219 | tar 0.748 |
| i386 | 3m | prefix | 2.670 | 1.646 | 1.739 | 1.523 | 1.736 | util-linux 1.236 | tar 0.748 |
| i386 | 3m | A | 2.992 | 1.726 | 1.729 | 1.602 | 1.723 | util-linux 1.241 | tar 0.738 |
| i386 | 3m | B | 2.600 | 1.729 | 1.660 | 1.530 | 1.604 | util-linux 1.225 | tar 0.731 |
| i386 | 3m | C | 1.657 | 1.736 | 1.696 | 1.539 | 1.630 | util-linux 1.242 | tar 0.748 |
| i386 | 3m | D | 1.657 | 1.686 | 1.676 | 1.537 | 1.629 | util-linux 1.234 | tar 0.724 |
| i386 | 10m | prefix | 3.253 | 1.691 | 1.712 | 1.609 | 1.717 | util-linux 1.246 | tar 0.729 |
| i386 | 10m | A | 3.253 | 1.696 | 1.713 | 1.588 | 1.730 | util-linux 1.227 | tar 0.733 |
| i386 | 10m | B | 2.600 | 1.726 | 1.651 | 1.525 | 1.613 | util-linux 1.232 | tar 0.738 |
| i386 | 10m | C | 1.657 | 1.730 | 1.698 | 1.522 | 1.638 | util-linux 1.226 | tar 0.745 |
| i386 | 10m | D | 1.657 | 1.718 | 1.695 | 1.549 | 1.637 | util-linux 1.242 | tar 0.753 |
| aarch64 | 1.5m | prefix | 1.305 | 1.550 | 1.029 | 1.091 | 1.069 | util-linux 1.032 | tar 0.831 |
| aarch64 | 1.5m | A | 1.458 | 1.495 | 1.023 | 1.064 | 1.045 | util-linux 1.033 | tar 0.847 |
| aarch64 | 1.5m | B | 1.430 | 1.735 | 1.042 | 1.097 | 1.070 | util-linux 1.032 | tar 0.827 |
| aarch64 | 1.5m | C | 1.461 | 1.527 | 1.063 | 1.145 | 1.067 | util-linux 1.044 | tar 0.819 |
| aarch64 | 1.5m | D | 1.461 | 1.520 | 1.068 | 1.149 | 1.065 | util-linux 1.039 | tar 0.816 |
| aarch64 | 3m | prefix | 2.660 | 1.616 | 1.065 | 1.142 | 1.087 | util-linux 1.044 | tar 0.782 |
| aarch64 | 3m | A | 2.832 | 1.596 | 1.027 | 1.096 | 1.057 | util-linux 1.038 | tar 0.789 |
| aarch64 | 3m | B | 2.900 | 1.730 | 1.058 | 1.174 | 1.075 | util-linux 1.045 | tar 0.796 |
| aarch64 | 3m | C | 2.870 | 1.519 | 1.078 | 1.237 | 1.086 | util-linux 1.053 | tar 0.760 |
| aarch64 | 3m | D | 2.871 | 1.556 | 1.076 | 1.241 | 1.078 | util-linux 1.045 | tar 0.780 |
| aarch64 | 10m | prefix | 6.369 | 1.671 | 1.080 | 1.246 | 1.102 | util-linux 1.051 | tar 0.759 |
| aarch64 | 10m | A | 9.692 | 1.625 | 1.072 | 1.272 | 1.101 | util-linux 1.051 | tar 0.751 |
| aarch64 | 10m | B | 9.718 | 1.624 | 1.073 | 1.293 | 1.076 | util-linux 1.067 | tar 0.727 |
| aarch64 | 10m | C | 9.668 | 1.500 | 1.084 | 1.384 | 1.082 | util-linux 1.061 | tar 0.740 |
| aarch64 | 10m | D | 9.669 | 1.484 | 1.090 | 1.375 | 1.075 | util-linux 1.064 | tar 0.744 |

## Paired comparisons

Blind ratios above one favor the numerator. The round range and number of wins use matched rounds and equal family weights. Five rounds on two new families support descriptive comparisons.

| Guest | Tier | Ratio | Blind | Blind rounds won | Blind round range | Regression |
| --- | --- | --- | --- | --- | --- | --- |
| i386 | 1.5m | C/B | 1.024 | 5/5 | 1.010–1.037 | 1.043 |
| i386 | 1.5m | D/C | 0.995 | 3/5 | 0.985–1.013 | 1.004 |
| i386 | 3m | C/B | 1.006 | 3/5 | 0.995–1.053 | 1.021 |
| i386 | 3m | D/C | 0.998 | 2/5 | 0.981–1.003 | 0.988 |
| i386 | 10m | C/B | 0.998 | 0/5 | 0.981–0.999 | 1.028 |
| i386 | 10m | D/C | 1.018 | 4/5 | 1.000–1.027 | 0.998 |
| aarch64 | 1.5m | C/B | 1.043 | 5/5 | 1.008–1.067 | 1.019 |
| aarch64 | 1.5m | D/C | 1.004 | 2/5 | 0.991–1.015 | 1.005 |
| aarch64 | 3m | C/B | 1.053 | 5/5 | 1.016–1.069 | 1.019 |
| aarch64 | 3m | D/C | 1.003 | 3/5 | 0.953–1.020 | 0.998 |
| aarch64 | 10m | C/B | 1.070 | 5/5 | 1.029–1.096 | 1.010 |
| aarch64 | 10m | D/C | 0.994 | 1/5 | 0.948–1.022 | 1.006 |

## Search and linking

Size calibration used linked Mach-O function spans from the 36 historical plans per guest. Cross-validation withheld entire search strategies and removed duplicate tables. Median absolute whole-plan errors were 3,367 B on i386 and 22,600.5 B on AArch64. The one-sided 90th-percentile reserves were 5,021 B and 174,708 B respectively. Candidate tables in this experiment come from new selection methods and can have different residuals.

| Guest | Method | Offline rank (s) | Candidate builds | Oversized attempts | Estimated cuts | Verified cuts |
| --- | --- | --- | --- | --- | --- | --- |
| i386 | A | 0.653 | 3 | 0 | 8659 / 17103 / 18378 | 8659 / 17103 / 18378 |
| i386 | B | 0.993 | 3 | 1 | 9581 / 15510 / 15510 | 9315 / 15510 / 15510 |
| i386 | C | 0.994 | 3 | 1 | 7539 / 8191 / 8191 | 7335 / 8191 / 8191 |
| i386 | D | 1.283 | 3 | 1 | 7539 / 8191 / 8191 | 7335 / 8191 / 8191 |
| aarch64 | A | 0.588 | 7 | 4 | 4182 / 9091 / 24085 | 4097 / 8239 / 22784 |
| aarch64 | B | 1.186 | 5 | 2 | 3134 / 7036 / 19587 | 3134 / 6613 / 18928 |
| aarch64 | C | 1.474 | 6 | 3 | 4810 / 10276 / 25585 | 4657 / 8976 / 23570 |
| aarch64 | D | 2.409 | 6 | 3 | 4811 / 10272 / 25584 | 4656 / 8978 / 23573 |

The [build ledger analysis](sizes.json) includes predicted and linked growth for every attempt. The [composition report](composition.json) compares B and C. Each ranked candidate, its binding, incremental gain, estimated cost and evidence is present in the trainer’s `ranking.json`. The [archived measurements](measurements.json) include all 17 cases in all five timed rounds, artifact digests, verified cuts, source fingerprints and ranking checksums.

## What changed in the selected code

On i386 at the verified 1.5 MiB cutoff, C used 74.3% exact scalar entries and 12.2% dynamic scalar entries. B used 83.3% and 6.5%. The C order reached its last positive marginal at 8,191 entries, which cost 1,737,992 linked bytes. An exhaustive one-bundle check on the remaining 10,187 candidates found 2,618 zero and 7,569 negative discounted marginals. A more specific native handler can displace a broader handler with greater discounted credit. The actual C and D i386 memberships and native text hashes match at all three tiers.

On AArch64, moving from B to C shortened the average admitted fusion from 5.04 to 3.24 instructions at 1.5 MiB, 4.51 to 3.15 at 3 MiB, and 4.20 to 3.25 at 10 MiB. With identical undiscounted credits on both sides, fusions of five or more instructions produced 77–78% of B's training gain and 26–30% of C's. The share of realized gain exclusive to one training family fell from roughly 69% to 64–66%. These measurements come from replaying the actual linked prefixes. The [composition data](composition.json) also retain the pre-link cutoffs.

Replay matches the native fusion, exact and partial handler priority and skips consumed instructions. Its scalar credit is a class estimate. The profiles do not distinguish independent ELF code sites, and the proxy does not model native instruction-cache costs, page faults or intra-fusion re-entry. The measured training speedup, family-held-out proxy and end-to-end blind results answer different questions.

## Why the size guard missed

Of 12 i386 tiers, nine passed on the first proposed cutoff, including three reused cutoffs. Of 12 AArch64 tiers, one passed on the first proposal. The median absolute error on the new links was 14,109 B for i386 and 397,399 B for AArch64. This is materially larger than the historical cross-validation error.

For the first AArch64 A 3 MiB attempt all 9,091 bindings had cached spans. Their combined estimate differed by 36 B. Three generated lookup functions occupied 774,080 B, including 579,064 B in the sequence resolver. When the prefix shrank by 549 entries, the lookup code grew by 62,456 B and the next link still exceeded its tier. Whole-program codegen changes the size of these switches with their keys and shape. An additive body-size model cannot guarantee a prefix fits. Only the verified linked prefixes are reported as usable tiers.

A separate retrospective fit used only 36 earlier AArch64 links to price each resolver by case count, first-word diversity and shared prefixes. On the 24 new links, median absolute whole-plan error fell from 397,399 B to 97,076 B, and maximum underprediction fell from 758,073 B to 431,190 B. The new historical 90th-percentile guard covered 10 of the 24 new links. These estimates were calculated after detecting lookup growth and did not select the timed binaries. See the [retrospective model, coefficients and residuals](resolver-retrospective.json).

## Development folds

Each of four folds generated candidates, ordering and transfer features from three families and replayed the held-out fourth family under native priority. The cost cache was shared and contains historical linked sizes. Dispatch utility is modeled offline and has different units from measured wall time. See [fold results](folds/summary.json) and [blind protocol and fixtures](blind/README.md).

| Guest | Method | Mean held-out proxy, 1.5 / 3 / 10 MiB | Worst held-out proxy, 1.5 / 3 / 10 MiB |
| --- | --- | --- | --- |
| i386 | joint-static | 0.3937 / 0.4040 / 0.4040 | 0.3189 / 0.3333 / 0.3333 |
| i386 | joint-marginal | 0.3909 / 0.3949 / 0.3949 | 0.3091 / 0.3132 / 0.3132 |
| i386 | joint-general | 0.3936 / 0.3937 / 0.3937 | 0.3172 / 0.3174 / 0.3174 |
| i386 | joint-beam | 0.3936 / 0.3937 / 0.3937 | 0.3172 / 0.3174 / 0.3174 |
| aarch64 | joint-static | 0.1739 / 0.2207 / 0.3326 | 0.0441 / 0.0563 / 0.1312 |
| aarch64 | joint-marginal | 0.2700 / 0.3132 / 0.3382 | 0.0573 / 0.0953 / 0.1056 |
| aarch64 | joint-general | 0.2637 / 0.3064 / 0.3250 | 0.0852 / 0.1186 / 0.1250 |
| aarch64 | joint-beam | 0.2637 / 0.3064 / 0.3250 | 0.0852 / 0.1186 / 0.1250 |

## Individual workload speedups

### i386

| Tier | Method | gzip | gunzip | sed | sort | sha256 | tar | du | ls | grep-fixed | grep-regex | node-minify | mawk-aggregate | diff-revisions | blind-perl-groups | blind-rev-lines | codegen-gzip-o1 | codegen-gzip-o3 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1.5m | prefix | 2.603 | 2.630 | 2.840 | 2.235 | 1.666 | 0.765 | 0.766 | 1.061 | 1.283 | 1.753 | 1.351 | 2.096 | 2.022 | 1.778 | 1.242 | 1.711 | 1.582 |
| 1.5m | A | 2.676 | 2.666 | 3.302 | 2.255 | 2.125 | 0.738 | 0.747 | 0.877 | 1.284 | 1.713 | 1.349 | 2.119 | 2.022 | 1.935 | 1.217 | 1.694 | 1.612 |
| 1.5m | B | 2.669 | 2.661 | 3.348 | 2.147 | 2.109 | 0.716 | 0.709 | 0.708 | 1.184 | 1.621 | 1.278 | 2.002 | 1.993 | 1.816 | 1.232 | 1.601 | 1.576 |
| 1.5m | C | 2.681 | 2.695 | 3.361 | 2.253 | 2.033 | 0.747 | 0.773 | 1.061 | 1.246 | 1.714 | 1.332 | 2.102 | 2.041 | 1.893 | 1.238 | 1.680 | 1.594 |
| 1.5m | D | 2.676 | 2.691 | 3.356 | 2.256 | 2.072 | 0.748 | 0.734 | 0.940 | 1.251 | 1.725 | 1.340 | 2.115 | 2.037 | 1.905 | 1.219 | 1.669 | 1.591 |
| 3m | prefix | 2.612 | 2.680 | 2.858 | 2.218 | 1.567 | 0.748 | 0.765 | 1.062 | 1.307 | 1.808 | 1.359 | 2.138 | 2.046 | 1.875 | 1.236 | 1.799 | 1.675 |
| 3m | A | 2.689 | 2.676 | 3.356 | 2.247 | 2.147 | 0.738 | 0.734 | 0.896 | 1.260 | 1.772 | 1.348 | 2.166 | 2.048 | 2.068 | 1.241 | 1.760 | 1.687 |
| 3m | B | 2.677 | 2.674 | 3.309 | 2.250 | 2.066 | 0.731 | 0.752 | 1.046 | 1.209 | 1.706 | 1.307 | 2.066 | 1.960 | 1.911 | 1.225 | 1.626 | 1.583 |
| 3m | C | 2.673 | 2.666 | 3.350 | 2.256 | 2.093 | 0.748 | 0.751 | 0.958 | 1.251 | 1.749 | 1.335 | 2.066 | 2.029 | 1.907 | 1.242 | 1.666 | 1.595 |
| 3m | D | 2.671 | 2.666 | 3.344 | 2.199 | 2.086 | 0.724 | 0.728 | 0.732 | 1.210 | 1.702 | 1.315 | 2.071 | 2.020 | 1.915 | 1.234 | 1.665 | 1.594 |
| 10m | prefix | 2.691 | 2.656 | 3.271 | 2.221 | 2.186 | 0.729 | 0.750 | 0.745 | 1.274 | 1.777 | 1.343 | 2.154 | 1.972 | 2.077 | 1.246 | 1.770 | 1.667 |
| 10m | A | 2.705 | 2.658 | 3.293 | 2.266 | 2.123 | 0.733 | 0.731 | 0.761 | 1.259 | 1.784 | 1.340 | 2.155 | 1.990 | 2.057 | 1.227 | 1.785 | 1.676 |
| 10m | B | 2.700 | 2.673 | 3.330 | 2.253 | 2.162 | 0.738 | 0.728 | 0.922 | 1.212 | 1.672 | 1.302 | 2.043 | 1.961 | 1.887 | 1.232 | 1.649 | 1.578 |
| 10m | C | 2.664 | 2.681 | 3.345 | 2.223 | 2.080 | 0.745 | 0.749 | 0.944 | 1.269 | 1.753 | 1.328 | 2.068 | 2.028 | 1.889 | 1.226 | 1.675 | 1.602 |
| 10m | D | 2.676 | 2.680 | 3.369 | 2.250 | 2.087 | 0.753 | 0.762 | 0.759 | 1.260 | 1.737 | 1.333 | 2.070 | 2.021 | 1.931 | 1.242 | 1.674 | 1.601 |

### aarch64

| Tier | Method | gzip | gunzip | sed | sort | sha256 | tar | du | ls | grep-fixed | grep-regex | node-minify | mawk-aggregate | diff-revisions | blind-perl-groups | blind-rev-lines | codegen-gzip-o1 | codegen-gzip-o3 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1.5m | prefix | 2.239 | 2.480 | 1.994 | 1.632 | 4.765 | 0.831 | 0.928 | 0.658 | 0.870 | 0.994 | 1.006 | 1.154 | 1.040 | 1.153 | 1.032 | 1.074 | 1.064 |
| 1.5m | A | 2.150 | 2.504 | 1.792 | 1.664 | 4.129 | 0.847 | 0.924 | 0.639 | 0.853 | 0.992 | 0.997 | 1.154 | 1.035 | 1.097 | 1.033 | 1.058 | 1.031 |
| 1.5m | B | 2.444 | 2.616 | 2.643 | 1.758 | 4.730 | 0.827 | 0.907 | 0.954 | 0.871 | 0.996 | 0.994 | 1.183 | 1.079 | 1.167 | 1.032 | 1.073 | 1.066 |
| 1.5m | C | 2.198 | 2.164 | 2.380 | 1.691 | 2.797 | 0.819 | 0.924 | 0.612 | 0.855 | 0.983 | 1.006 | 1.246 | 1.110 | 1.254 | 1.044 | 1.069 | 1.065 |
| 1.5m | D | 2.204 | 2.147 | 2.345 | 1.668 | 2.777 | 0.816 | 0.895 | 0.651 | 0.882 | 0.991 | 1.009 | 1.262 | 1.093 | 1.270 | 1.039 | 1.075 | 1.055 |
| 3m | prefix | 2.325 | 2.557 | 2.288 | 1.628 | 4.649 | 0.782 | 0.862 | 0.914 | 0.856 | 1.014 | 1.009 | 1.238 | 1.105 | 1.250 | 1.044 | 1.092 | 1.082 |
| 3m | A | 2.261 | 2.521 | 2.180 | 1.657 | 4.107 | 0.789 | 0.990 | 0.918 | 0.848 | 0.986 | 0.998 | 1.170 | 1.041 | 1.158 | 1.038 | 1.075 | 1.038 |
| 3m | B | 2.421 | 2.613 | 2.697 | 1.788 | 4.728 | 0.796 | 0.958 | 0.936 | 0.856 | 0.994 | 1.001 | 1.225 | 1.109 | 1.320 | 1.045 | 1.075 | 1.075 |
| 3m | C | 2.227 | 2.176 | 2.469 | 1.667 | 2.760 | 0.760 | 0.917 | 0.656 | 0.862 | 1.038 | 1.000 | 1.311 | 1.091 | 1.452 | 1.053 | 1.085 | 1.086 |
| 3m | D | 2.214 | 2.162 | 2.437 | 1.696 | 2.762 | 0.780 | 0.870 | 0.968 | 0.870 | 1.034 | 1.000 | 1.279 | 1.105 | 1.473 | 1.045 | 1.085 | 1.071 |
| 10m | prefix | 2.356 | 2.589 | 2.554 | 1.733 | 4.541 | 0.759 | 0.982 | 0.906 | 0.863 | 1.038 | 0.983 | 1.324 | 1.104 | 1.479 | 1.051 | 1.095 | 1.109 |
| 10m | A | 2.374 | 2.572 | 2.577 | 1.765 | 4.487 | 0.751 | 0.924 | 0.622 | 0.835 | 1.006 | 0.997 | 1.308 | 1.105 | 1.540 | 1.051 | 1.096 | 1.106 |
| 10m | B | 2.407 | 2.578 | 2.593 | 1.748 | 4.658 | 0.727 | 0.908 | 0.650 | 0.852 | 1.014 | 0.998 | 1.293 | 1.103 | 1.566 | 1.067 | 1.061 | 1.092 |
| 10m | C | 2.209 | 2.165 | 2.273 | 1.638 | 2.704 | 0.740 | 0.935 | 0.868 | 0.852 | 1.033 | 0.983 | 1.374 | 1.089 | 1.804 | 1.061 | 1.080 | 1.084 |
| 10m | D | 2.194 | 2.146 | 2.349 | 1.672 | 2.701 | 0.744 | 0.939 | 0.633 | 0.856 | 1.048 | 0.988 | 1.364 | 1.108 | 1.776 | 1.064 | 1.066 | 1.083 |


## Archived inputs

The [ranking archive](rankings/README.md) contains the scored candidate lists and cost caches from this experiment. The [measurements](measurements.json) retain the raw times, workload definitions, compiler settings and candidate hashes used in the tables above. The current [preset trainer](../../../../docs/baked-instruction-variants.md) implements method C for both guests.
