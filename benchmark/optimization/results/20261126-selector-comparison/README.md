# Baking selector comparison

Apple M5 Max, macOS 27.2.1, native Apple Clang 21.0.0 (clang-2100.4.1.1), Release (-O3, LTO), single guest per library. Each candidate was measured against the corresponding unbaked interpreter. Both guests used the same commands, fixtures and expected output hashes.

The split was fixed before capture. Training used eight cases in four software families (gzip, sed, coreutils, tar). Holdout used grep-fixed, grep-regex and node-minify, weighted as two families (grep and Node). CoreMark was excluded. Search depth 2, seed 1, specificity/family risk 0.35, nine candidate build attempts per algorithm and guest. Each case uses the median paired ratio, followed by the geometric mean within and across families. Finalist training uses three repeats and holdout uses five.

## Findings

- Under the 1.5 MiB i386 budget, greedy selected all 1,024 fusion candidates and 575 ModRM bindings. Prefix selected 304 fusions and all 1,213 ModRM bindings. Greedy led on the training families (1.740× versus 1.656×); prefix led on grep/Node (1.414× versus 1.356×) and mawk/diff (2.049× versus 1.871×). Several classes changed together, so these comparisons establish a tradeoff for whole tables.
- All four i386 10 MiB plans contain the same 18,378 candidates. Their Mach-O `__TEXT,__text` SHA-256 digests match. Differences in their recorded speedups illustrate run-to-run timing variation. The complete i386 pool only grew native text by 3.253 MiB for this split.
- AArch64 transferred much less of its training speedup to grep/Node. At 3 MiB all four methods fell between 0.955× and 0.992× on those two families in the common panel. Measuring only the held-out cases produced different values near parity; both protocols are reported below. External mawk/diff improved with every plan, with model leading at 1.5 and 3 MiB and prefix leading at 10 MiB.
- Each algorithm used nine attempted builds per guest. Of 72 linked candidates, 18 exceeded their requested tier and all 18 fit a larger one. The trainer retained those libraries so the search could reuse their measured code and size. Each frozen chain was checked for strict inclusion and actual text growth.
- This comparison covers one host toolchain, one seed and one family split. The four implementations search multiple candidate builds. The final section gives a design for a single offline ranking shared across tiers.

## Common-reference remeasurement

All frozen libraries for each guest were remeasured together against one unbaked native library. Trial order used a fixed shuffle and alternated forward and reverse over five repeats. The training, held-out and external columns each give equal weight to software families within their own split. External cases run mawk aggregation and GNU diff on deterministic new inputs. The external fixtures and reference output digests were fixed before measuring the holdout. Each case output was checked on every run.

| Guest | Tier | Algorithm | Growth (MiB) | Training | Holdout | grep | Node | External | mawk | diff |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|
| i386 | 1.5m | prefix | 1.357 | 1.656 | 1.414 | 1.486 | 1.346 | 2.049 | 2.088 | 2.011 |
| i386 | 1.5m | greedy | 1.151 | 1.740 | 1.356 | 1.404 | 1.310 | 1.871 | 2.004 | 1.747 |
| i386 | 1.5m | knapsack | 1.068 | 1.728 | 1.322 | 1.354 | 1.291 | 1.865 | 1.983 | 1.755 |
| i386 | 1.5m | model | 1.373 | 1.655 | 1.378 | 1.423 | 1.333 | 2.041 | 2.066 | 2.017 |
| i386 | 3m | prefix | 2.670 | 1.642 | 1.431 | 1.520 | 1.347 | 2.092 | 2.124 | 2.060 |
| i386 | 3m | greedy | 2.168 | 1.680 | 1.393 | 1.457 | 1.333 | 2.027 | 2.099 | 1.958 |
| i386 | 3m | knapsack | 2.702 | 1.692 | 1.388 | 1.453 | 1.326 | 2.091 | 2.131 | 2.051 |
| i386 | 3m | model | 2.264 | 1.677 | 1.393 | 1.437 | 1.351 | 2.081 | 2.127 | 2.037 |
| i386 | 10m | prefix | 3.253 | 1.682 | 1.403 | 1.487 | 1.324 | 2.062 | 2.149 | 1.979 |
| i386 | 10m | greedy | 3.253 | 1.674 | 1.395 | 1.466 | 1.327 | 2.074 | 2.150 | 2.001 |
| i386 | 10m | knapsack | 3.253 | 1.693 | 1.415 | 1.500 | 1.335 | 2.058 | 2.138 | 1.981 |
| i386 | 10m | model | 3.253 | 1.728 | 1.420 | 1.507 | 1.339 | 2.060 | 2.152 | 1.971 |
| aarch64 | 1.5m | prefix | 1.305 | 1.641 | 0.980 | 0.956 | 1.004 | 1.087 | 1.158 | 1.021 |
| aarch64 | 1.5m | greedy | 1.289 | 1.431 | 1.006 | 0.989 | 1.022 | 1.088 | 1.159 | 1.022 |
| aarch64 | 1.5m | knapsack | 1.279 | 1.445 | 1.004 | 0.991 | 1.018 | 1.091 | 1.153 | 1.033 |
| aarch64 | 1.5m | model | 1.351 | 1.406 | 1.010 | 1.001 | 1.019 | 1.156 | 1.221 | 1.094 |
| aarch64 | 3m | prefix | 2.660 | 1.601 | 0.962 | 0.927 | 0.999 | 1.157 | 1.231 | 1.088 |
| aarch64 | 3m | greedy | 2.745 | 1.515 | 0.976 | 0.946 | 1.007 | 1.130 | 1.198 | 1.066 |
| aarch64 | 3m | knapsack | 2.600 | 1.546 | 0.955 | 0.916 | 0.996 | 1.094 | 1.172 | 1.022 |
| aarch64 | 3m | model | 2.441 | 1.470 | 0.992 | 0.968 | 1.016 | 1.174 | 1.244 | 1.109 |
| aarch64 | 10m | prefix | 6.369 | 1.695 | 0.970 | 0.955 | 0.985 | 1.206 | 1.313 | 1.108 |
| aarch64 | 10m | greedy | 7.437 | 1.679 | 0.961 | 0.925 | 0.999 | 1.152 | 1.244 | 1.066 |
| aarch64 | 10m | knapsack | 6.995 | 1.699 | 0.964 | 0.932 | 0.997 | 1.152 | 1.245 | 1.066 |
| aarch64 | 10m | model | 7.827 | 1.724 | 0.965 | 0.922 | 1.009 | 1.174 | 1.254 | 1.100 |

## All cases in the common-reference remeasurement

| Guest | Tier | Algorithm | gzip | gunzip | sed | sort | sha256 | tar | du | ls | grep-fixed | grep-regex | Node | mawk | diff |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| i386 | 1.5m | prefix | 2.588 | 2.635 | 2.881 | 2.232 | 1.698 | 0.763 | 0.743 | 1.046 | 1.265 | 1.744 | 1.346 | 2.088 | 2.011 |
| i386 | 1.5m | greedy | 2.703 | 2.657 | 3.320 | 2.247 | 1.977 | 0.750 | 0.770 | 1.044 | 1.202 | 1.640 | 1.310 | 2.004 | 1.747 |
| i386 | 1.5m | knapsack | 2.660 | 2.666 | 3.312 | 2.217 | 1.841 | 0.756 | 0.765 | 1.019 | 1.188 | 1.543 | 1.291 | 1.983 | 1.755 |
| i386 | 1.5m | model | 2.662 | 2.694 | 3.073 | 2.247 | 1.881 | 0.745 | 0.739 | 0.716 | 1.210 | 1.674 | 1.333 | 2.066 | 2.017 |
| i386 | 3m | prefix | 2.608 | 2.697 | 2.864 | 2.213 | 1.747 | 0.752 | 0.728 | 0.935 | 1.297 | 1.782 | 1.347 | 2.124 | 2.060 |
| i386 | 3m | greedy | 2.704 | 2.677 | 3.195 | 2.234 | 2.138 | 0.739 | 0.728 | 0.714 | 1.266 | 1.675 | 1.333 | 2.099 | 1.958 |
| i386 | 3m | knapsack | 2.686 | 2.669 | 3.272 | 2.280 | 2.171 | 0.740 | 0.734 | 0.701 | 1.243 | 1.698 | 1.326 | 2.131 | 2.051 |
| i386 | 3m | model | 2.677 | 2.688 | 3.255 | 2.232 | 2.139 | 0.724 | 0.724 | 0.710 | 1.237 | 1.669 | 1.351 | 2.127 | 2.037 |
| i386 | 10m | prefix | 2.695 | 2.661 | 3.232 | 2.246 | 2.178 | 0.721 | 0.756 | 0.727 | 1.272 | 1.738 | 1.324 | 2.149 | 1.979 |
| i386 | 10m | greedy | 2.687 | 2.648 | 3.219 | 2.273 | 2.196 | 0.729 | 0.722 | 0.690 | 1.263 | 1.703 | 1.327 | 2.150 | 2.001 |
| i386 | 10m | knapsack | 2.696 | 2.663 | 3.286 | 2.239 | 2.189 | 0.735 | 0.722 | 0.730 | 1.263 | 1.781 | 1.335 | 2.138 | 1.981 |
| i386 | 10m | model | 2.697 | 2.698 | 3.246 | 2.258 | 2.196 | 0.735 | 0.738 | 1.010 | 1.275 | 1.780 | 1.339 | 2.152 | 1.971 |
| aarch64 | 1.5m | prefix | 2.248 | 2.511 | 1.985 | 1.607 | 4.793 | 0.898 | 1.128 | 0.992 | 0.900 | 1.017 | 1.004 | 1.158 | 1.021 |
| aarch64 | 1.5m | greedy | 2.183 | 2.446 | 1.901 | 1.658 | 1.053 | 0.851 | 0.899 | 1.004 | 0.953 | 1.028 | 1.022 | 1.159 | 1.022 |
| aarch64 | 1.5m | knapsack | 2.170 | 2.466 | 1.897 | 1.637 | 1.037 | 0.887 | 0.908 | 1.019 | 0.942 | 1.043 | 1.018 | 1.153 | 1.033 |
| aarch64 | 1.5m | model | 2.186 | 2.493 | 1.897 | 1.688 | 1.096 | 0.875 | 0.824 | 0.679 | 0.952 | 1.053 | 1.019 | 1.221 | 1.094 |
| aarch64 | 3m | prefix | 2.327 | 2.539 | 2.277 | 1.659 | 4.674 | 0.813 | 0.937 | 0.624 | 0.851 | 1.009 | 0.999 | 1.231 | 1.088 |
| aarch64 | 3m | greedy | 2.312 | 2.577 | 2.346 | 1.635 | 1.092 | 0.824 | 0.926 | 0.936 | 0.872 | 1.027 | 1.007 | 1.198 | 1.066 |
| aarch64 | 3m | knapsack | 2.334 | 2.558 | 2.267 | 1.637 | 2.791 | 0.807 | 0.878 | 0.661 | 0.852 | 0.985 | 0.996 | 1.172 | 1.022 |
| aarch64 | 3m | model | 2.286 | 2.559 | 2.243 | 1.683 | 1.101 | 0.844 | 0.891 | 0.652 | 0.897 | 1.044 | 1.016 | 1.244 | 1.109 |
| aarch64 | 10m | prefix | 2.398 | 2.580 | 2.579 | 1.748 | 4.426 | 0.797 | 1.029 | 0.857 | 0.862 | 1.059 | 0.985 | 1.313 | 1.108 |
| aarch64 | 10m | greedy | 2.403 | 2.546 | 2.592 | 1.772 | 4.478 | 0.816 | 1.030 | 0.651 | 0.843 | 1.015 | 0.999 | 1.244 | 1.066 |
| aarch64 | 10m | knapsack | 2.410 | 2.573 | 2.621 | 1.776 | 4.606 | 0.831 | 1.049 | 0.648 | 0.847 | 1.027 | 0.997 | 1.245 | 1.066 |
| aarch64 | 10m | model | 2.401 | 2.594 | 2.616 | 1.739 | 4.574 | 0.813 | 1.037 | 0.932 | 0.836 | 1.017 | 1.009 | 1.254 | 1.100 |

## Training finalists and per-algorithm holdout runs

| Guest | Algorithm | Growth tier (MiB) | Linked growth (MiB) | Training | Holdout | grep | Node | Slowest training family |
|---|---|---:|---:|---:|---:|---:|---:|---|
| i386 | prefix | 1.5 | 1.357 | 1.658 | 1.372 | 1.392 | 1.353 | tar 0.756 |
| i386 | prefix | 3 | 2.670 | 1.660 | 1.387 | 1.422 | 1.353 | tar 0.760 |
| i386 | prefix | 10 | 3.253 | 1.671 | 1.379 | 1.411 | 1.347 | tar 0.707 |
| i386 | greedy | 1.5 | 1.151 | 1.738 | 1.329 | 1.343 | 1.315 | tar 0.740 |
| i386 | greedy | 3 | 2.168 | 1.743 | 1.369 | 1.392 | 1.346 | tar 0.752 |
| i386 | greedy | 10 | 3.253 | 1.740 | 1.384 | 1.417 | 1.351 | tar 0.742 |
| i386 | knapsack | 1.5 | 1.068 | 1.736 | 1.305 | 1.293 | 1.317 | tar 0.752 |
| i386 | knapsack | 3 | 2.702 | 1.728 | 1.368 | 1.390 | 1.347 | tar 0.719 |
| i386 | knapsack | 10 | 3.253 | 1.744 | 1.379 | 1.409 | 1.349 | tar 0.751 |
| i386 | model | 1.5 | 1.373 | 1.677 | 1.360 | 1.372 | 1.349 | tar 0.745 |
| i386 | model | 3 | 2.264 | 1.736 | 1.378 | 1.400 | 1.358 | tar 0.737 |
| i386 | model | 10 | 3.253 | 1.665 | 1.373 | 1.409 | 1.338 | tar 0.714 |
| aarch64 | prefix | 1.5 | 1.305 | 1.534 | 1.021 | 1.010 | 1.032 | tar 0.811 |
| aarch64 | prefix | 3 | 2.660 | 1.626 | 1.029 | 1.020 | 1.038 | tar 0.787 |
| aarch64 | prefix | 10 | 6.369 | 1.610 | 1.016 | 1.026 | 1.005 | tar 0.741 |
| aarch64 | greedy | 1.5 | 1.289 | 1.394 | 1.016 | 1.002 | 1.030 | tar 0.793 |
| aarch64 | greedy | 3 | 2.745 | 1.442 | 1.026 | 1.022 | 1.030 | tar 0.797 |
| aarch64 | greedy | 10 | 7.437 | 1.674 | 1.016 | 1.013 | 1.020 | tar 0.764 |
| aarch64 | knapsack | 1.5 | 1.279 | 1.388 | 1.016 | 1.006 | 1.025 | tar 0.826 |
| aarch64 | knapsack | 3 | 2.600 | 1.509 | 1.018 | 1.011 | 1.024 | tar 0.776 |
| aarch64 | knapsack | 10 | 6.995 | 1.688 | 1.021 | 1.014 | 1.027 | tar 0.764 |
| aarch64 | model | 1.5 | 1.351 | 1.414 | 1.022 | 1.022 | 1.021 | tar 0.821 |
| aarch64 | model | 3 | 2.441 | 1.436 | 1.028 | 1.024 | 1.033 | tar 0.786 |
| aarch64 | model | 10 | 7.827 | 1.648 | 1.020 | 1.012 | 1.029 | tar 0.774 |

## Build effort

| Guest | Algorithm | Candidate attempts | Eligible | Reused at larger tier | Build minutes | Unbaked text (B) |
|---|---|---:|---:|---:|---:|---:|
| i386 | prefix | 9 | 9 | 0 | 2.0 | 531496 |
| i386 | greedy | 9 | 9 | 1 | 2.7 | 531496 |
| i386 | knapsack | 9 | 9 | 1 | 2.6 | 531496 |
| i386 | model | 9 | 9 | 0 | 2.4 | 531496 |
| aarch64 | prefix | 9 | 9 | 6 | 4.0 | 308812 |
| aarch64 | greedy | 9 | 9 | 4 | 4.7 | 308812 |
| aarch64 | knapsack | 9 | 9 | 3 | 4.8 | 308812 |
| aarch64 | model | 9 | 9 | 3 | 5.7 | 308812 |

## Case speedups in the separate runs

| Guest | Algorithm | Tier | gzip | gunzip | sed | sort | sha256 | tar | du | ls | grep-fixed | grep-regex | node-minify |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| i386 | prefix | 1.5m | 2.603 | 2.612 | 2.890 | 2.258 | 1.664 | 0.756 | 0.779 | 1.059 | 1.147 | 1.690 | 1.353 |
| i386 | prefix | 3m | 2.609 | 2.660 | 2.862 | 2.239 | 1.687 | 0.760 | 0.763 | 1.068 | 1.160 | 1.744 | 1.353 |
| i386 | prefix | 10m | 2.703 | 2.658 | 3.229 | 2.230 | 2.163 | 0.707 | 0.726 | 0.750 | 1.150 | 1.732 | 1.347 |
| i386 | greedy | 1.5m | 2.698 | 2.654 | 3.332 | 2.212 | 1.960 | 0.740 | 0.804 | 1.048 | 1.120 | 1.611 | 1.315 |
| i386 | greedy | 3m | 2.708 | 2.668 | 3.279 | 2.197 | 2.069 | 0.752 | 0.782 | 1.054 | 1.158 | 1.673 | 1.346 |
| i386 | greedy | 10m | 2.715 | 2.668 | 3.300 | 2.198 | 2.173 | 0.742 | 0.754 | 1.038 | 1.153 | 1.740 | 1.351 |
| i386 | knapsack | 1.5m | 2.677 | 2.668 | 3.311 | 2.144 | 1.939 | 0.752 | 0.789 | 1.058 | 1.110 | 1.506 | 1.317 |
| i386 | knapsack | 3m | 2.713 | 2.665 | 3.326 | 2.162 | 2.172 | 0.719 | 0.759 | 1.039 | 1.156 | 1.671 | 1.347 |
| i386 | knapsack | 10m | 2.717 | 2.661 | 3.269 | 2.197 | 2.096 | 0.751 | 0.793 | 1.056 | 1.148 | 1.730 | 1.349 |
| i386 | model | 1.5m | 2.675 | 2.658 | 3.077 | 2.235 | 1.866 | 0.745 | 0.734 | 0.919 | 1.139 | 1.652 | 1.349 |
| i386 | model | 3m | 2.700 | 2.650 | 3.301 | 2.264 | 2.109 | 0.737 | 0.756 | 1.048 | 1.149 | 1.705 | 1.358 |
| i386 | model | 10m | 2.694 | 2.654 | 3.293 | 2.159 | 2.160 | 0.714 | 0.685 | 0.698 | 1.147 | 1.731 | 1.338 |
| aarch64 | prefix | 1.5m | 2.262 | 2.512 | 1.995 | 1.572 | 4.764 | 0.811 | 0.879 | 0.644 | 0.981 | 1.041 | 1.032 |
| aarch64 | prefix | 3m | 2.334 | 2.576 | 2.298 | 1.649 | 4.733 | 0.787 | 0.871 | 0.914 | 0.965 | 1.078 | 1.038 |
| aarch64 | prefix | 10m | 2.404 | 2.579 | 2.533 | 1.675 | 4.609 | 0.741 | 0.843 | 0.656 | 0.957 | 1.101 | 1.005 |
| aarch64 | greedy | 1.5m | 2.192 | 2.452 | 1.875 | 1.598 | 1.093 | 0.793 | 0.866 | 0.955 | 0.978 | 1.026 | 1.030 |
| aarch64 | greedy | 3m | 2.310 | 2.568 | 2.254 | 1.612 | 1.116 | 0.797 | 0.815 | 0.652 | 0.980 | 1.066 | 1.030 |
| aarch64 | greedy | 10m | 2.406 | 2.564 | 2.626 | 1.752 | 4.505 | 0.764 | 0.899 | 0.868 | 0.957 | 1.073 | 1.020 |
| aarch64 | knapsack | 1.5m | 2.149 | 2.460 | 1.881 | 1.607 | 1.029 | 0.826 | 0.709 | 0.993 | 0.979 | 1.034 | 1.025 |
| aarch64 | knapsack | 3m | 2.358 | 2.568 | 2.241 | 1.614 | 2.886 | 0.776 | 0.763 | 0.609 | 0.976 | 1.048 | 1.024 |
| aarch64 | knapsack | 10m | 2.423 | 2.605 | 2.643 | 1.731 | 4.682 | 0.764 | 0.894 | 0.908 | 0.953 | 1.080 | 1.027 |
| aarch64 | model | 1.5m | 2.195 | 2.505 | 1.891 | 1.656 | 1.114 | 0.821 | 0.844 | 0.935 | 0.982 | 1.065 | 1.021 |
| aarch64 | model | 3m | 2.296 | 2.582 | 2.249 | 1.665 | 1.140 | 0.786 | 0.760 | 0.659 | 0.972 | 1.078 | 1.033 |
| aarch64 | model | 10m | 2.371 | 2.600 | 2.633 | 1.748 | 4.693 | 0.774 | 0.887 | 0.617 | 0.953 | 1.074 | 1.029 |

## Frozen table composition

i386 counts are ModRM / policy / exact / fusion / dynamic displacement / dynamic immediate / both dynamic. AArch64 counts are exact / partial / pair / sequence. Long counts include sequences of at least five words.

| Guest | Tier | Algorithm | Actual entry counts | Long AArch64 sequences | Identical selection in this tier |
|---|---|---|---|---:|---|
| i386 | 1.5m | prefix | 1213 / 463 / 3950 / 304 / 550 / 421 / 92 | — | — |
| i386 | 3m | prefix | 1213 / 654 / 12099 / 367 / 805 / 421 / 92 | — | — |
| i386 | 10m | prefix | 1213 / 654 / 14169 / 1024 / 805 / 421 / 92 | — | greedy, knapsack, model |
| i386 | 1.5m | greedy | 575 / 416 / 2723 / 1024 / 228 / 159 / 18 | — | — |
| i386 | 3m | greedy | 935 / 592 / 7891 / 1024 / 577 / 306 / 69 | — | — |
| i386 | 10m | greedy | 1213 / 654 / 14169 / 1024 / 805 / 421 / 92 | — | prefix, knapsack, model |
| i386 | 1.5m | knapsack | 558 / 464 / 2132 / 1024 / 252 / 158 / 19 | — | — |
| i386 | 3m | knapsack | 994 / 617 / 11020 / 1024 / 682 / 389 / 78 | — | — |
| i386 | 10m | knapsack | 1213 / 654 / 14169 / 1024 / 805 / 421 / 92 | — | prefix, greedy, model |
| i386 | 1.5m | model | 682 / 570 / 4346 / 696 / 512 / 307 / 0 | — | — |
| i386 | 3m | model | 1213 / 654 / 7281 / 1024 / 805 / 421 / 92 | — | — |
| i386 | 10m | model | 1213 / 654 / 14169 / 1024 / 805 / 421 / 92 | — | prefix, greedy, knapsack |
| aarch64 | 1.5m | prefix | 883 / 402 / 422 / 820 | 234 | — |
| aarch64 | 3m | prefix | 1974 / 804 / 844 / 1639 | 312 | — |
| aarch64 | 10m | prefix | 7001 / 1024 / 2845 / 5844 | 425 | — |
| aarch64 | 1.5m | greedy | 540 / 159 / 362 / 974 | 208 | — |
| aarch64 | 3m | greedy | 1972 / 247 / 651 / 2012 | 279 | — |
| aarch64 | 10m | greedy | 3979 / 283 / 3858 / 7267 | 713 | — |
| aarch64 | 1.5m | knapsack | 553 / 144 / 354 / 965 | 208 | — |
| aarch64 | 3m | knapsack | 1259 / 199 / 354 / 2214 | 359 | — |
| aarch64 | 10m | knapsack | 3654 / 199 / 1260 / 7381 | 714 | — |
| aarch64 | 1.5m | model | 1290 / 1024 / 88 / 603 | 167 | — |
| aarch64 | 3m | model | 1982 / 1024 / 441 / 1487 | 254 | — |
| aarch64 | 10m | model | 1982 / 1024 / 441 / 9355 | 945 | — |

## Pre-build size estimator

For each candidate, the class-count fit below uses only earlier linked candidates from the same guest and algorithm. The predicted value is a retrospective replay of the existing pre-build formula on the originally requested counts. A target miss counts a table whose measured growth exceeded the initially requested tier, including a table subsequently archived at a larger tier.

| Guest | Linked candidates | Median absolute error (MiB) | Max underprediction (MiB) | Max overprediction (MiB) | Requested tier exceeded |
|---|---:|---:|---:|---:|---:|
| i386 | 36 | 0.028 | 1.468 | 0.809 | 2 |
| aarch64 | 36 | 0.303 | 1.028 | 2.648 | 16 |

## One offline ranking for all three tiers

A front-to-back replay of the class-count estimator on the initial control builds exposes large misses. The full i386 table was estimated at 1.784 MiB of growth and linked at 3.253 MiB. An AArch64 1.5 MiB proposal was estimated at 1.344 MiB and linked at 2.372 MiB. Each estimate uses only measurements available before the corresponding build.

Apple arm64 Mach-O function starts give a more informative calibration signal. In the first AArch64 control run, all 2,817 named BakedHandler entries shared by the small and large plans had the same linked function span; the medium and large plans agreed on all 5,781 shared entries. Among the large plan's 4,421 four-word BakedHandler names, spans ranged from 20 to 628 bytes, with a median of 140 bytes. These spans include any inter-function alignment. A fusion can own multiple entry points or share code. A single handler span measures only one component of its total cost.

| Design choice | Proposed implementation |
|---|---|
| Cost before build | Cache linked symbol spans by stable binding identity, guest, host target, compiler, generator version and codegen flags. Count shared symbol addresses once. Fit missing costs using instruction and specialization features. Reserve space for lookup code and the one-sided residual observed on independent calibration plans. Treat LTO and layout changes as sources of uncertainty. |
| Utility in a partially built table | Replay only affected blocks when adding a candidate and its missing dependencies. An exact operand fixing an immediate receives the incremental improvement over any matching partial. A long fusion receives the incremental dispatch savings after shorter fusions. |
| Generality | Weight software families equally, shrink gains concentrated in one family, and use the observed diversity of operands and contexts alongside the matcher's specificity. A dynamic imm12 that works across many offsets or a short sequence that survives different surrounding code receives a stronger prior. |
| Nested optimization | Jointly maximize a weighted sum of utility at 1.5, 3 and 10 MiB. A depth-one search can follow marginal robust utility per byte; a beam can retain alternative orderings and refine candidates around the three cutoffs. Search depth controls offline work. Export the insertion rank, marginal utility, cost, cumulative cost, supporting families and first tier for each item. |
| Validation | With a matching cost cache, build the three final libraries and verify their true text growth. If an artifact exceeds its cap, shorten its prefix and relink using the same ranking. For a new toolchain, account for the calibration build separately. An output table for a larger capacity can be identical when the useful pool is exhausted. |

The nested-capacity objective is the [incremental knapsack problem](https://arxiv.org/abs/1701.07299). Classical guarantees assume a specific profit and cost model. Native dispatch priority, shared code and overlapping patterns require empirical replay and linked-size validation here. A single unconditional hit-count score cannot account for the gain lost when another fusion consumes the same instructions. The design above describes a prospective selector. The measurements in this report use the four implemented selectors.

The [measurement archive](measurements.json) includes the common-reference timings, verification metadata, and historical pre-build estimates.

## Reproducing the comparison

Use the shared GNU/Node manifest and the native Release configuration from the training guide. Run each algorithm with the same verified captures. The invocation below shows the greedy run with a shared rootfs configuration.

```sh
python3 scripts/train_nested_presets.py --config training.json --output out/greedy \
  --algorithm greedy --max-builds 9 --search-depth 2 --seed 1 \
  --holdout-family grep --holdout-family node --capture-source out/prefix
python3 scripts/evaluate_baking_holdout.py --config training.json \
  --frozen out/greedy/aarch64/frozen --output out/holdout/greedy/aarch64 --repeats 5
```

For the first run use `--algorithm prefix` and omit `--capture-source` to capture the training cases. The common-reference panel runs all twelve frozen libraries for each guest in one timing batch with the same unbaked reference. Its raw seconds and output checksums are in the measurement archive.

The two external cases read the following UTF-8 fixtures. Run this snippet in a new fixture directory and use that directory as both cases' guest working directory. The measurement archive gives the exact shell commands and input and output SHA-256 digests.

```python
from pathlib import Path
import random
import string

rng = random.Random(384672)
before, after = [], []
for i in range(1, 45001):
    group = rng.randrange(101)
    value = rng.randrange(8000)
    token = ''.join(rng.choices(string.ascii_lowercase, k=28))
    line = f'{i:06d} {group} {value} {token}'
    before.append(line)
    after.append(line + (' revised' if i % 97 == 0 else ''))
for name, lines in [('before.txt', before), ('after.txt', after)]:
    Path(name).write_bytes(('\n'.join(lines) + '\n').encode())
```
