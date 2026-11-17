import collections,datetime,json,pathlib,re,xml.etree.ElementTree as E
out=pathlib.Path(__file__).resolve().parent;toc=E.parse(out/'toc.xml');start=datetime.datetime.fromisoformat(toc.findtext('.//summary/start-date')).timestamp()
markers={}
for name,edge,stamp in re.findall(r'__PHASE__ (\w+) (BEGIN|END) ([0-9.]+)',(out/'profile-stdout.log').read_text()):markers.setdefault(name,{})[edge]=float(stamp)-start
assert len(markers)==10 and all('END' in x for x in markers.values()),markers
root=E.parse(out/'samples.xml').getroot();idx={e.get('id'):e for e in root.iter() if e.get('id')}
def res(e):return idx[e.get('ref')] if e is not None and e.get('ref') else e
asm={};function=''
for line in (out/'baseline.asm').read_text().splitlines():
 m=re.match(r'^([0-9a-f]+) <(.*)>:$',line)
 if m:function=m[2]
 m=re.match(r'^\s*([0-9a-f]+):\s+[0-9a-f]{8}\s+(\S+)\s*(.*)$',line)
 if m:asm[int(m[1],16)]={'function':function,'mnemonic':m[2],'operands':m[3]}
stats={c:{'symbols':collections.Counter(),'pcs':collections.Counter(),'binaries':collections.Counter()} for c in markers}
for row in root.iter('row'):
 time=float(res(row.find('sample-time')).text)/1e9
 case=next((c for c,p in markers.items() if p['BEGIN']+.15<=time<=p['END']),None)
 if case is None:continue
 bt=res(row.find('backtrace'));frames=[res(x) for x in bt] if bt is not None else []
 f=next((f for f in frames if f.get('name') and f.get('name')!='<deduplicated_symbol>'),None)
 if f is None:continue
 b=res(f.find('binary'));bn=b.get('name','unknown') if b is not None else 'unknown';weight=float(res(row.find('weight')).text)/1e6;stats[case]['binaries'][bn]+=weight
 if bn=='libfibercpu.dylib':
  pc=(int(f.get('addr'),16)-int(b.get('load-addr'),16))&~3;stats[case]['pcs'][pc]+=weight;stats[case]['symbols'][f.get('name')]+=weight
result={'trace_start_epoch':start,'phases':markers,'cases':{}};equal=collections.Counter()
for case,s in stats.items():
 n=sum(s['symbols'].values());assert n>10,(case,n)
 for name,v in s['symbols'].items():equal[name]+=v/n/len(stats)
 result['cases'][case]={'native_ms':n,'binaries_ms':dict(s['binaries'].most_common()),'top_symbols':[{'name':name,'ms':v,'percent':v/n*100} for name,v in s['symbols'].most_common(30)],'top_pcs':[{'pc':hex(pc),'ms':v,'percent':v/n*100,**asm.get(pc,{})} for pc,v in s['pcs'].most_common(30)]}
 print('\n'+case+' native '+str(n)+'ms')
 for x in result['cases'][case]['top_symbols'][:10]:print(f"{x['percent']:5.2f}% "+x['name'].split('::op::')[-1].split('(')[0])
result['equal_case_weight_hotspots']=[{'name':name,'percent':v*100} for name,v in equal.most_common(60)]
(out/'profile-by-workload.json').write_text(json.dumps(result,indent=2)+'\n')
print('\nEQUAL WEIGHT')
for x in result['equal_case_weight_hotspots'][:20]:print(f"{x['percent']:5.2f}% "+x['name'].split('::op::')[-1].split('(')[0])
