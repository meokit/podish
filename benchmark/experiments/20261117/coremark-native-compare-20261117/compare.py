import datetime, hashlib, json, pathlib, platform, re, statistics, subprocess, time
out=pathlib.Path(__file__).resolve().parent
repo=out.parents[1]
root=out/'rootfs'
(root/'tmp').mkdir(exist_ok=True)
cli=repo/'Podish.Cli/bin/Release/net10.0/Podish.Cli.dll'
args=['0x0','0x0','0x66','0','7','1','2000']
commands={
 'engine_ia32':['dotnet',str(cli),'run','--rm','--log-driver','none','--network','host','--rootfs',str(root),'/coremark_i686_linux']+args,
 'native_arm64':[str(out/'coremark_arm64_macos')]+args,
}
def host_value(key):
 p=subprocess.run(['sysctl','-n',key],text=True,capture_output=True)
 return p.stdout.strip() if p.returncode==0 else None
metadata=json.loads((out/'build-metadata.json').read_text())
metadata.update({'started_at':datetime.datetime.now().astimezone().isoformat(),'host_platform':platform.platform(),'cpu':host_value('machdep.cpu.brand_string'),'model':host_value('hw.model'),'logical_cpus':host_value('hw.logicalcpu'),'memory_bytes':host_value('hw.memsize'),'commands':commands,'method':'Three alternating paired rounds, one process at a time; identical runtime args, builtin automatic iteration calibration; each timed region >=10s; single context, 2000 bytes, performance seeds; score excludes process startup and calibration.', 'binary_sha256':{name:hashlib.sha256(path.read_bytes()).hexdigest() for name,path in [('ia32',root/'coremark_i686_linux'),('native',out/'coremark_arm64_macos')]}})
samples=[]
result={'metadata':metadata,'samples':samples}
def save(): (out/'comparison.json').write_text(json.dumps(result,indent=2)+'\n')
save()
for round_number in range(1,4):
 order=['engine_ia32','native_arm64'] if round_number%2 else ['native_arm64','engine_ia32']
 for variant in order:
  print(f'Start round {round_number} {variant}',flush=True)
  start=time.perf_counter()
  proc=subprocess.run(commands[variant],cwd=repo,text=True,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=300)
  wall=time.perf_counter()-start
  log=f'round-{round_number}-{variant}.log'
  (out/log).write_text(proc.stdout)
  if proc.returncode or 'Correct operation validated' not in proc.stdout:
   raise RuntimeError(f'{variant} failed ({proc.returncode}): {proc.stdout}')
  def number(label):
   m=re.search(r'^'+re.escape(label)+r'\s*:\s*([0-9.]+)',proc.stdout,re.M)
   if not m: raise RuntimeError(f'Missing {label}: {proc.stdout}')
   return float(m.group(1))
  elapsed=number('Total time (secs)');iterations=int(number('Iterations'));score=number('Iterations/Sec')
  assert elapsed>=10,(variant,elapsed)
  assert abs(iterations/elapsed/score-1)<0.00001
  crcs={name:re.search(r'(?:\[0\])?'+name+r'\s*:\s*(0x[0-9a-fA-F]+)',proc.stdout).group(1) for name in ['seedcrc','crclist','crcmatrix','crcstate']}
  if samples: assert crcs==samples[0]['crcs'],(variant,crcs,samples[0]['crcs'])
  samples.append({'round':round_number,'variant':variant,'iterations_per_second':score,'iterations':iterations,'timed_seconds':elapsed,'wall_seconds':wall,'crcs':crcs,'correct_operation_validated':True,'log':log})
  save()
  print(f'Round {round_number} {variant}: {score:.3f} iterations/s; {iterations} iterations, {elapsed:.3f}s timed, {wall:.3f}s wall',flush=True)
summary={name:{'median_iterations_per_second':statistics.median(s['iterations_per_second'] for s in samples if s['variant']==name),'min_iterations_per_second':min(s['iterations_per_second'] for s in samples if s['variant']==name),'max_iterations_per_second':max(s['iterations_per_second'] for s in samples if s['variant']==name)} for name in commands}
e=summary['engine_ia32']['median_iterations_per_second'];n=summary['native_arm64']['median_iterations_per_second']
summary.update({'engine_percent_of_native':e/n*100,'native_speedup_over_engine':n/e})
result['summary']=summary
save()
print(json.dumps(summary,indent=2),flush=True)
