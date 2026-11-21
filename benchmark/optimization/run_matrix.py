#!/usr/bin/env python3
"""Serial GCC CoreMark / GNU holdout validation and timing. Build all libraries first."""
import argparse
import gzip
import hashlib
import json
import pathlib
import random
import re
import statistics
import subprocess
import tarfile
import time
import platform
from datetime import datetime, timezone

GCC_SHA256 = '48c3ecf2c2e2bbbebc8249f3f2ab30933a1e85e8eeaa1b6c0806bed90a5085db'
COMMANDS = {
    'gzip': '/bin/gzip -n -6 -c input.txt > result.gz',
    'gunzip': '/bin/gzip -dc input.gz > result.txt',
    'sed': "/bin/sed -e 's/row=/record=/g' -e 's/otherxx/plainxx/g' input.txt > result.txt",
    'grep_fixed': '/bin/grep -F -c needle input.txt > result.txt',
    'grep_regex': "/bin/grep -E -c 'group=(07|13|42)' input.txt > result.txt",
    'sort': '/usr/bin/sort input.txt > result.txt',
    'sha256': '/usr/bin/sha256sum input.txt > result.txt',
    'tar': '/bin/tar --format=ustar -cf result.tar tar-input',
}
ITERATIONS = dict(gzip=7, gunzip=33, sed=7, grep_fixed=79, grep_regex=51, sort=19, sha256=36, tar=82)

def sha(data):
    return hashlib.sha256(data).hexdigest()

def prepare_fixture(work, seed=987654, fixture_bytes=2 * 1024 * 1024):
    work.mkdir(parents=True, exist_ok=True)
    rng = random.Random(seed)
    lines, size = [], 0
    while size < fixture_bytes:
        i = len(lines)
        line = f'row={i:07d} group={i % 97:02d} ' + ('needle ' if i % 7 == 0 else 'otherxx ') + ''.join(rng.choices('abcdefghijklmnopqrstuvwxyz0123456789', k=32)) + '\n'
        lines.append(line)
        size += len(line)
    text = ''.join(lines)
    data = text.encode()
    (work/'input.txt').write_bytes(data)
    (work/'input.gz').write_bytes(gzip.compress(data, mtime=0))
    (work/'tar-input').mkdir(exist_ok=True)
    # Preserve the original 2 MiB fixture recipe exactly. Larger holdouts span the full input.
    chunk = 131072 if fixture_bytes == 2 * 1024 * 1024 else (len(data) + 15) // 16
    for i in range(16):
        (work/'tar-input'/f'{i:02d}.txt').write_bytes(data[i*chunk:(i+1)*chunk])
    expected = {
        'seed': seed, 'bytes': len(data), 'input_sha256': sha(data),
        'sed': sha(text.replace('row=', 'record=').replace('otherxx', 'plainxx').encode()),
        'sort': sha(''.join(sorted(lines)).encode()),
        'tar_member_bytes': [(work/'tar-input'/f'{i:02d}.txt').stat().st_size for i in range(16)],
        'grep_fixed': str(sum('needle' in line for line in lines))+'\n',
        'grep_regex': str(sum(re.search('group=(07|13|42)', line) is not None for line in lines))+'\n',
    }
    return expected

def prepare(out, seed=987654, fixture_bytes=2 * 1024 * 1024):
    expected = prepare_fixture(out / 'rootfs/work/manual', seed, fixture_bytes)
    (out/'fixture.json').write_text(json.dumps(expected, indent=2)+'\n')

def validate_gnu_output(work, case, expected):
    if case == 'gzip': assert sha(gzip.decompress((work/'result.gz').read_bytes())) == expected['input_sha256']
    elif case == 'gunzip': assert sha((work/'result.txt').read_bytes()) == expected['input_sha256']
    elif case in ('sed', 'sort'): assert sha((work/'result.txt').read_bytes()) == expected[case]
    elif case in ('grep_fixed', 'grep_regex'): assert (work/'result.txt').read_text() == expected[case]
    elif case == 'sha256': assert (work/'result.txt').read_text().split()[0] == expected['input_sha256']
    elif case == 'tar':
        with tarfile.open(work/'result.tar') as archive:
            members = [entry for entry in archive.getmembers() if entry.isfile()]
            assert len(members) == 16
            for entry in members: assert archive.extractfile(entry).read() == (work/entry.name).read_bytes()

def run(out, case, variant, round_index, scale, core_iterations):
    root = out/'rootfs'
    work = root/'work/manual'
    expected = json.loads((out/'fixture.json').read_text())
    cmd = ['dotnet', str(out/(variant+'-cli')/'Podish.Cli.dll'), 'run', '--rm', '--log-driver', 'none', '--rootfs', str(root), '--env', 'LC_ALL=C', '--env', 'LANG=C', '--env', 'TZ=UTC']
    if case == 'coremark_gcc':
        assert sha((root/'work/coremark_gcc').read_bytes()) == GCC_SHA256
        n = core_iterations
        cmd += ['/work/coremark_gcc', '0x0', '0x0', '0x66', str(n), '7', '1', '2000']
    else:
        n = max(1, round(ITERATIONS[case]*scale))
        script = "set -eu\ncd /work/manual\nprintf '__BEGIN__\\n'\nfor ((i=0;i<"+str(n)+";i++)); do\n"+COMMANDS[case]+"\ndone\nprintf '__END__\\n'\n"
        cmd += ['/bin/bash', '--noprofile', '--norc', '-c', script]
    start, end, lines = None, None, []
    wall = time.perf_counter()
    with subprocess.Popen(cmd, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, bufsize=1) as process:
        for line in process.stdout:
            lines.append(line)
            if line.strip() == '__BEGIN__': start = time.perf_counter()
            if line.strip() == '__END__': end = time.perf_counter()
        code = process.wait(timeout=300)
    wall = time.perf_counter()-wall
    output = ''.join(lines)
    log = f'{round_index}-{variant}-{case}.log'
    (out/log).write_text(output)
    assert code == 0, output
    result = dict(case=case, variant=variant, round=round_index, iterations=n, wall_seconds=wall, log=log)
    if case == 'coremark_gcc':
        assert 'Correct operation validated' in output, output
        result['seconds'] = float(re.search(r'Total time \(secs\):\s*([0-9.]+)', output)[1])
        result['score'] = float(re.search(r'Iterations/Sec\s*:\s*([0-9.]+)', output)[1])
        assert result['seconds'] >= 10, output
    else:
        assert start is not None and end is not None, output
        result['seconds'] = end-start
        validate_gnu_output(work, case, expected)
    result['validated'] = True
    return result

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=pathlib.Path, required=True)
    parser.add_argument('--variants', nargs='+', default=['base', 'shapes', 'pinstatic'])
    parser.add_argument('--cases', nargs='+', default=['coremark_gcc', *COMMANDS])
    parser.add_argument('--rounds', type=int, default=3)
    parser.add_argument('--scale', type=float, default=1)
    parser.add_argument('--core-iterations', type=int, default=60000)
    parser.add_argument('--tag', default='results')
    parser.add_argument('--prepare', action='store_true')
    parser.add_argument('--seed', type=int, default=987654)
    parser.add_argument('--fixture-bytes', type=int, default=2 * 1024 * 1024)
    args = parser.parse_args()
    out = args.out.resolve()
    if args.prepare: prepare(out, args.seed, args.fixture_bytes)
    results = []
    destination = out/(args.tag+'.json')
    assert not destination.exists(), f'Refusing to overwrite {destination}'
    identities = {}
    for variant in args.variants:
        cli = out/(variant+'-cli')
        identities[variant] = {name: sha((cli/name).read_bytes()) for name in ('Podish.Cli.dll', 'libfibercpu.dylib')}
    assert len({value['Podish.Cli.dll'] for value in identities.values()}) == 1, 'Managed CLI differs between variants'
    metadata = dict(utc=datetime.now(timezone.utc).isoformat(), host=platform.platform(), gcc_sha256=GCC_SHA256,
                    variants=identities, cases=args.cases, rounds=args.rounds, scale=args.scale,
                    core_iterations=args.core_iterations, fixture=json.loads((out/'fixture.json').read_text()))
    (out/(args.tag+'-metadata.json')).write_text(json.dumps(metadata, indent=2)+'\n')
    for rnd in range(args.rounds):
        order = args.variants[rnd % len(args.variants):]+args.variants[:rnd % len(args.variants)]
        for case in args.cases:
            for variant in order:
                result = run(out, case, variant, f'{args.tag}-{rnd}', args.scale, args.core_iterations)
                results.append(result)
                destination.write_text(json.dumps(results, indent=2)+'\n')
                print(json.dumps(result), flush=True)
    baseline = args.variants[0]
    for case in args.cases:
        values = {variant: statistics.median(r['score'] if case == 'coremark_gcc' else r['iterations']/r['seconds'] for r in results if r['case']==case and r['variant']==variant) for variant in args.variants}
        print(case, {variant: round(100*(value/values[baseline]-1), 3) for variant, value in values.items()}, flush=True)

if __name__ == '__main__': main()
