#!/usr/bin/env bash
# Build only the selected, unmodified LTP programs, never the entire suite.
set -euo pipefail
here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd -- "$here/../.." && pwd)
out=${1:-"$repo/artifacts/ltp"}
manifest=${2:-"$here/cases.json"}
mkdir -p "$out"
out=$(cd -- "$out" && pwd)
[[ -f "$manifest" ]] || { echo "Missing case manifest: $manifest" >&2; exit 1; }
manifest=$(cd -- "$(dirname -- "$manifest")" && pwd)/$(basename -- "$manifest")
upstream="$here/upstream"
if [[ ! -f "$upstream/configure.ac" ]]; then
    echo 'Initialize LTP: git submodule update --init tests/ltp/upstream' >&2
    exit 1
fi
for tool in git gcc make autoconf automake aclocal python3 tar; do
    command -v "$tool" >/dev/null || { echo "Missing build tool: $tool" >&2; exit 1; }
done
commit=$(git -C "$upstream" rev-parse HEAD)
# Export committed LF sources into a private build tree. Windows git checkouts
# may have CRLF; neither generated files nor local patches enter the submodule.
cache=${LTP_BUILD_CACHE:-"${XDG_CACHE_HOME:-$HOME/.cache}/podish-ltp"}
mkdir -p "$cache"
work=$(mktemp -d "$cache/build-XXXXXXXX")
trap 'rm -rf -- "$work"' EXIT
git -C "$upstream" archive HEAD | tar -x -C "$work"
printf 'int main(void) { return 0; }\n' > "$work/probe.c"
gcc -m32 -static -o "$work/probe" "$work/probe.c"
cd -- "$work"
make autotools
CC='gcc -m32' CFLAGS='-O2 -g -march=i686 -mno-avx' LDFLAGS='-static' \
    ./configure --host=i686-linux-gnu --prefix=/opt/ltp --without-numa
make -j"${LTP_JOBS:-4}" include-all lib-all
mkdir -p "$out/bin"
python3 - "$manifest" > "$work/selection" <<'PY'
import json, re, sys
cases = json.load(open(sys.argv[1]))['cases']
seen = set()
for case in cases:
    for key in ('name', 'directory'):
        if not re.fullmatch(r'[A-Za-z0-9_]+', case[key]):
            raise SystemExit('Invalid selection: ' + repr(case))
    if case['name'] in seen:
        raise SystemExit('Duplicate case: ' + case['name'])
    seen.add(case['name'])
    print(case['directory'], case['name'])
PY
while read -r directory name; do
    make -C "testcases/kernel/syscalls/$directory" -j"${LTP_JOBS:-4}" "$name"
    # Reject accidental host-architecture or dynamically linked binaries.
    python3 - "testcases/kernel/syscalls/$directory/$name" <<'PY'
import struct, sys
data = open(sys.argv[1], 'rb').read()
if data[:5] != b'\x7fELF\x01' or struct.unpack_from('<H', data, 18)[0] != 3:
    raise SystemExit('Expected an ELF32 i386 executable: ' + sys.argv[1])
offset = struct.unpack_from('<I', data, 28)[0]
size, count = struct.unpack_from('<HH', data, 42)
if any(struct.unpack_from('<I', data, offset + i * size)[0] == 3 for i in range(count)):
    raise SystemExit('Unexpected ELF interpreter: ' + sys.argv[1])
PY
    cp "testcases/kernel/syscalls/$directory/$name" "$out/bin/$name"
done < "$work/selection"
python3 - "$out" "$commit" "$manifest" <<'PY'
import hashlib, json, pathlib, subprocess, sys
out = pathlib.Path(sys.argv[1])
cases = json.load(open(sys.argv[3]))['cases']
metadata = {
    'ltpCommit': sys.argv[2],
    'manifest': pathlib.Path(sys.argv[3]).name,
    'requiresRoot': bool(json.load(open(sys.argv[3])).get('requiresRoot')),
    'compiler': subprocess.check_output(['gcc', '--version'], text=True).splitlines()[0],
    'flags': '-m32 -static -O2 -g -march=i686 -mno-avx',
    'binaries': {c['name']: hashlib.sha256((out / 'bin' / c['name']).read_bytes()).hexdigest() for c in cases},
}
(out / 'build.json').write_text(json.dumps(metadata, indent=2) + '\n')
print(f"Built {len(cases)} static i386 LTP executables: {out}")
PY
