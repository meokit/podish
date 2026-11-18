import pathlib,subprocess,json,ctypes,re,struct,collections,argparse
parser=argparse.ArgumentParser();parser.add_argument('--out',type=pathlib.Path,required=True);parser.add_argument('--repo',type=pathlib.Path,default=pathlib.Path.cwd());args=parser.parse_args()
out=args.out.resolve();repo=args.repo.resolve();libpath=out/'profile-cli/libfibercpu.dylib'
lib=ctypes.CDLL(str(libpath));lib.X86_GetHandlerById.restype=ctypes.c_void_p;lib.X86_GetHandlerId.argtypes=[ctypes.c_void_p];lib.X86_GetHandlerSymbolById.restype=ctypes.c_char_p
symbol=next(line.split()[-1] for line in subprocess.check_output(['/usr/bin/nm',str(libpath)],text=True).splitlines() if 'CanonicalOperandHandler' in line and ' T ' in line)
canon=lib[symbol[1:]];canon.argtypes=[ctypes.c_void_p];canon.restype=ctypes.c_void_p
class Variant(ctypes.Structure):
 _fields_=[('schema',ctypes.c_uint32),('opcode',ctypes.c_uint32),('key',ctypes.c_uint64),('handler',ctypes.c_void_p),('logic',ctypes.c_char_p),('family',ctypes.c_char_p),('parent',ctypes.c_char_p),('priority',ctypes.c_int),('bake',ctypes.c_uint),('fallback',ctypes.c_uint),('criteria',ctypes.c_uint*19)]
assert ctypes.sizeof(Variant)==136
names={}
for i in range(lib.X86_GetInstructionVariantCount()):
 v=Variant();assert lib.X86_GetInstructionVariant(i,ctypes.byref(v));names[lib.X86_GetHandlerId(v.handler)]='DispatchWrapper<'+v.logic.decode()+'>'
ids={}
for i in range(lib.X86_GetHandlerCount()):
 ptr=canon(lib.X86_GetHandlerById(i));cid=lib.X86_GetHandlerId(ptr)
 s=lib.X86_GetHandlerSymbolById(cid).decode();m=re.search(r'(SuperOpcode_([234])_\w+)\(',s)
 ids[i]=(m[1],int(m[2]),'fused') if m else (names.get(cid,s),1,'wrapper')
# Handwritten entries have widths in the native registration callback, not their names.
src=(repo/'libfibercpu/src/superopcodes.cpp').read_text()
hw={m[1]:int(m[2]) for m in re.finditer(r'handwritten_superopcodes::(HandWrittenSuperOpcode_\w+),\s*([234])\);',src)}
for i,(name,width,kind) in list(ids.items()):
 for key,w in hw.items():
  if key in name:ids[i]=(name,w,'handwritten');break
commands={'gunzip':'/bin/gzip -dc input.gz > result.txt','grep_regex':"/bin/grep -E -c 'group=(07|13|42)' input.txt > result.txt",'gzip':'/bin/gzip -n -6 -c input.txt > result.gz','sed':"/bin/sed -e 's/row=/record=/g' input.txt > result.txt",'sha256':'/usr/bin/sha256sum input.txt > result.txt','tar':'/bin/tar --format=ustar -cf result.tar tar-input'}
allrows=[]
for case,command in commands.items():
 dest=out/('counts-'+case);dest.mkdir(exist_ok=True)
 cmd=['dotnet',str(out/'profile-cli/Podish.Cli.dll'),'run','--rm','--log-driver','none','--guest-stats-dir',str(dest),'--rootfs',str(out/'rootfs'),'--env','LC_ALL=C','/bin/bash','--noprofile','--norc','-c','set -eu; cd /work/manual; '+command]
 with (out/(case+'-counts.log')).open('w') as log:subprocess.run(cmd,stdout=log,stderr=subprocess.STDOUT,check=True)
 b=(dest/'blocks.bin').read_bytes();pos=0
 def read(fmt):
  global pos
  v=struct.unpack_from('<'+fmt,b,pos);pos+=struct.calcsize('<'+fmt);return v
 magic,version,base,nblocks,nh=read('IIQii');assert magic==0x324b4c42 and version==2
 for _ in range(nh):
  hid,opid,ptr,length=read('iiQI');pos+=length
 counts=collections.Counter()
 for _ in range(nblocks):
  start,end,n,executions=read('IIIQ');ops=[read('QIBBBBIIQi') for j in range(n)]
  if not executions:continue
  j=0
  while j<n:
   op=ops[j];name,width,kind=ids.get(op[-1],('unknown',1,'unknown'))
   if j+width>n:width=1;kind='unknown'
   shapes=tuple(ops[j+k][3] | (ops[j+k][4]<<8) | ((ops[j+k][0]>>32)<<16) for k in range(width))+(0,)*(4-width)
   counts[(name,kind,width,shapes)]+=executions;j+=width
 assert pos==len(b),(pos,len(b))
 rows=[dict(original=name,kind=kind,width=width,shapes=list(shapes),count=count,workload=case) for (name,kind,width,shapes),count in counts.most_common()]
 (out/(case+'-shapes.json')).write_text(json.dumps(rows,indent=2)+'\n');allrows+=rows;print(case,'blocks',nblocks,'dispatch estimates',sum(counts.values()),'top',rows[0]['original'],flush=True)
(out/'gnu-shapes.json').write_text(json.dumps(allrows,indent=2)+'\n')
