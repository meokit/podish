import collections,json,pathlib,xml.etree.ElementTree as E
out=pathlib.Path(__file__).resolve().parent;root=E.parse(out/'candidate-samples.xml').getroot();idx={e.get('id'):e for e in root.iter() if e.get('id')}
def res(e):return idx[e.get('ref')] if e is not None and e.get('ref') else e
symbols=collections.Counter();binaries=collections.Counter();pcs=collections.Counter()
for row in root.iter('row'):
 time=float(res(row.find('sample-time')).text)/1e9
 if time<4:continue
 bt=res(row.find('backtrace'));frames=[res(f) for f in bt] if bt is not None else []
 f=next((f for f in frames if f.get('name') and f.get('name')!='<deduplicated_symbol>'),None)
 if f is None:continue
 b=res(f.find('binary'));name=b.get('name','unknown') if b is not None else 'unknown';ms=float(res(row.find('weight')).text)/1e6;binaries[name]+=ms
 if name=='libfibercpu.dylib':
  symbols[f.get('name')]+=ms;pcs[(int(f.get('addr'),16)-int(b.get('load-addr'),16))&~3]+=ms
native=sum(symbols.values());assert native>1000
p={'exclude_initial_seconds':4,'native_ms':native,'binaries_ms':dict(binaries.most_common()),'symbols':[{'name':s,'ms':v,'percent':v/native*100} for s,v in symbols.most_common()],'pcs':[{'pc':hex(pc),'ms':v,'percent':v/native*100} for pc,v in pcs.most_common()]}
(out/'candidate-hotspots.json').write_text(json.dumps(p,indent=2)+'\n')
for s in p['symbols'][:20]:print(f"{s['percent']:6.2f}% {s['name']}")
