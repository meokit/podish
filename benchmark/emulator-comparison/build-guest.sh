#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C TZ=UTC
bench=/opt/bench
mkdir -p "$bench"/{input,out,expected}
cp /boot/vmlinuz-*-686-pae /kernel

fetch() {
    curl -fsSL --retry 3 "$1" -o "$2"
    printf '%s  %s\n' "$3" "$2" | sha256sum -c -
}

fetch https://math.nist.gov/scimark/scimark4_c.zip scimark.zip \
    91c839bca629d01efe6c2fc298530efed330c5ff6aea7bee16fd2f452cb731b1
unzip -q scimark.zip
(
    cd scimark4
    common=(-O3 -static -no-pie -ffp-contract=off -fno-tree-vectorize)
    sources=(FFT.c kernel.c Stopwatch.c Random.c SOR.c SparseCompRow.c array.c MonteCarlo.c LU.c scimark4.c)
    gcc "${common[@]}" -march=i686 -mno-sse -mno-sse2 -mfpmath=387 "${sources[@]}" -lm -o "$bench/scimark-x87"
    gcc "${common[@]}" -march=pentium4 -msse2 -mfpmath=sse "${sources[@]}" -lm -o "$bench/scimark-sse2"
)

fetch https://downloads.sourceforge.net/project/polybench/polybench-c-4.2.1-beta.tar.gz polybench.tar.gz \
    426519ee8443a5f2175de6a3e9328cda8917a5e33053d0e8f59855ab56d689a4
tar -xzf polybench.tar.gz
poly=polybench-c-4.2.1-beta
gemm="$poly/linear-algebra/blas/gemm"
sed -i 's/"%0.2lf "/"%a "/' "$gemm/gemm.h"
gcc -O3 -static -no-pie -march=pentium4 -msse2 -mfpmath=sse -ffp-contract=off \
    -fno-inline -fno-ipa-cp -DNI=320 -DNJ=384 -DNK=384 \
    -DPOLYBENCH_TIME -DPOLYBENCH_NO_FLUSH_CACHE -DPOLYBENCH_DUMP_ARRAYS \
    -fopt-info-vec-optimized="$bench/expected/gemm-vectorization.txt" \
    -I "$poly/utilities" "$poly/utilities/polybench.c" "$gemm/gemm.c" -lm -o "$bench/gemm"
grep -q 'gemm.c:.*vectorized' "$bench/expected/gemm-vectorization.txt"

fetch https://unofficial-builds.nodejs.org/download/release/v14.21.3/node-v14.21.3-linux-x86.tar.xz node.tar.xz \
    28e618444e84b7b804487690a09dd5f2a1da96be383ef612ce5a531a3a2df3e3
tar -xJOf node.tar.xz node-v14.21.3-linux-x86/bin/node > "$bench/node"
chmod +x "$bench/node"

python3 make-fixture.py "$bench" cases.json
(
    cd "$bench"
    sha256sum input/sha.bin > expected/sha256sum
    sort --parallel=1 -S 96M input/sort.txt -o out/sort.txt
    sha256sum out/sort.txt > expected/sort
    gzip -n -6 -c input/sort.txt | sha256sum > expected/gzip
    ./gemm > /dev/null 2> out/gemm.txt
    sha256sum out/gemm.txt > expected/gemm
    rm out/{gemm,sort}.txt
    gcc --version | head -n 1 > expected/guest-compiler
    dpkg-query -W coreutils gzip libc6 libstdc++6 linux-image-686-pae > expected/guest-packages
)
