import hashlib,json,pathlib,re,statistics,subprocess,sys,time,os
out=pathlib.Path(__file__).resolve().parent;repo=out.parents[1];root=repo/'artifacts/gnu-coremark-specialization-20261117/rootfs'
tag=sys.argv[1];rounds=int(sys.argv[2]);variants=sys.argv[3:];result={'tag':tag,'commit':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),'method':'Rotating variant order, same host/guest/rootfs/managed CLI, iteration count is guest_iterations_argument (0=auto); every timed sample >=10s, one process at a time, no concurrent build/profile; CRC checked. CoreMark GCC15.2 -O2 i386, 2K, one context.','guest_iterations_argument':os.environ.get('COREMARK_ITERATIONS','0'),'samples':[],'library_sha256':{v:hashlib.sha256((out/(v+'-cli/libfibercpu.dylib')).read_bytes()).hexdigest() for v in variants}}
assert len({hashlib.sha256((out/(v+'-cli/Podish.Cli.dll')).read_bytes()).hexdigest() for v in variants})==1
for rnd in range(rounds):
 order=variants[rnd%len(variants):]+variants[:rnd%len(variants)]
 for v in order:
  print('START',rnd+1,v,flush=True);t=time.perf_counter();p=subprocess.run(['dotnet',str(out/(v+'-cli/Podish.Cli.dll')),'run','--rm','--log-driver','none','--rootfs',str(root),'/work/coremark_gcc','0x0','0x0','0x66',os.environ.get('COREMARK_ITERATIONS','0'),'7','1','2000'],cwd=repo,text=True,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=240);wall=time.perf_counter()-t
  log=f'{tag}-{rnd+1}-{v}.log';(out/log).write_text(p.stdout)
  assert p.returncode==0 and 'Correct operation validated' in p.stdout,p.stdout
  def num(s):return float(re.search(r'^'+re.escape(s)+r'\s*:\s*([0-9.]+)',p.stdout,re.M).group(1))
  sec=num('Total time (secs)');assert sec>=10
  r={'round':rnd+1,'variant':v,'score':num('Iterations/Sec'),'timed_seconds':sec,'iterations':int(num('Iterations')),'wall_seconds':wall,'validated':True,'log':log};result['samples'].append(r);print(r,flush=True)
  (out/(tag+'.json')).write_text(json.dumps(result,indent=2)+'\n')
result['median_scores']={v:statistics.median(s['score'] for s in result['samples'] if s['variant']==v) for v in variants}
(out/(tag+'.json')).write_text(json.dumps(result,indent=2)+'\n');print(result['median_scores'],flush=True)
