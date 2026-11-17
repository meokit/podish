import hashlib,json,pathlib,re,statistics,subprocess,time
out=pathlib.Path(__file__).resolve().parent;repo=out.parents[1]
root=repo/'artifacts/coremark-asm-profile-20261117/gcc-rootfs'
cli=repo/'Podish.Cli/bin/Release/net10.0/Podish.Cli.dll'
lib=cli.parent/'libfibercpu.dylib'
command=['dotnet',str(cli),'run','--rm','--log-driver','none','--rootfs',str(root),'/coremark_gcc_i386','0x0','0x0','0x66','0','7','1','2000']
r={'engine_commit':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),'native_library_sha256':hashlib.sha256(lib.read_bytes()).hexdigest(),'guest_sha256':hashlib.sha256((root/'coremark_gcc_i386').read_bytes()).hexdigest(),'cpu':subprocess.check_output(['sysctl','-n','machdep.cpu.brand_string'],text=True).strip(),'configuration':'Release, ARM64 preserve_none enabled, default GNU 32-pair + 32-long fusion table, single context, GCC 15.2.0 -O2 CoreMark, auto-calibrated >=10s per run','command':command,'samples':[]}
for round_number in range(1,4):
 print(f'Start round {round_number}',flush=True);t=time.perf_counter();p=subprocess.run(command,text=True,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,cwd=repo,timeout=180);wall=time.perf_counter()-t
 log=f'gcc-preserve-none-round-{round_number}.log';(out/log).write_text(p.stdout)
 if p.returncode or 'Correct operation validated' not in p.stdout:raise RuntimeError(p.stdout)
 def num(label):return float(re.search('^'+re.escape(label)+r'\s*:\s*([0-9.]+)',p.stdout,re.M).group(1))
 secs=num('Total time (secs)');assert secs>=10
 sample={'round':round_number,'score':num('Iterations/Sec'),'timed_seconds':secs,'iterations':int(num('Iterations')),'wall_seconds':wall,'validated':True,'log':log};r['samples'].append(sample)
 (out/'gcc-preserve-none-results.json').write_text(json.dumps(r,indent=2)+'\n');print(sample,flush=True)
r['median_score']=statistics.median(s['score'] for s in r['samples']);r['min_score']=min(s['score'] for s in r['samples']);r['max_score']=max(s['score'] for s in r['samples'])
(out/'gcc-preserve-none-results.json').write_text(json.dumps(r,indent=2)+'\n');print('Median:',r['median_score'],flush=True)
