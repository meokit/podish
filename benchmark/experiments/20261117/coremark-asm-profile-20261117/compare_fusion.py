import hashlib,json,pathlib,re,statistics,subprocess,time
out=pathlib.Path(__file__).resolve().parent;repo=out.parents[1]
root=repo/'artifacts/coremark-native-compare-20261117/rootfs'
variants={'current64':out/'baseline-cli','old256':out/'old256-cli'}
result={'method':'Three alternating paired rounds; only native library/table differs; current engine source; identical static i686 ELF, Zig -O3, same auto-calibrated runtime args, >=10s timed; single context, 2K performance seeds. Old generated source from 20771dae^ with relative includes adapted for external-source placement. No mainline source changed.','samples':[],'library_sha256':{name:hashlib.sha256((folder/'libfibercpu.dylib').read_bytes()).hexdigest() for name,folder in variants.items()}}
assert len({hashlib.sha256((folder/'Podish.Cli.dll').read_bytes()).hexdigest() for folder in variants.values()})==1
for round_number in range(1,4):
 for variant in (['current64','old256'] if round_number%2 else ['old256','current64']):
  cmd=['dotnet',str(variants[variant]/'Podish.Cli.dll'),'run','--rm','--log-driver','none','--rootfs',str(root),'/coremark_i686_linux','0x0','0x0','0x66','0','7','1','2000']
  print('Starting',round_number,variant,flush=True);t=time.perf_counter();p=subprocess.run(cmd,cwd=repo,text=True,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=240);wall=time.perf_counter()-t
  log=f'fusion-round-{round_number}-{variant}.log';(out/log).write_text(p.stdout)
  if p.returncode or 'Correct operation validated' not in p.stdout:raise RuntimeError(p.stdout)
  def num(label):return float(re.search('^'+re.escape(label)+r'\s*:\s*([0-9.]+)',p.stdout,re.M).group(1))
  seconds=num('Total time (secs)');assert seconds>=10
  score=num('Iterations/Sec');sample={'round':round_number,'variant':variant,'score':score,'iterations':int(num('Iterations')),'timed_seconds':seconds,'wall_seconds':wall,'log':log,'validated':True}
  result['samples'].append(sample);(out/'fusion-comparison.json').write_text(json.dumps(result,indent=2)+'\n');print(sample,flush=True)
med={v:statistics.median(x['score'] for x in result['samples'] if x['variant']==v) for v in variants}
result['summary']={'median_scores':med,'old256_speedup_percent':(med['old256']/med['current64']-1)*100,'current64_slowdown_vs_old256_percent':(1-med['current64']/med['old256'])*100}
(out/'fusion-comparison.json').write_text(json.dumps(result,indent=2)+'\n');print(result['summary'],flush=True)
