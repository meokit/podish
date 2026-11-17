import hashlib,json,pathlib,re,statistics,subprocess,time
out=pathlib.Path(__file__).resolve().parent;repo=out.parents[1]
root=repo/'artifacts/coremark-native-compare-20261117/rootfs'
variants={'current_zig_static':out/'baseline-cli','preserve_none_zig_static':out/'abi-cli','current_gcc_dynamic':out/'baseline-cli'}
result={'method':'Three rotating-order rounds, one process at a time, >=10s timed, identical performance seeds and 2K data. ABI experiment only enables GNU preserve_none probe for aarch64 in a source copy; current default fusion table retained. GCC case uses existing GCC 15.2.0 dynamic ELF plus its musl loader: guest binary/toolchain/linkage differ, so this is a workload comparability check, not an isolated linkage test. No tracked source changed.','samples':[],'library_sha256':{name:hashlib.sha256((folder/'libfibercpu.dylib').read_bytes()).hexdigest() for name,folder in variants.items()}}
assert len({hashlib.sha256((folder/'Podish.Cli.dll').read_bytes()).hexdigest() for folder in variants.values()})==1
for round_number in range(1,4):
 for variant in (list(variants)[round_number-1:]+list(variants)[:round_number-1]):
  cmd=['dotnet',str(variants[variant]/'Podish.Cli.dll'),'run','--rm','--log-driver','none','--rootfs',str(out/'gcc-rootfs' if variant=='current_gcc_dynamic' else root),'/coremark_gcc_i386' if variant=='current_gcc_dynamic' else '/coremark_i686_linux','0x0','0x0','0x66','0','7','1','2000']
  print('Starting',round_number,variant,flush=True);t=time.perf_counter();p=subprocess.run(cmd,cwd=repo,text=True,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=240);wall=time.perf_counter()-t
  log=f'abi-guest-round-{round_number}-{variant}.log';(out/log).write_text(p.stdout)
  if p.returncode or 'Correct operation validated' not in p.stdout:raise RuntimeError(p.stdout)
  def num(label):return float(re.search('^'+re.escape(label)+r'\s*:\s*([0-9.]+)',p.stdout,re.M).group(1))
  seconds=num('Total time (secs)');assert seconds>=10
  score=num('Iterations/Sec');sample={'round':round_number,'variant':variant,'score':score,'iterations':int(num('Iterations')),'timed_seconds':seconds,'wall_seconds':wall,'log':log,'validated':True}
  result['samples'].append(sample);(out/'abi-guest-comparison.json').write_text(json.dumps(result,indent=2)+'\n');print(sample,flush=True)
med={v:statistics.median(x['score'] for x in result['samples'] if x['variant']==v) for v in variants}
result['summary']={'median_scores':med,'preserve_none_speedup_percent':(med['preserve_none_zig_static']/med['current_zig_static']-1)*100,'gcc_guest_speedup_percent':(med['current_gcc_dynamic']/med['current_zig_static']-1)*100}
(out/'abi-guest-comparison.json').write_text(json.dumps(result,indent=2)+'\n');print(result['summary'],flush=True)
