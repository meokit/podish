import collections,json,pathlib,re,sys,xml.etree.ElementTree as ET
out=pathlib.Path(__file__).resolve().parent
profile=sys.argv[1] if len(sys.argv)>1 else 'baseline'
root=ET.parse(out/profile/(profile+'.xml')).getroot();idx={e.get('id'):e for e in root.iter() if e.get('id')}
def res(e):
 if e is None:return None
 return idx[e.get('ref')] if e.get('ref') else e
asm={};function=None
for line in (out/'baseline-full.asm').read_text().splitlines():
 m=re.match(r'^([0-9a-f]+) <(.*)>:$',line)
 if m:function=m.group(2);continue
 m=re.match(r'^\s*([0-9a-f]+):\s+[0-9a-f]{8}\s+([a-zA-Z0-9_.]+)\s*(.*)$',line)
 if m:asm[int(m[1],16)]={'function':function,'mnemonic':m[2],'operands':m[3]}
pcs=collections.Counter();symbols=collections.Counter();binaries=collections.Counter();cores=collections.Counter();rows=0
for row in root.iter('row'):
 t=res(row.find('sample-time'))
 if t is None or int(t.text)<4e9:continue
 bt=res(row.find('backtrace'))
 if bt is None:continue
 frames=[res(f) for f in bt.findall('frame')]
 frame=next((f for f in frames if f.get('name') and f.get('name')!='<deduplicated_symbol>'),frames[0] if frames else None)
 if frame is None:continue
 weight=int(res(row.find('weight')).text)/1e6
 b=res(frame.find('binary'));name=b.get('name','unknown') if b is not None else 'unknown'
 binaries[name]+=weight;rows+=1
 if name!='libfibercpu.dylib':continue
 pc=(int(frame.get('addr'),16)-int(b.get('load-addr'),16)) & ~3  # AArch64 instruction containing the exported PC
 pcs[pc]+=weight;symbols[frame.get('name')]+=weight
 core=res(row.find('core'));cores[core.get('fmt',core.text)]+=weight
categories=collections.Counter();instructions=collections.Counter();missed=0
for pc,n in pcs.items():
 ins=asm.get(pc)
 if not ins:missed+=n;continue
 op=ins['mnemonic'];operands=ins['operands']
 if op in ('br','blr'):category='indirect branch'
 elif op.startswith('b.') or op in ('cbz','cbnz','tbz','tbnz'):category='conditional branch'
 elif op in ('b','bl','ret'):category='direct branch/call/return'
 elif '[sp' in operands or op in ('stp','ldp') and any(x in operands for x in ['x29','x30']):category='stack/frame memory'
 elif op.startswith(('ldr','ldp','ldur','ldar','str','stp','stur','stlr')):category='other load/store'
 else:category='arithmetic/bit manipulation/other'
 categories[category]+=n;instructions[op]+=n
result={'warmup_seconds':4,'total_samples':rows,'binaries_ms':dict(binaries.most_common()),'native_ms':sum(pcs.values()),'native_pc_unmapped_ms':missed,'native_categories_ms':dict(categories.most_common()),'native_instructions_ms':dict(instructions.most_common()),'core_ms':dict(cores),'native_superopcode_ms':sum(n for s,n in symbols.items() if 'SuperOpcode_' in s),'native_wrapper_ms':sum(n for s,n in symbols.items() if 'DispatchWrapper' in s),'dispatch_next_load_ms':sum(n for pc,n in pcs.items() if asm.get(pc,{}).get('mnemonic')=='ldr' and '[x1, #0x20]!' in asm.get(pc,{}).get('operands','')), 'top_pcs':[dict(pc=hex(pc),samples_ms=n,**asm.get(pc,{})) for pc,n in pcs.most_common(100)],'top_symbols':[{'symbol':s,'samples_ms':n} for s,n in symbols.most_common(80)]}
(out/(profile+'-pc-summary.json')).write_text(json.dumps(result,indent=2)+'\n')
print(json.dumps({k:v for k,v in result.items() if k not in ('top_pcs','top_symbols')},indent=2))
for x in result['top_pcs'][:25]:print(x['samples_ms'],x['pc'],x.get('mnemonic'),x.get('operands'),x.get('function','')[:95])
