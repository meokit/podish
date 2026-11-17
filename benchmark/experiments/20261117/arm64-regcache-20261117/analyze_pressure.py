import pathlib,re,json,collections,sys,os
out=pathlib.Path(__file__).resolve().parent;patterns=[json.loads(l) for l in (out/'pattern-counts.jsonl').read_text().splitlines()];chosen=json.loads((out/('selected-patterns-'+os.environ.get('REGCACHE_LIMIT','64')+'.json')).read_text());which={(x['symbol'],tuple(x['shapes'])):i for i,x in enumerate(chosen)}
def canon(s):return s.rsplit('(fiberish::EmuState*',1)[0]
reports={}
for variant in sys.argv[1:]:
 functions={};name=None
 for l in (out/(variant+'.asm')).read_text().splitlines():
  m=re.match(r'^[0-9a-f]+ <(.*)>:$',l)
  if m:name=canon(m[1]);functions[name]=[]
  elif name and re.match(r'^\s*[0-9a-f]+:',l):functions[name].append(l)
 rows=[]
 for x in patterns:
  i=which.get((x['symbol'],tuple(x['shapes'])))
  if i is not None:name=next(n for n in functions if f'RegCacheClone_{i:03d}_' in n)
  else:name=canon(x['symbol'])
  if name not in functions:continue
  ins=functions[name];sp=[l for l in ins if re.search(r'\[(?:sp|x29)\b',l)]
  rows.append({'symbol':x['symbol'],'entry':name,'count':x['count'],'cloned':i is not None,'stack_refs':len(sp),'instructions':len(ins)})
 total=sum(x['count'] for x in rows);stack=sum(x['count'] for x in rows if x['stack_refs']);print(variant,'profile covered',total,'has any stack path entry weight',stack/total)
 reports[variant]=rows
(out/('weighted-pressure-'+os.environ.get('REGCACHE_LIMIT','64')+'.json')).write_text(json.dumps(reports,indent=2)+'\n')
baseline=sys.argv[1]
if baseline in reports:
 base={x['symbol']+str(x['cloned']):x['stack_refs'] for x in reports[baseline]}
 for v,rows in reports.items():
  if v==baseline:continue
  changed=collections.Counter()
  for x in rows:
   k=x['symbol']+str(x['cloned'])
   if x['stack_refs']>base.get(k,x['stack_refs']):changed[x['entry']]+=x['count']
  print(v,'stack refs increased',changed.most_common(8))
