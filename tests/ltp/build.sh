#!/usr/bin/env bash
# Build selected upstream programs or the explicitly selected local exec suite.
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
local_suite=$(python3 - "$manifest" <<'PY'
import json, sys
suite = json.load(open(sys.argv[1])).get('localSuite', '')
if suite not in ('', 'exec'):
    raise SystemExit('Unsupported local suite: ' + repr(suite))
print(suite)
PY
)
if [[ "$local_suite" == exec ]]; then
    # Add our own directory only in the disposable build tree. No upstream
    # source, test assertion or checked-out submodule is modified.
    mkdir -p "$work/testcases/kernel/syscalls/podish_exec"
    cp "$here/exec/Makefile" "$here"/exec/*.c "$here"/exec/*.h \
        "$work/testcases/kernel/syscalls/podish_exec/"
fi
printf 'int main(void) { return 0; }\n' > "$work/probe.c"
gcc -m32 -static -o "$work/probe" "$work/probe.c"
cd -- "$work"
make autotools
CC='gcc -m32' CFLAGS='-O2 -g -march=i686 -mno-avx' LDFLAGS='-static' \
    ./configure --host=i686-linux-gnu --prefix=/opt/ltp --without-numa
make -j"${LTP_JOBS:-4}" include-all lib-all
mkdir -p "$out/bin" "$out/testcases/bin"
python3 - "$manifest" > "$work/selection" <<'PY'
import json, re, sys
cases = json.load(open(sys.argv[1]))['cases']
seen = set()
resources = set()
for case in cases:
    for key in ('name', 'directory'):
        if not re.fullmatch(r'[A-Za-z0-9_]+', case[key]):
            raise SystemExit('Invalid selection: ' + repr(case))
    if case['name'] in seen:
        raise SystemExit('Duplicate case: ' + case['name'])
    if case['name'] in resources:
        raise SystemExit('Case name collides with resource: ' + case['name'])
    seen.add(case['name'])
    print('case', case['directory'], case['name'])
    for resource in case.get('resources', []):
        if not re.fullmatch(r'[A-Za-z0-9_]+', resource):
            raise SystemExit('Invalid resource: ' + repr(resource))
        if resource in seen:
            raise SystemExit('Resource name collides with case: ' + resource)
        if resource in resources:
            continue
        resources.add(resource)
        print('resource', case['directory'], resource)
PY
while read -r kind directory name; do
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
    if [[ "$kind" == resource ]]; then
        cp "testcases/kernel/syscalls/$directory/$name" "$out/testcases/bin/$name"
    fi
done < "$work/selection"
python3 - "$out" "$commit" "$manifest" "$work" <<'PY'
import hashlib, json, pathlib, subprocess, sys
out = pathlib.Path(sys.argv[1])
cases = json.load(open(sys.argv[3]))['cases']
resources = sorted({r for c in cases for r in c.get('resources', [])})
metadata = {
    'ltpCommit': sys.argv[2],
    'manifest': pathlib.Path(sys.argv[3]).name,
    'requiresRoot': bool(json.load(open(sys.argv[3])).get('requiresRoot')),
    'compiler': subprocess.check_output(['gcc', '--version'], text=True).splitlines()[0],
    'flags': '-m32 -static -O2 -g -march=i686 -mno-avx',
    'binaries': {c['name']: hashlib.sha256((out / 'bin' / c['name']).read_bytes()).hexdigest() for c in cases},
    'resources': {r: hashlib.sha256((out / 'bin' / r).read_bytes()).hexdigest() for r in resources},
}
if json.load(open(sys.argv[3])).get('localSuite') == 'exec':
    sources = pathlib.Path(sys.argv[4]) / 'testcases/kernel/syscalls/podish_exec'
    metadata['localSources'] = {
        p.name: hashlib.sha256(p.read_bytes()).hexdigest()
        for p in sorted(sources.iterdir())
        if p.is_file() and (p.name == 'Makefile' or p.suffix in ('.c', '.h'))
    }
(out / 'build.json').write_text(json.dumps(metadata, indent=2) + '\n')
print(f"Built {len(cases)} static i386 LTP executables: {out}")
PY
