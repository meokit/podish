import collections,gzip,hashlib,json,math,os,pathlib,random,re,shutil,statistics,subprocess,sys,time
OUT=pathlib.Path(__file__).resolve().parent;REPO=OUT.parents[1];ROOT=OUT/'rootfs';WORK=ROOT/'work/manual';WORK.mkdir(exist_ok=True,parents=True)
def prepare():
 rng=random.Random(12345);lines=[];size=0
 while size<2*1024*1024:
  i=len(lines);line=f'row={i:07d} group={i%97:02d} '+('needle ' if i%7==0 else 'otherxx ')+''.join(rng.choices('abcdefghijklmnopqrstuvwxyz0123456789',k=32))+'\n';lines.append(line);size+=len(line)
 text=''.join(lines);(WORK/'input.txt').write_text(text);(WORK/'input.gz').write_bytes(gzip.compress(text.encode(),compresslevel=6,mtime=0))
 (WORK/'tar-input').mkdir(exist_ok=True)
 for i in range(16):(WORK/'tar-input'/f'{i:02d}.txt').write_bytes(text.encode()[i*131072:(i+1)*131072])
 (WORK/'extract').mkdir(exist_ok=True)
 expected={'grep_fixed':str(sum('needle' in l for l in lines))+'\n','grep_regex':str(sum(re.search('group=(07|13|42)',l) is not None for l in lines))+'\n','sed':re.sub('row=','record=',text).replace('otherxx','plainxx'),'sort':''.join(sorted(lines)),'sha256':hashlib.sha256(text.encode()).hexdigest()}
 (OUT/'expected.json').write_text(json.dumps(expected));(OUT/'fixture-meta.json').write_text(json.dumps({'seed':12345,'bytes':len(text.encode()),'lines':len(lines),'sha256':expected['sha256']},indent=2))
 return expected
COMMANDS={'gzip':'/bin/gzip -n -6 -c input.txt > result.gz','gunzip':'/bin/gzip -dc input.gz > result.txt','sed':"/bin/sed -e 's/row=/record=/g' -e 's/otherxx/plainxx/g' input.txt > result.txt",'grep_fixed':"/bin/grep -F -c needle input.txt > result.txt",'grep_regex':"/bin/grep -E -c 'group=(07|13|42)' input.txt > result.txt",'sort':'/usr/bin/sort input.txt > result.txt','sha256':'/usr/bin/sha256sum input.txt > result.txt','tar':'/bin/tar --format=ustar -cf result.tar tar-input'}
def launch(variant,script):
 return ['dotnet',str(OUT/(variant+'-cli')/'Podish.Cli.dll'),'run','--rm','--log-driver','none','--rootfs',str(ROOT),'--env','LC_ALL=C','--env','LANG=C','--env','TZ=UTC','/bin/bash','--noprofile','--norc','-c',script]
def script_for(case,n):return "set -eu\ncd /work/manual\nprintf '__BEGIN__\\n'\nfor ((i=0;i<"+str(n)+";i++)); do\n"+COMMANDS[case]+"\ndone\nprintf '__END__\\n'\n"
def run(case,variant,n,tag):
 if case.startswith('coremark'):
  root=ROOT
  exe='/work/'+case
  cmd=['dotnet',str(OUT/(variant+'-cli')/'Podish.Cli.dll'),'run','--rm','--log-driver','none','--rootfs',str(root),exe,'0x0','0x0','0x66','0','7','1','2000']
 else:cmd=launch(variant,script_for(case,n))
 p=subprocess.Popen(cmd,cwd=REPO,text=True,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,bufsize=1);start=None;end=None;lines=[];wall=time.perf_counter()
 for line in p.stdout:
  lines.append(line)
  if line.strip()=='__BEGIN__':start=time.perf_counter()
  if line.strip()=='__END__':end=time.perf_counter()
 code=p.wait(timeout=300);wall=time.perf_counter()-wall;output=''.join(lines);(OUT/(tag+'.log')).write_text(output)
 if code:raise RuntimeError(case+' '+output)
 if case.startswith('coremark'):
  assert 'Correct operation validated' in output,output
  score=float(re.search(r'Iterations/Sec\s*:\s*([0-9.]+)',output).group(1));seconds=float(re.search(r'Total time \(secs\):\s*([0-9.]+)',output).group(1));assert seconds>=10
  return {'case':case,'variant':variant,'seconds':seconds,'score':score,'wall_seconds':wall,'iterations':int(re.search(r'Iterations\s*:\s*(\d+)',output).group(1)),'validated':True,'log':tag+'.log'}
 assert start is not None and end is not None,output
 expected=json.loads((OUT/'expected.json').read_text());input=(WORK/'input.txt').read_bytes()
 if case=='gzip':assert gzip.decompress((WORK/'result.gz').read_bytes())==input
 elif case=='gunzip':assert (WORK/'result.txt').read_bytes()==input
 elif case in ('sed','grep_fixed','grep_regex','sort'):assert (WORK/'result.txt').read_text()==expected[case],case
 elif case=='sha256':assert (WORK/'result.txt').read_text().split()[0]==expected['sha256']
 elif case=='tar':
  import tarfile
  with tarfile.open(WORK/'result.tar') as f:
   members=[x for x in f.getmembers() if x.isfile()];assert len(members)==16
   for x in members:assert f.extractfile(x).read()==(WORK/x.name).read_bytes()
 return {'case':case,'variant':variant,'seconds':end-start,'wall_seconds':wall,'iterations':n,'validated':True,'log':tag+'.log'}
if __name__=='__main__':
 mode=sys.argv[1]
 if mode=='prepare':prepare()
 elif mode=='pilot':
  samples=[]
  for case in COMMANDS:
   r=run(case,'base',1,'pilot-'+case);samples.append(r);print(r,flush=True)
  counts={s['case']:max(1,min(100,math.ceil(4/max(s['seconds'],.001)))) for s in samples}
  (OUT/'pilot.json').write_text(json.dumps({'samples':samples,'iterations':counts},indent=2));print(counts,flush=True)
