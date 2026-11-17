import hashlib,json,pathlib,statistics,subprocess,time
import harness
out=pathlib.Path(__file__).resolve().parent
variants=['base','specialized','baked'];cases=list(harness.COMMANDS)+['coremark_gcc','coremark_zig']
counts=json.loads((out/'benchmark-counts.json').read_text())
result={'engine_commit':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),'method':'Three rounds, rotating case order and variant order, one guest at a time. GNU time uses host timestamps at BEGIN/END markers and includes command fork/exec, not CLI startup. CoreMark auto-calibrates >=10s, reported iterations/s. All outputs independently checked. Identical managed CLI, input data and pinned rootfs. Native libraries are isolated, profiles OFF, preserve_none ON, same Apple Clang Release/ThinLTO build.','counts':counts,'samples':[],'library_sha256':{v:hashlib.sha256((out/(v+'-cli/libfibercpu.dylib')).read_bytes()).hexdigest() for v in variants}}
assert len({hashlib.sha256((out/(v+'-cli/Podish.Cli.dll')).read_bytes()).hexdigest() for v in variants})==1
for rnd in range(3):
 order=cases[rnd:]+cases[:rnd]
 for ci,case in enumerate(order):
  vo=variants[(rnd+ci)%3:]+variants[:(rnd+ci)%3]
  for variant in vo:
   print(f'Start round {rnd+1} {case} {variant}',flush=True)
   sample=harness.run(case,variant,counts.get(case,1),f'bench-{rnd+1}-{case}-{variant}');sample['round']=rnd+1
   result['samples'].append(sample);(out/'benchmark.json').write_text(json.dumps(result,indent=2)+'\n');print(sample,flush=True)
summary={}
for case in cases:
 med={v:statistics.median(s['score'] if case.startswith('coremark') else s['seconds'] for s in result['samples'] if s['case']==case and s['variant']==v) for v in variants}
 speed={v:(med[v]/med['base'] if case.startswith('coremark') else med['base']/med[v]) for v in variants}
 summary[case]={'median_scores' if case.startswith('coremark') else 'median_seconds':med,'speedup':speed}
result['summary']=summary
result['geometric_mean_speedup']={v:statistics.geometric_mean(x['speedup'][v] for x in summary.values()) for v in variants}
(out/'benchmark.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps({'summary':summary,'geomean':result['geometric_mean_speedup']},indent=2),flush=True)
