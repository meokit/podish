#!/usr/bin/env bash
# Build only the independent backend preflight. LTP binaries/metadata stay unchanged.
set -euo pipefail
here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
out=${1:-"$here/../../artifacts/memory-mapping"}
mkdir -p "$out"
gcc -m32 -static -O2 -g -march=i686 -mno-avx -Wall -Wextra -Werror \
    "$here/backend_probe.c" -o "$out/podish-fs-probe"
python3 - "$here/backend_probe.c" "$out" <<'PY'
import hashlib, json, pathlib, struct, subprocess, sys
source, out = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
data = (out / 'podish-fs-probe').read_bytes()
if data[:5] != b'\x7fELF\x01' or struct.unpack_from('<H', data, 18)[0] != 3:
    raise SystemExit('Expected ELF32 i386')
offset = struct.unpack_from('<I', data, 28)[0]
size, count = struct.unpack_from('<HH', data, 42)
if any(struct.unpack_from('<I', data, offset + i * size)[0] == 3 for i in range(count)):
    raise SystemExit('Unexpected ELF interpreter')
metadata = {'sourceSha256': hashlib.sha256(source.read_bytes()).hexdigest(),
            'binarySha256': hashlib.sha256(data).hexdigest(),
            'compiler': subprocess.check_output(['gcc', '--version'], text=True).splitlines()[0],
            'flags': '-m32 -static -O2 -g -march=i686 -mno-avx -Wall -Wextra -Werror'}
(out / 'probe-build.json').write_text(json.dumps(metadata, indent=2) + '\n')
print(out / 'podish-fs-probe')
PY
