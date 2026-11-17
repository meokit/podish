from pathlib import Path
import ctypes,json,re,sys,collections
out=Path(__file__).resolve().parent;repo=out.parents[1]
class Info(ctypes.Structure):
 _fields_=[('schema_version',ctypes.c_uint32),('opcode',ctypes.c_uint32),('variant_key',ctypes.c_uint64),('handler',ctypes.c_void_p),('logic_name',ctypes.c_char_p),('family_name',ctypes.c_char_p),('fusion_parent',ctypes.c_char_p),('priority',ctypes.c_int32),('bake_with_parent',ctypes.c_uint32),('is_fallback',ctypes.c_uint32),('constraints',ctypes.c_uint32*19)]
lib=ctypes.CDLL(str((repo/'Fiberish.X86/build_native/host/libfibercpu.dylib').resolve()));lib.X86_GetInstructionVariant.argtypes=[ctypes.c_int32,ctypes.POINTER(Info)]
rows=[]
for i in range(lib.X86_GetInstructionVariantCount()):
 v=Info();assert lib.X86_GetInstructionVariant(i,ctypes.byref(v))==1
 rows.append({'opcode':v.opcode,'logic':v.logic_name.decode(),'family':v.family_name.decode(),'parent':v.fusion_parent.decode(),'bake':bool(v.bake_with_parent),'fallback':bool(v.is_fallback),'key':f'{v.variant_key:016x}','priority':v.priority,'constraints':list(v.constraints)})
assert len({r['key'] for r in rows})==len(rows)
# Normalize the 8 template targets to their new stable constexpr aliases.
def oldname(s):
 t=re.search(r'fiberish::op::(.+?)\(fiberish::EmuState',s)[1]
 if t.startswith('OpRotateRegImmediate<'):
  bits=16 if 'unsigned short' in t else 32;right='true, ' in t;flags=t.endswith('true>');t=f'Op{"Ror" if right else "Rol"}_RegImm{bits}_{"Live" if flags else "Dead"}'
 return 'op::'+t
baseline=[]
for line in (out/'baseline-registry.tsv').read_text().splitlines():
 a=line.split('\t');v=(*map(int,a[:-1]),oldname(a[-1]))
 if v not in baseline:baseline.append(v)
new=[(r['opcode'],*r['constraints'][:17],r['logic']) for r in rows if not r['fallback']]
assert baseline==new,[(i,a,b) for i,(a,b) in enumerate(zip(baseline,new)) if a!=b][:5]
assert all(r['constraints'][17:]==[0,0] for r in rows)
fb={int(k):oldname(v) for k,v in json.loads((out/'baseline-fallbacks.json').read_text()).items()}
assert {r['opcode']:r['logic'] for r in rows if r['fallback']}==fb
(out/'catalog.json').write_text(json.dumps({'schema_version':1,'variants':rows},indent=2)+'\n')
print('Exact baseline match:',len(new),'unique variants (32 duplicate declarations removed),',len(fb),'fallbacks; stable keys unique')
print('Bake expansions:',[(r['parent'],r['logic']) for r in rows if r['bake']])
