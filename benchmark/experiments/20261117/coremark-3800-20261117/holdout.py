import gzip,hashlib,json,pathlib,random,re,shutil,statistics,sys
out=pathlib.Path(__file__).resolve().parent;repo=out.parents[1]
sys.path.insert(0,str(repo/'artifacts/gnu-coremark-specialization-20261117'));import harness as h
h.OUT=out;h.REPO=repo;h.ROOT=out/'holdout-rootfs';h.WORK=h.ROOT/'work/manual'
if not h.ROOT.exists():shutil.copytree(repo/'artifacts/gnu-coremark-specialization-20261117/rootfs',h.ROOT)
rng=random.Random(987654);lines=[];size=0
while size<2*1024*1024:
 i=len(lines);line=f'row={i:07d} group={i%97:02d} '+('needle ' if i%7==0 else 'otherxx ')+''.join(rng.choices('abcdefghijklmnopqrstuvwxyz0123456789',k=32))+'\n';lines.append(line);size+=len(line)
text=''.join(lines);data=text.encode();(h.WORK/'input.txt').write_bytes(data);(h.WORK/'input.gz').write_bytes(gzip.compress(data,compresslevel=6,mtime=0))
for i in range(16):(h.WORK/'tar-input'/f'{i:02d}.txt').write_bytes(data[i*131072:(i+1)*131072])
expected={'grep_fixed':str(sum('needle' in l for l in lines))+'\n','grep_regex':str(sum(re.search('group=(07|13|42)',l) is not None for l in lines))+'\n','sed':re.sub('row=','record=',text).replace('otherxx','plainxx'),'sort':''.join(sorted(lines)),'sha256':hashlib.sha256(data).hexdigest()}
(out/'expected.json').write_text(json.dumps(expected));(out/'holdout-fixture.json').write_text(json.dumps({'seed':987654,'bytes':len(data),'sha256':expected['sha256']},indent=2))
counts=json.loads((repo/'artifacts/gnu-coremark-specialization-20261117/benchmark-counts.json').read_text());cases=list(h.COMMANDS)+['coremark_zig'];samples=[]
for rnd in range(2):
 for i,c in enumerate(cases[rnd:]+cases[:rnd]):
  variants=['base','observed'] if (rnd+i)%2==0 else ['observed','base']
  for v in variants:
   print('START',rnd+1,c,v,flush=True);r=h.run(c,v,counts.get(c,1),f'holdout-{rnd+1}-{c}-{v}');r['round']=rnd+1;samples.append(r);print(r,flush=True)
   (out/'holdout.json').write_text(json.dumps({'fixture_seed':987654,'samples':samples},indent=2)+'\n')
summary={}
for c in cases:
 med={v:statistics.median(s.get('score',s['seconds']) for s in samples if s['case']==c and s['variant']==v) for v in ['base','observed']}
 summary[c]={'medians':med,'speedup':med['observed']/med['base'] if c.startswith('coremark') else med['base']/med['observed']}
(out/'holdout.json').write_text(json.dumps({'fixture_seed':987654,'method':'Two rotating paired rounds with independent text seed, one process at a time; includes separate Zig CoreMark compiler holdout. All outputs/CRCs checked.','samples':samples,'summary':summary},indent=2)+'\n');print(summary,flush=True)
