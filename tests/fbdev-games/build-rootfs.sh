#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 || ${2:-x11} != x11 ]]; then
    echo "Usage: $0 ROOTFS [x11]" >&2
    exit 2
fi

script_dir=$(cd -- "$(dirname -- "$0")" && pwd)
manifest="$script_dir/packages.sha256"
if [[ ${2:-} == x11 ]]; then
    manifest="$script_dir/x11-packages.sha256"
fi
if [[ ! -s "$manifest" ]]; then
    echo "Package manifest is missing or empty: $manifest" >&2
    exit 1
fi

rootfs=$1
if [[ -e "$rootfs" || -L "$rootfs" ]]; then
    echo "Root filesystem destination already exists: $rootfs" >&2
    exit 2
fi

mkdir -p -- "$(dirname -- "$rootfs")"
staging=$(mktemp -d "${rootfs}.XXXXXX")
trap 'rm -rf -- "$staging"' EXIT
mkdir -p "$staging/rootfs" "$staging/packages"

while read -r checksum path; do
    (
        package="$staging/packages/${path##*/}"
        curl --fail --silent --show-error --location --retry 3 \
            "https://archive.debian.org/debian/$path" --output "$package"
        printf '%s  %s\n' "$checksum" "$package" | shasum -a 256 --check
    ) &
    pids+=("$!")
    if (( ${#pids[@]} >= 8 )); then
        for pid in "${pids[@]}"; do
            wait "$pid"
        done
        pids=()
    fi
done < "$manifest"
for pid in "${pids[@]}"; do
    wait "$pid"
done

while read -r checksum path; do
    package="$staging/packages/${path##*/}"
    data=$(ar t "$package" | sed -n '/^data\.tar\./p')
    ar p "$package" "$data" | tar -xf - -C "$staging/rootfs"
done < "$manifest"

mkdir -p "$staging/rootfs"/{bin,dev,proc,root,sys,tmp,var/log,var/lib/xkb}
chmod 1777 "$staging/rootfs/tmp"
ln -s busybox "$staging/rootfs/bin/sh"
if [[ ${2:-} == x11 ]]; then
    mkdir -p "$staging/rootfs"/{etc/X11,usr/local/bin}
    install -m 644 "$staging/rootfs/usr/share/base-passwd/passwd.master" "$staging/rootfs/etc/passwd"
    install -m 644 "$staging/rootfs/usr/share/base-passwd/group.master" "$staging/rootfs/etc/group"
    install -m 644 "$script_dir/openbox-menu.xml" "$staging/rootfs/etc/xdg/openbox/menu.xml"
    install -m 644 "$script_dir/xorg.conf" "$staging/rootfs/etc/X11/xorg.conf"
    install -m 755 "$script_dir/x11-session.sh" "$staging/rootfs/usr/local/bin/x11-session"
fi
mv -- "$staging/rootfs" "$rootfs"
