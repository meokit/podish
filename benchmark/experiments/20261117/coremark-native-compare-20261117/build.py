import hashlib, json, os, pathlib, shutil, subprocess
out = pathlib.Path(__file__).resolve().parent
source = out / 'upstream'
src = out / 'source'
shutil.copytree(source, src, dirs_exist_ok=True, ignore=shutil.ignore_patterns('.git', 'docs', '*.exe', '*.o', '*.log'))
for line in (src/'coremark.md5').read_text().splitlines():
    digest, name = line.split()
    if name.endswith('.c'): assert hashlib.md5((src/name).read_bytes()).hexdigest() == digest, name
files = ['core_list_join.c', 'core_main.c', 'core_matrix.c', 'core_state.c', 'core_util.c', 'posix/core_portme.c']
common = ['-O3', '-DPERFORMANCE_RUN=1', '-DITERATIONS=0', '-DMULTITHREAD=1', '-DTOTAL_DATA_SIZE=2000', '-Iposix', '-I.']
zig = shutil.which('zig')
env = dict(os.environ, ZIG_GLOBAL_CACHE_DIR=str(out/'zig-global-cache'), ZIG_LOCAL_CACHE_DIR=str(out/'zig-local-cache'))
commands = {}
for name, flags, binary in [('ia32', ['-target', 'x86-linux-musl', '-march=i686', '-static'], out/'rootfs/coremark_i686_linux'), ('native', ['-target', 'aarch64-macos', '-mcpu=native'], out/'coremark_arm64_macos')]:
    binary.parent.mkdir(exist_ok=True, parents=True)
    cmd = [zig, 'cc'] + flags + common + [f'-DFLAGS_STR="zig cc {" ".join(flags + common)}"'] + files + ['-o', str(binary)]
    commands[name] = cmd
    print('Building', name, flush=True)
    result = subprocess.run(cmd, cwd=src, env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    (out/f'build-{name}.log').write_text(result.stdout)
    if result.returncode: raise RuntimeError(result.stdout)
metadata = {'zig': subprocess.check_output([zig, 'version'], text=True).strip(), 'source_origin': str(source), 'source_revision': subprocess.check_output(['git','-C',str(source),'rev-parse','HEAD'],text=True).strip(), 'core_c_files_match_upstream_md5_manifest': True, 'source_sha256': {f:hashlib.sha256((src/f).read_bytes()).hexdigest() for f in files+['coremark.h','posix/core_portme.h']}, 'build_commands': commands, 'engine_commit':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip()}
(out/'build-metadata.json').write_text(json.dumps(metadata, indent=2)+'\n')
print('Both builds complete', flush=True)
