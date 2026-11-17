from pathlib import Path
import sys,shutil,json,statistics
out=Path(__file__).resolve().parent;repo=out.parents[1];sys.path.insert(0,str(repo/'artifacts/gnu-coremark-specialization-20261117'));import harness as h
h.OUT=out;h.REPO=repo;h.ROOT=repo/'artifacts/gnu-coremark-specialization-20261117/rootfs';h.WORK=h.ROOT/'work/manual';shutil.copyfile(repo/'artifacts/gnu-coremark-specialization-20261117/expected.json',out/'expected.json');counts={'gzip':7,'sha256':51,'coremark_zig':1};cases=list(counts);samples=[]
for rnd in range(2):
 for i,c in enumerate(cases[rnd:]+cases[:rnd]):
  order=['before','after'] if (rnd+i)%2==0 else ['after','before']
  for v in order:
   print('START',rnd+1,c,v,flush=True);r=h.run(c,v,counts[c],f'holdout-{rnd+1}-{c}-{v}');r['round']=rnd+1;samples.append(r);print(r,flush=True);(out/'holdout.json').write_text(json.dumps({'samples':samples},indent=2)+'\n')
summary={}
for c in cases:
 med={v:statistics.median(s.get('score',s['seconds']) for s in samples if s['case']==c and s['variant']==v) for v in ['before','after']};summary[c]={'medians':med,'speedup':med['after']/med['before'] if c.startswith('coremark') else med['before']/med['after']}
(out/'holdout.json').write_text(json.dumps({'method':'Two rotating paired rounds; GNU gzip/sha256 fixed deterministic input independent of CoreMark; separately compiled Zig CoreMark. Compares the original and unified declaration registries with the same 71 fusion handlers. All outputs validated.','samples':samples,'summary':summary},indent=2)+'\n');print(summary,flush=True)
