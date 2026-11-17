"""Reproduce the two observed candidates from the saved exact CoreMark profile."""
from pathlib import Path
import json,re
out=Path(__file__).resolve().parent;repo=out.parents[1]
profile=json.loads((out/'gcc-training/sequence_profile.json').read_text())
assert profile['buildFeatures']==2 and profile['schemaVersion']==2
base=json.loads((repo/'benchmark/superopcode/manifests/gnu-long4x32-v1.json').read_text())['candidates']
seen={tuple(x['pair']) for x in base};patterns=list(base)
ordinary=re.compile(r'^op::Op(Mov|Lea|Cmp|Test|Add|Sub|And|Or|Xor|Inc|Dec|Neg|Push|Pop|Group[1-5]_|Cmov|Setcc|Shl|Shr|Sar|Rol|Ror|Imul|Mul|Cdq|Cwde)')
excluded=re.compile(r'Call|Jmp|Ret|String|Movs_|Movs[bdw]|Cmps|Scas|Stos|Lods')
def allowed(ops):
 return all((ordinary.match(op) or i==len(ops)-1 and op.startswith('op::OpJcc_')) and not excluded.search(op) for i,op in enumerate(ops))
for candidates,budget in [([{'ops':[x['first'],x['second']],'count':x['count']} for x in profile['pairs']],64),([x for x in profile['ngrams'] if len(x['ops'])==4],32)]:
 selected=0
 for x in sorted(candidates,key=lambda x:(-x['count'],tuple(x['ops']))):
  ops=tuple(x['ops'])
  if allowed(ops) and ops not in seen:
   patterns.append({'pair':list(ops),'training_count':x['count']});seen.add(ops);selected+=1
   if selected==budget:break
assert len(patterns)==160
for variant,n in [('pairs',128),('observed',160)]:
 saved=json.loads((out/(variant+'.json')).read_text())
 assert saved['candidates']==patterns[:n],variant
 print(variant,': exact saved manifest reproduced')
