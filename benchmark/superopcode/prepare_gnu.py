#!/usr/bin/env python3
"""Explicit online preparation of a pinned GNU/i386 training rootfs (no apk install hooks).

Normal training/builds never download. --update-lock is an explicit maintainer action.
Package hashes are retained in packages.lock.json; upstream packages retain licenses.
"""
import argparse
import gzip
import hashlib
import io
import json
import pathlib
import re
import tarfile
import urllib.request

HERE = pathlib.Path(__file__).resolve().parent
REPO = 'https://dl-cdn.alpinelinux.org/alpine/v3.20/main/x86/'


def fetch(url):
    with urllib.request.urlopen(url, timeout=90) as response:
        return response.read()


def entries(data):
    with tarfile.open(fileobj=io.BytesIO(gzip.decompress(data)), mode='r:', ignore_zeros=True) as archive:
        for entry in archive:
            yield entry, archive.extractfile(entry).read() if entry.isfile() else None


def update_lock(path):
    index = next(data for entry, data in entries(fetch(REPO + 'APKINDEX.tar.gz')) if entry.name == 'APKINDEX')
    packages = {}
    providers = {}
    for paragraph in index.decode().split('\n\n'):
        fields = dict(line.split(':', 1) for line in paragraph.splitlines() if ':' in line)
        if 'P' not in fields:
            continue
        packages[fields['P']] = fields
        for provided in fields.get('p', '').split():
            providers[provided.split('=')[0]] = fields['P']
    selected = {}
    pending = ['bash', 'tar', 'gzip', 'sed', 'grep', 'coreutils']
    while pending:
        dependency = pending.pop()
        name = re.split('[<>=~]', dependency)[0]
        if name.startswith('!'):
            continue
        name = name if name in packages else providers[name]
        if name in selected:
            continue
        package = packages[name]
        url = REPO + name + '-' + package['V'] + '.apk'
        data = fetch(url)
        selected[name] = dict(name=name, version=package['V'], url=url,
                              sha256=hashlib.sha256(data).hexdigest(), license=package.get('L', ''))
        pending.extend(package.get('D', '').split())
    path.write_text(json.dumps(dict(schemaVersion=1, architecture='i386',
                                   packages=sorted(selected.values(), key=lambda x: x['name'])), indent=2) + '\n', encoding='utf-8')


def prepare(lock, destination):
    manifest = json.loads(lock.read_text(encoding='utf-8'))
    destination.mkdir(parents=True, exist_ok=True)
    if any(destination.iterdir()):
        raise SystemExit('Use a new empty rootfs directory; existing files are never overwritten.')
    links = {}
    for package in manifest['packages']:
        data = fetch(package['url'])
        if hashlib.sha256(data).hexdigest() != package['sha256']:
            raise SystemExit('Package hash mismatch: ' + package['name'])
        for entry, content in entries(data):
            name = entry.name.removeprefix('./')
            if name.startswith('.') or not name:
                continue
            relative = pathlib.PurePosixPath(name)
            if relative.is_absolute() or '..' in relative.parts:
                raise SystemExit('Unsafe archive path: ' + name)
            output = destination.joinpath(*relative.parts)
            if entry.isdir():
                output.mkdir(parents=True, exist_ok=True)
            elif entry.isfile():
                output.parent.mkdir(parents=True, exist_ok=True)
                output.write_bytes(content)
                output.chmod(entry.mode)
            elif entry.issym() or entry.islnk():
                links[name] = entry.linkname if entry.issym() else '/' + entry.linkname
    # Materialize aliases as ordinary files: Windows does not need symlink privileges.
    def resolve(name, seen):
        if name not in links:
            return destination.joinpath(*pathlib.PurePosixPath(name).parts)
        if name in seen:
            raise SystemExit('Link cycle: ' + name)
        import posixpath
        target = links[name]
        target = posixpath.normpath(target.lstrip('/') if target.startswith('/') else posixpath.join(posixpath.dirname(name), target))
        if target == '..' or target.startswith('../'):
            raise SystemExit('Link escapes root: ' + name)
        return resolve(target, seen | {name})
    for name in links:
        source = resolve(name, set())
        if source.is_file():
            output = destination.joinpath(*pathlib.PurePosixPath(name).parts)
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_bytes(source.read_bytes())
            output.chmod(0o755)
    for directory in ('tmp', 'dev', 'proc', 'etc', 'run', 'work'):
        (destination / directory).mkdir(exist_ok=True)
    (destination / 'bin/sh').write_bytes((destination / 'bin/bash').read_bytes())
    (destination / 'bin/sh').chmod(0o755)
    (destination / 'etc/passwd').write_text('root:x:0:0:root:/:/bin/bash\n', encoding='utf-8')
    (destination / 'etc/group').write_text('root:x:0:\n', encoding='utf-8')
    (destination / 'packages.lock.json').write_bytes(lock.read_bytes())
    files = {p.relative_to(destination).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
             for p in sorted(destination.rglob('*')) if p.is_file()}
    (destination / 'rootfs.lock.json').write_text(json.dumps(files, indent=2) + '\n', encoding='utf-8')
    print(f'Prepared {len(files)} pinned GNU/i386 files in {destination}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--lock', type=pathlib.Path, default=HERE / 'packages.lock.json')
    parser.add_argument('--update-lock', action='store_true')
    parser.add_argument('--output', type=pathlib.Path)
    args = parser.parse_args()
    if args.update_lock:
        update_lock(args.lock)
    if args.output:
        prepare(args.lock, args.output.resolve())
