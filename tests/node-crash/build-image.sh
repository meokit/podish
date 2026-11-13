#!/usr/bin/env bash
# Build the Alpine 3.12 x86 (i686) rootfs that carries Node.js 14.5.0 for the
# Podish crash reproducer.
#
# Podish emulates a 32-bit x86 Linux userland, so the matching guest ABI is the
# Alpine *x86* (i686) port with musl. Node.js 14.5.0 (nodejs-current) is the
# newest Node that Alpine still ships for 32-bit x86.
#
# The scratch tree must live on a Windows-visible path: WSL wipes /tmp when the
# distribution stops, and this script runs in several invocations.
#
# Usage: bash build-image.sh [--inspect]
set -euo pipefail

WIN_BASE=${WIN_BASE:-/mnt/c/Users/Jiang/AppData/Local/Temp/opencode/node-repro}
SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/../.." && pwd)
WORK=$WIN_BASE/wsl-work
ROOT=$WORK/rootfs
APKS=$WORK/apks
MIRROR=https://dl-cdn.alpinelinux.org/alpine
VERSION=3.12
ARCH=x86

mkdir -p "$WORK" "$APKS"

if [ "${1:-}" != "--inspect" ]; then
    rm -rf "$ROOT"
    mkdir -p "$ROOT"

    echo "==> minirootfs"
    MINI=$MIRROR/v$VERSION/releases/$ARCH/alpine-minirootfs-3.12.12-$ARCH.tar.gz
    [ -f "$WORK/minirootfs.tar.gz" ] || curl -fsSL -o "$WORK/minirootfs.tar.gz" "$MINI"
    tar -xzf "$WORK/minirootfs.tar.gz" -C "$ROOT"

    echo "==> packages"
    for entry in \
        "main musl" \
        "main busybox" \
        "community nodejs-current" \
        "main brotli-libs" \
        "main c-ares" \
        "main openssl" \
        "main libgcc" \
        "main nghttp2-libs" \
        "main libstdc++" \
        "main libuv" \
        "main zlib" \
        "main ca-certificates"; do
        repo=${entry%% *}
        name=${entry##* }
        index="$WORK/APKINDEX-$repo.tar.gz"
        [ -f "$index" ] || curl -fsSL -o "$index" "$MIRROR/v$VERSION/$repo/$ARCH/APKINDEX.tar.gz"
        # Do not let awk exit early: with `set -o pipefail` an early exit makes
        # tar die with SIGPIPE and the whole pipeline look failed.
        version=$(tar -xzOf "$index" APKINDEX 2>/dev/null | \
            awk -v pkg="$name" 'BEGIN{RS="";FS="\n";found=0} {p="";v="";for(i=1;i<=NF;i++){if($i ~ /^P:/)p=substr($i,3);if($i ~ /^V:/)v=substr($i,3)} if(!found && p==pkg && v!=""){found=1; print v}}')
        [ -n "$version" ] || { echo "  cannot resolve $name"; exit 1; }
        file="$name-$version.apk"
        [ -f "$APKS/$file" ] || curl -fsSL -o "$APKS/$file" "$MIRROR/v$VERSION/$repo/$ARCH/$file"
        # An apk is three concatenated gzip tar streams: signature, control, data.
        # Only the data stream matters; control files are removed afterwards.
        tar -xzf "$APKS/$file" -C "$ROOT" 2>/dev/null || true
        echo "    $file"
    done

    rm -f "$ROOT"/.PKGINFO
    find "$ROOT" -maxdepth 1 -name '.SIGN.*' -delete 2>/dev/null || true
    chmod 0755 "$ROOT/usr/bin/node" 2>/dev/null || true

    echo "==> reproducer payload"
    mkdir -p "$ROOT/app"
    cp "$REPO_ROOT/tests/node-crash/repro.js" "$ROOT/app/repro.js"

    echo "==> archive"
    # NOTE: do not use "tar -C $ROOT ." here. GNU tar then stores every entry as
    # "./usr/bin/node", and Podish's OCI layer index builder keeps that prefix,
    # so the guest resolves "/./usr/bin/node" instead of "/usr/bin/node".
    cd "$ROOT"
    tar --numeric-owner --format=gnu -cf "$WIN_BASE/node-rootfs.tar" $(ls -A)
    ls -l "$WIN_BASE/node-rootfs.tar"
fi

echo "== required shared objects =="
for f in libbrotlidec.so.1 libbrotlienc.so.1 libcares.so.2 libcrypto.so.1.1 \
         libgcc_s.so.1 libnghttp2.so.14 libssl.so.1.1 libstdc++.so.6 \
         libuv.so.1 libz.so.1 libc.musl-x86.so.1 ld-musl-i386.so.1; do
    hit=$(find "$ROOT/lib" "$ROOT/usr/lib" -maxdepth 1 -name "$f" 2>/dev/null | head -1)
    printf '%-24s %s\n' "$f" "${hit:-MISSING}"
done
echo "== node PT_INTERP =="
readelf -l "$ROOT/usr/bin/node" | grep -A1 INTERP || true
