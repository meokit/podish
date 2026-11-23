import hashlib
import json
from pathlib import Path
import random
import shlex
import sys

root = Path(sys.argv[1])
cases = json.loads(Path(sys.argv[2]).read_text())

(root / "input/sha.bin").write_bytes(hashlib.shake_256(b"Podish benchmark SHA-256 v1").digest(64 * 1024 * 1024))
rng = random.Random(20261123)
with (root / "input/sort.txt").open("w", encoding="ascii") as output:
    for index in range(262144):
        output.write(f"{rng.getrandbits(64):016x} {index:07d} {'x' * 38}\n")

sources = root / "webpack/src"
sources.mkdir(parents=True)
imports = []
for index in range(160):
    source = sources / f"chunk-{index}.js"
    data = hashlib.shake_256(f"Podish bundle node {index}".encode()).hexdigest(2304)
    source.write_text(f"export default () => '{data}'.slice({index % 32});\n")
    imports.append(f"import chunk{index} from './{source.name}';\n")
calls = ", ".join(f"chunk{index}()" for index in range(len(imports)))
(sources / "entry.js").write_text("".join(imports) + f"console.log([{calls}].join(','));\n")

commands = ["case \"$bench_case\" in"]
for case, argv in cases.items():
    command = " ".join('"${bench_iterations}"' if arg == "{iterations}" else shlex.quote(arg) for arg in argv)
    if case == "gzip":
        command += " > out/gzip.bin"
    elif case == "gemm":
        command += " 2> out/gemm.txt"
    commands.append(f"    {case}) {command} ;;")
commands.extend(['    *) exit 2 ;;', 'esac'])
(root / "system-cases.sh").write_text("\n".join(commands) + "\n")
