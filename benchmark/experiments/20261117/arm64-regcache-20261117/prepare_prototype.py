from pathlib import Path
import shutil,re,json,collections,os
out=Path(__file__).resolve().parent;repo=out.parents[1];root=out/'source';shutil.copytree(repo/'libfibercpu',root/'libfibercpu',dirs_exist_ok=True,ignore=shutil.ignore_patterns('build*','__pycache__'));(root/'cmake').mkdir(exist_ok=True);shutil.copyfile(repo/'cmake/CPM.cmake',root/'cmake/CPM.cmake');(root/'scripts').mkdir(exist_ok=True);shutil.copyfile(repo/'scripts/gen_handler_reflection.py',root/'scripts/gen_handler_reflection.py')
src=root/'libfibercpu/src'
header=['#pragma once','#ifndef REGCACHE_COUNT','#define REGCACHE_COUNT 0','#endif','#ifndef REGCACHE_ACTIVE','#define REGCACHE_ACTIVE 0','#endif','#ifndef REGCACHE_CLONES','#define REGCACHE_CLONES 0','#endif']
for n in range(5):
 regs=[0,2,1,3][:n]
 header += [f'#if REGCACHE_COUNT == {n}', '#define RC_DECL '+''.join(', uint32_t rc'+str(i) for i in range(n)), '#define RC_NAMES '+''.join(', rc'+str(i) for i in range(n)), '#define RC_READ '+''.join(', state->ctx.regs['+str(r)+']' for r in regs), '#define RC_ASSUME_ALL '+''.join('__builtin_assume(state->ctx.regs['+str(r)+'] == rc'+str(i)+'); ' for i,r in enumerate(regs)), '#endif']
header+=['#if REGCACHE_ACTIVE','#define RC_PASS RC_READ','#define RC_ASSUME RC_ASSUME_ALL','#else','#define RC_PASS RC_NAMES','#define RC_ASSUME','#endif','#define RC_INIT RC_READ']
(src/'regcache_abi.h').write_text(('#ifndef REGCACHE_SECOND\n#define REGCACHE_SECOND 2\n#endif\n'+'\n'.join(header)+'\n').replace('state->ctx.regs[2]','state->ctx.regs[REGCACHE_SECOND]'))
p=src/'decoder.h';s=p.read_text().replace('#include "common.h"','#include "common.h"\n#include "regcache_abi.h"');s=s.replace('uint32_t branch, uint64_t flags_cache);','uint32_t branch, uint64_t flags_cache RC_DECL);');p.write_text(s)
files=['dispatch.h','ops.cpp','superopcodes.h','superopcodes.cpp']
def extend(s):
 s=s.replace('uint64_t flags_cache)', 'uint64_t flags_cache RC_DECL)')
 s=re.sub(r'(RC_DECL\)\s*\{[ \t]*)(\\?)(\n)',lambda m:m[1]+m[2]+m[3]+'    RC_ASSUME; '+('\\\n' if m[2] else '\n'),s)
 s,n=re.subn(r'(ATTR_MUSTTAIL return [^;]*?\bflags_cache)(\s*\))',r'\1 RC_PASS\2',s)
 return s
for f in files:
 p=src/f;p.write_text(extend(p.read_text()))
p=src/'bindings.cpp';s=p.read_text();s=re.sub(r'((?:head->handler|block_ptr->entry)\([^;]*?flags_cache)(\s*\))',r'\1 RC_INIT\2',s);p.write_text(s)
p=root/'scripts/gen_handler_reflection.py';s=p.read_text().replace("'uint64_t flags_cache'","'uint64_t flags_cache RC_DECL'");p.write_text(s)
# Preserve the reviewed fusion matching; specialize selected final entries only
# after fusion, including all constituents of a selected fused entry.
p=src/'decoder.cpp';s=p.read_text().replace('namespace fiberish {','namespace fiberish {\nHandlerFunc RegCacheSpecialize(const DecodedOp* op);',1);needle='    ApplySuperOpcodesToBlockOps(reinterpret_cast<DecodedOp*>(temp_ops.data()), decoded_inst_count);';assert needle in s;s=s.replace(needle,needle+'''\n#if REGCACHE_CLONES
    for (uint32_t i=0;i<decoded_inst_count;i++) if (temp_ops[i].head.len != 0)
        if (auto h = RegCacheSpecialize(&temp_ops[i].head)) temp_ops[i].head.handler = h;
#endif''');p.write_text(s)
original=extend((repo/'artifacts/coremark-3800-20261117/observed.generated.cpp').read_text())
# Parsed copies of existing fused bodies, not new semantics.
def body_for(s,name):
 start=s.index('ATTR_PRESERVE_NONE int64_t '+name+'(');b=s.index('{',start);depth=1;i=b+1
 while depth:
  if s[i]=='{':depth+=1
  elif s[i]=='}':depth-=1
  i+=1
 return s[start:i]
wrapper=body_for((src/'dispatch.h').read_text(),'DispatchWrapper')
def logic_name(symbol):
 m=re.search(r'fiberish::op::(Op\w+)\(',symbol)
 return 'op::'+m[1] if m else None
patterns=[]
for line in (out/'pattern-counts.jsonl').read_text().splitlines():
 x=json.loads(line);name=x['symbol'];m=re.search(r'fiberish::(SuperOpcode_([24])_\w+)\(',name)
 if m:x['original']=m[1];x['width']=int(m[2]);x['kind']='fused'
 else:
  op=logic_name(name)
  if not op or op.startswith('op::OpJcc_'):continue
  x['original']='DispatchWrapper<'+op+'>';x['width']=1;x['kind']='wrapper';x['logic']=op
 patterns.append(x)
patterns=sorted(patterns,key=lambda x:(-x['count'],x['symbol'],x['shapes']))[:int(os.environ.get('REGCACHE_LIMIT','64'))]
(out/os.environ.get('REGCACHE_SELECTION','selected-patterns.json')).write_text(json.dumps(patterns,indent=2)+'\n')
code=['namespace fiberish {\n#if REGCACHE_CLONES', '''template<uint64_t Shape> FORCE_INLINE void RCAssumeShape(const DecodedOp* op) {
    __builtin_assume(op->modrm == (Shape & 255));
    __builtin_assume(op->prefixes.all == ((Shape >> 8) & 255));
    if constexpr (Shape >> 16) {
        const auto actual = GetExt(op)->data.ea_desc;
        __builtin_assume(actual == uint32_t(Shape >> 16));
    }
}''']
for i,p in enumerate(patterns):
 name=f'RegCacheClone_{i:03d}_'+(p['original'] if p['kind']=='fused' else p['logic'][4:]);p['clone']=name
 if p['kind']=='fused':f=body_for(original,p['original']).replace(p['original'],name)
 else:f=re.sub(r'\bTarget\b',p['logic'],wrapper.replace('DispatchWrapper',name))
 pos=f.index('{')+1
 assumes='\n'+''.join('    RCAssumeShape<'+str(p['shapes'][j])+'ULL>(op + '+str(j)+');\n' for j in range(p['width']))
 f=f[:pos]+assumes+f[pos:];code.append(f)
code += ['#endif', 'HandlerFunc RegCacheSpecialize(const DecodedOp* op) {\n#if REGCACHE_CLONES', '    struct Pattern { HandlerFunc original, replacement; uint8_t width; uint64_t shapes[4]; };','    static const Pattern patterns[] = {']
for p in patterns:code.append('        { '+p['original']+', '+p['clone']+', '+str(p['width'])+', {'+','.join(str(x)+'ULL' for x in p['shapes'])+'} },')
code += ['    };','''    for (const auto& p : patterns) {
        if (op->handler != p.original) continue;
        bool match = true;
        for (int i=0;i<p.width;i++) {
            const auto* cur = op+i;
            uint32_t ea = cur->meta.flags.has_mem ? GetExt(cur)->data.ea_desc : 0;
            const uint64_t shape = uint64_t(cur->modrm) | (uint64_t(cur->prefixes.all)<<8) | (uint64_t(ea)<<16);
            if (shape != p.shapes[i]) { match=false; break; }
        }
        if (match) return p.replacement;
    }
#else
    (void)op;
#endif
    return nullptr;
}
} // namespace fiberish''']
(out/os.environ.get('REGCACHE_OUTPUT','regcache.generated.cpp')).write_text(original+'\n'+'\n'.join(code)+'\n')
print('Selected clones',len(patterns),'dynamic entries',sum(p['count'] for p in patterns))
for p in patterns[:6]:print(p['count'],p['original'],p['shapes'])
