import collections,json,pathlib,sys,xml.etree.ElementTree as E
out=pathlib.Path(__file__).resolve().parent;tag=sys.argv[1] if len(sys.argv)>1 else 'hardware';start=float(sys.argv[2]) if len(sys.argv)>2 else 4;end=float(sys.argv[3]) if len(sys.argv)>3 else 14;output_tag=tag+('-whole' if start==0 else '');r=E.parse(out/(tag+'-counters.xml')).getroot();idx={e.get('id'):e for e in r.iter() if e.get('id')}
def res(e):return idx[e.get('ref')] if e is not None and e.get('ref') else e
names=['BRANCH_MISPRED_NONSPEC','Cycles','Instructions','INST_BRANCH','INST_LDST','L1D_CACHE_MISS_LD_NONSPEC','L1I_CACHE_MISS_DEMAND','ARM_STALL_FRONTEND','ARM_STALL_BACKEND']
threads=collections.Counter();rows=[]
for row in r.iter('row'):
 t=res(row.find('sample-time'));ev=res(row.find('pmc-events'));pr=res(row.find('process'));th=res(row.find('thread'))
 if t is None or not start*1e9<=float(t.text)<=end*1e9 or ev is None or pr is None or 'dotnet' not in pr.get('fmt',''):continue
 a=list(map(int,ev.text.split()));assert len(a)==len(names)
 tid=th.get('fmt');threads[tid]+=a[1];rows.append((row,tid,a,float(t.text)/1e9))
worker=threads.most_common(1)[0][0];total=collections.Counter();core=collections.Counter();n=0;times=[];native_samples=0;block=[]
for row,tid,a,t in rows:
 if tid!=worker:continue
 c=res(row.find('core'))
 if c is None or '(P Core)' not in c.get('fmt',''):continue
 n+=1;times.append(t)
 for name,v in zip(names,a):total[name]+=v
 c=res(row.find('core'));core[c.get('fmt') if c is not None else 'unknown']+=a[1]
 bt=res(row.find('backtrace'));frames=[res(f) for f in bt] if bt is not None else [];f=next((f for f in frames if f.get('name') and f.get('name')!='<deduplicated_symbol>'),None)
 if f is not None:
  b=res(f.find('binary'))
  if b is not None and b.get('name')=='libfibercpu.dylib':native_samples+=1
 block.append({'time':t,**dict(zip(names,a))})
cyc=total['Cycles'];ins=total['Instructions'];branch=total['INST_BRANCH'];metrics={'IPC':ins/cyc,'CPI':cyc/ins,'branch_misprediction_percent':100*total['BRANCH_MISPRED_NONSPEC']/branch,'branch_prediction_accuracy_percent':100*(1-total['BRANCH_MISPRED_NONSPEC']/branch),'branch_MP KI'.replace(' ',''):1000*total['BRANCH_MISPRED_NONSPEC']/ins,'branch_instruction_percent':100*branch/ins,'load_store_instruction_percent':100*total['INST_LDST']/ins,'L1D_load_miss_MPKI':1000*total['L1D_CACHE_MISS_LD_NONSPEC']/ins,'L1I_demand_miss_MPKI':1000*total['L1I_CACHE_MISS_DEMAND']/ins,'frontend_no_issue_cycles_percent':100*total['ARM_STALL_FRONTEND']/cyc,'backend_no_issue_cycles_percent':100*total['ARM_STALL_BACKEND']/cyc}
result={'worker':worker,'core_filter':'P Core only','analysis_window_seconds':[start,end],'rows':n,'time_range_seconds':[min(times),max(times)],'samples_with_native_top_frame_percent':100*native_samples/n,'core_cycles':dict(core),'counter_sums':dict(total),'metrics':metrics,'notes':[f'Counts are per-interval deltas; sum selected worker samples in [{start:g},{end:g}]s. The default 4..14s window is steady; the whole-recording window also includes startup/cleanup.','PMU events are sampled at ~1ms. Backtrace samples do not identify the cause of every counted cycle.','L1D metric is retired load misses per 1000 all instructions, not cache miss rate per load.','Frontend/backend no-issue ratios are separate events, not an additive top-down decomposition.']}
(out/(output_tag+'-summary.json')).write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result,indent=2))
