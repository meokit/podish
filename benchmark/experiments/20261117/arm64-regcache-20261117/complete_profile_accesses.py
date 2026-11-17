from pathlib import Path
import re
src=Path(__file__).resolve().parent/'profile-source/libfibercpu/src/ops'
changed=[]
for p in src.glob('*_impl.h'):
 s=p.read_text();old=s
 s=re.sub(r'state->ctx.regs\[([^\n;]+?)\] = ([^;]+);',r'SetReg(state, \1, \2);',s)
 s=re.sub(r'state->ctx.regs\[([^\n;]+?)\]',r'GetReg(state, \1)',s)
 if s!=old:p.write_text(s);changed.append(p.name)
print('Instrumented direct register access paths:',changed)
