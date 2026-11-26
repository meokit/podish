#!/bin/sh
set -eu
export LC_ALL=C
if [ "$#" != 2 ]; then
    echo "usage: $0 <work directory containing gzip-1.12.tar.xz> <i386|aarch64>" >&2
    exit 2
fi
base=$(cd -- "$1" && pwd)
arch=$2
case "$arch" in
    i386) triple=x86-linux-gnu.2.36; host=i686-linux-gnu; march=pentium4 ;;
    aarch64) triple=aarch64-linux-gnu.2.36; host=aarch64-linux-gnu; march=generic ;;
    *) exit 2 ;;
esac
cd "$base"
python3 - "$base/gzip-1.12.tar.xz" <<'PY'
import hashlib, sys
from pathlib import Path
if hashlib.sha256(Path(sys.argv[1]).read_bytes()).hexdigest() != "ce5e03e519f637e1f814011ace35c4f87b33c0bbabeec35baf5fbd3479e91956":
    raise ValueError("gzip-1.12 source checksum mismatch")
PY
tar -xJf gzip-1.12.tar.xz
src=$base/gzip-1.12
mkdir -p "$base/bin/$arch" "$base/build-$arch"
cd "$base/build-$arch"
export CC="zig cc -target $triple -march=$march" AR="zig ar" RANLIB="zig ranlib"
"$src/configure" --disable-dependency-tracking --build="$("$src/build-aux/config.guess")" --host="$host" CFLAGS=-O1
for opt in O1 O3; do
    "${MAKE:-gmake}" -s clean
    # Zig 0.15.2 defaults to cc1 -O2 for both driver flags. Set the final cc1 option explicitly.
    "${MAKE:-gmake}" -j4 V=1 CFLAGS="-$opt -Xclang -$opt"
    cp gzip "$base/bin/$arch/gzip-$opt"
done
