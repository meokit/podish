#!/usr/bin/env python3
"""Generate the IA-32 GLES 2.0 and 3.0 entry points and matching ANGLE calls."""
import argparse
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent
HEADER = ROOT / "guest/include/GLES3/gl3.h"
HOST = ROOT.parent / "Podish.Core/Graphics"
TYPES = {
    "GLbitfield": "uint", "GLboolean": "byte", "GLenum": "uint", "GLfloat": "float",
    "GLint": "int", "GLintptr": "nint", "GLsizei": "int", "GLsizeiptr": "nint",
    "GLuint": "uint", "void": "void",
    "GLsync": "nint", "GLuint64": "ulong",
}
NULLABLE_INPUT = {"glTexImage2D", "glTexImage3D", "glCompressedTexImage2D", "glCompressedTexImage3D"}
OPTIONAL_OUTPUT = {"count", "length"}
SPECIAL = {
    "glBindBuffer", "glBufferData", "glDeleteBuffers", "glDrawArrays", "glDrawArraysInstanced", "glDrawElements",
    "glDrawElementsInstanced", "glDrawRangeElements", "glGetActiveUniform", "glGetString", "glGetStringi",
    "glGetVertexAttribPointerv", "glPixelStorei", "glShaderSource", "glVertexAttribPointer", "glVertexAttribIPointer",
    "glMapBufferRange", "glFlushMappedBufferRange", "glUnmapBuffer", "glGetBufferPointerv", "glFenceSync",
    "glGetUniformIndices", "glTransformFeedbackVaryings",
}
NAMED_OPS = (
    "glBindSampler", "glBindTexture", "glCompileShader", "glCompressedTexImage2D", "glCompressedTexSubImage2D", "glCopyTexImage2D",
    "glCopyTexSubImage2D", "glDeleteFramebuffers", "glDeleteProgram", "glDeleteRenderbuffers", "glDeleteSamplers", "glDeleteShader",
    "glDeleteTextures", "glDetachShader", "glDrawArrays", "glDrawArraysInstanced", "glDrawElements",
    "glDrawElementsInstanced", "glDrawRangeElements",
    "glFinish", "glFlush", "glFramebufferRenderbuffer", "glFramebufferTexture2D", "glFramebufferTextureLayer", "glGenerateMipmap", "glGetError",
    "glGetBooleanv", "glGetFloatv", "glGetFramebufferAttachmentParameteriv", "glGetIntegerv", "glGetStringi",
    "glGetRenderbufferParameteriv", "glGetSamplerParameterfv", "glGetSamplerParameteriv", "glGetShaderSource",
    "glGetTexParameterfv", "glGetTexParameteriv", "glIsSampler", "glIsTexture",
    "glLinkProgram", "glRenderbufferStorage", "glSamplerParameterf", "glSamplerParameterfv", "glSamplerParameteri",
    "glSamplerParameteriv", "glTexImage2D", "glTexParameterf", "glTexParameterfv", "glTexParameteri",
    "glTexParameteriv", "glTexSubImage2D", "glTexStorage2D", "glUseProgram", "glClientWaitSync",
)


def pointer_rule(name, arg, index):
    if name in ("glBindAttribLocation", "glGetAttribLocation", "glGetUniformLocation",
                "glGetFragDataLocation", "glGetUniformBlockIndex"):
        return ("String", None)
    if name in ("glBufferSubData", "glProgramBinary"):
        return ("Input", "(int)a[3]" if name == "glProgramBinary" else "(int)a[2]")
    if name in ("glInvalidateFramebuffer", "glInvalidateSubFramebuffer"):
        return ("Input", "Count(a[1], 4)")
    if name == "glDrawBuffers":
        return ("Input", "Count(a[0], 4)")
    if name.startswith("glClearBuffer"):
        return ("Input", "a[0] == 0x1800 ? 16 : 4")
    if name.startswith("glCompressedTex"):
        return ("Unpack", f"(int)a[{index-1}]")
    if name in ("glTexImage2D", "glTexSubImage2D", "glTexImage3D", "glTexSubImage3D"):
        width = (3 if name.startswith("glTexImage") else 4 if name.endswith("2D") else 5)
        depth = f"a[{width+2}]" if name.endswith("3D") else "1"
        volume = "true" if name.endswith("3D") else "false"
        return ("Unpack", f"PixelBytes(context, a[{width}], a[{width+1}], {depth}, "
                f"a[{index-2}], a[{index-1}], pack: false, volume: {volume})")
    if name == "glReadPixels":
        return ("Pack", "PixelBytes(context, a[2], a[3], 1, a[4], a[5], pack: true)")
    if name in ("glGetActiveUniformsiv", "glGetSynciv"):
        return ("Input" if arg == "uniformIndices" else "Output",
                "Count(a[1], 4)" if name == "glGetActiveUniformsiv" else
                "4" if arg == "length" else "Count(a[2], 4)")
    if name in ("glGetActiveUniformBlockName", "glGetTransformFeedbackVarying"):
        return ("Output", "4" if arg in ("length", "size", "type") else "Count(a[2], 1)")
    if name == "glGetActiveUniformBlockiv":
        return ("Output", "UniformBlockCount(a[0], a[1], a[2]) * 4")
    if name == "glGetInternalformativ":
        return ("Output", "Count(a[3], 4)")
    if name == "glGetProgramBinary":
        return ("Output", "Count(a[1], 1)" if arg == "binary" else "4")
    if name in ("glGetInteger64i_v", "glGetBufferParameteri64v"):
        return ("Output", "8")
    if name == "glGetIntegeri_v":
        return ("Output", "4")
    if name.startswith(("glDelete", "glGen")):
        return ("Input" if name.startswith("glDelete") else "Output", "Count(a[0], 4)")
    if name in ("glGetActiveAttrib", "glGetActiveUniform"):
        return ("Output", "Count(a[2], 1)" if arg == "name" else "4")
    if name in ("glGetProgramInfoLog", "glGetShaderInfoLog", "glGetShaderSource"):
        return ("Output", "4" if arg == "length" else "Count(a[1], 1)")
    if name == "glGetAttachedShaders":
        return ("Output", "4" if arg == "count" else "Count(a[1], 4)")
    if name == "glGetShaderPrecisionFormat":
        return ("Output", "8" if arg == "range" else "4")
    if name in ("glGetBooleanv", "glGetFloatv", "glGetIntegerv", "glGetInteger64v"):
        size = {"glGetBooleanv": 1, "glGetInteger64v": 8}.get(name, 4)
        return ("Output", f"Count((uint)StateCount(a[0]), {size})")
    if name.startswith("glGetVertexAttrib"):
        return ("Output", "a[1] == 0x8626 ? 16 : 4")
    if name in ("glGetUniformfv", "glGetUniformiv", "glGetUniformuiv"):
        return ("Output", "UniformCount(a[0], (int)a[1]) * 4")
    if name.startswith("glGet"):
        return ("Output", "4")
    if name == "glShaderBinary":
        return ("Input", "Count(a[0], 4)" if arg == "shaders" else "(int)a[4]")
    if name.startswith(("glTexParameter", "glSamplerParameter")):
        return ("Input", "4")
    if name.startswith("glUniform"):
        match = re.search(r"(Matrix)?([1-4])(?:x([2-4]))?", name)
        count = int(match[2]) * (int(match[3] or match[2]) if match[1] else 1)
        return ("Input", f"Count(a[1], {count*4})")
    if name.startswith("glVertexAttrib"):
        return ("Input", str(int(name.removeprefix("glVertexAttrib").lstrip("I")[0]) * 4))
    raise ValueError(name)


def generate():
    matches = re.findall(r"^GL_APICALL (.+?)\s*GL_APIENTRY (gl\w+) \(([^\n]*)\);",
                         HEADER.read_text(), re.M)
    es2 = re.findall(r"^GL_APICALL .*?GL_APIENTRY (gl\w+) \(", (ROOT / "guest/include/GLES2/gl2.h").read_text(), re.M)
    assert len(matches) == 246 and [name for _, name, _ in matches[:142]] == es2
    guest = ["/* clang-format off */", "/* Generated by graphics/generate.py. */", '#include "transport.h"', ""]
    host = ["// Generated by graphics/generate.py.", "using Fiberish.Native;", "",
            "namespace Podish.Core.Graphics;", "",
            "internal sealed unsafe partial class AngleDevice", "{",
            "    private int DispatchGl(Context context, uint op, Span<uint> a, GuestMemory guest)",
            "    {", "        var proc = _angle.Gl[(int)op - GlFirst];",
            "        switch (op)", "        {"]
    symbols = []
    for op, (result, name, params) in enumerate(matches, start=64):
        result = result.strip()
        original = params.split(", ") if params != "void" else []
        args = [re.fullmatch(r"(.+?\W)(\w+)", p).groups() for p in original]
        symbols.append(name)
        if name == "glGetString":
            guest.append(f"PODISH_EXPORT const GLubyte *{name}({params}) {{ return pg_string({op}, name); }}")
        elif name == "glGetStringi":
            guest.append(f"PODISH_EXPORT const GLubyte *{name}({params}) {{ return pg_string_index({op}, name, index); }}")
        elif name == "glMapBufferRange":
            guest.append(f"PODISH_EXPORT void *{name}({params}) {{ uint32_t address = 0; "
                         f"return pg_gl({op}, target, offset, length, access, pg_ptr(&address)) == 0 "
                         "? (void *)(uintptr_t)address : NULL; }")
        else:
            encoded = []
            for t, arg in args:
                if t.strip() == "GLuint64":
                    encoded.extend((f"(uint32_t){arg}", f"(uint32_t)({arg} >> 32)"))
                elif t.strip() == "GLfloat":
                    encoded.append(f"pg_float({arg})")
                elif "*" in t or t.strip() == "GLsync":
                    encoded.append(f"pg_ptr({arg})")
                else:
                    encoded.append(f"(uint32_t){arg}")
            assert len(encoded) <= 11
            call = f"pg_gl({op}" + (", " + ", ".join(encoded) if encoded else "") + ")"
            guest.append(f"PODISH_EXPORT {result} {name}({params}) {{ " +
                         (call if result == "void" else
                          f"int id = {call}; return id > 0 ? (GLsync)(uintptr_t)id : NULL" if result == "GLsync" else
                          f"return {call} > 0" if result == "GLboolean" else
                          f"return ({result}){call}") + "; }")

        if name in SPECIAL:
            host += [f"            case {op}: return {name[2:]}(context, proc, a, guest);"]
            continue

        native_types = []
        native_args = []
        setup = []
        outputs = []
        fixed = []
        i = 0
        for ctype, arg in args:
            if "*" in ctype:
                native_types.append("void*")
                method, size = pointer_rule(name, arg, i)
                expr = f"a[{i}]" + (f", {size}" if size else "")
                optional = name in NULLABLE_INPUT
                if optional:
                    expr += ", nullable: true"
                if method in ("Pack", "Unpack"):
                    binding = "PixelPackBufferBinding" if method == "Pack" else "PixelUnpackBufferBinding"
                    setup += [f"                var buffered = context.Version >= 3 && GetInteger({binding}) != 0;",
                              f"                var size{i} = buffered" + (f" || a[{i}] == 0" if optional else "") +
                              f" ? 0 : {size};",
                              f"                var b{i} = buffered ? null : guest." +
                              f"{'Output' if method == 'Pack' else 'Input'}(a[{i}], size{i}" +
                              (", nullable: true" if optional else "") + ");"]
                    native_args.append(f"buffered ? (void*)(nuint)a[{i}] : p{i}")
                    if method == "Pack":
                        outputs.append(f"                    guest.WriteOutput(a[{i}], b{i}, size{i});")
                else:
                    setup.append(f"                var b{i} = guest.{method}({expr});")
                    native_args.append(f"p{i}")
                fixed.append(f"                fixed (byte* p{i} = b{i})")
                if method == "Output":
                    outputs.append(f"                    guest.WriteOutput(a[{i}], b{i}, size{i});")
                    nullable = ", nullable: true" if arg in OPTIONAL_OUTPUT else ""
                    setup[-1] = (f"                var size{i} = {size};\n"
                                 f"                var b{i} = guest.Output(a[{i}], size{i}{nullable});")
            else:
                cstype = TYPES[ctype.strip()]
                native_types.append(cstype)
                native_args.append({
                    "float": f"BitConverter.UInt32BitsToSingle(a[{i}])",
                    "nint": f"(nint)(int)a[{i}]",
                    "uint": f"a[{i}]",
                    "ulong": f"a[{i}] | ((ulong)a[{i+1}] << 32)",
                }.get(cstype, f"({cstype})a[{i}]") if ctype.strip() != "GLsync" else "sync")
                if ctype.strip() == "GLsync":
                    setup.append(f"                var sync = context.Shared.Syncs.GetValueOrDefault(a[{i}]);")
                    if name == "glDeleteSync":
                        outputs.append(f"                    context.Shared.Syncs.Remove(a[{i}]);")
                if cstype == "ulong":
                    i += 1
            i += 1

        signature = ", ".join(native_types + [TYPES[result]])
        call = f"((delegate* unmanaged[Cdecl]<{signature}>)proc)({', '.join(native_args)})"
        if name in ("glGetBooleanv", "glGetFloatv", "glGetIntegerv", "glGetInteger64v"):
            value_type = {"glGetBooleanv": "byte", "glGetFloatv": "float", "glGetInteger64v": "long"}.get(name, "int")
            setup.insert(0, f"                if (a[0] == 0x821D && context.Version >= 3) "
                            f"return WriteExtensionCount<{value_type}>(context, a[1], guest);")
        host.extend([f"            case {op}: // {name}", "            {"] + setup + fixed)
        indent = "                    " if fixed else "                "
        if fixed:
            host.append("                {")
        if result == "void":
            host.extend([indent + call + ";"] + outputs + [indent + "return 0;"])
        else:
            host.append(indent + f"return (int){call};")
        if fixed:
            host.append("                }")
        host.append("            }")

    guest += ["", "void *pg_get_gl_proc(const char *name)", "{",
              "    static const struct { const char *name; void *address; } procs[] = {"]
    guest.extend(f'        {{ "{name}", (void *)&{name} }},' for name in symbols)
    guest += ["    };", "    for (unsigned i = 0; i < sizeof(procs) / sizeof(procs[0]); ++i)",
              "        if (!strcmp(name, procs[i].name)) return procs[i].address;",
              "    return NULL;", "}", ""]
    host += ["            default: return -(int)Errno.ENOTTY;", "        }", "    }", "",
             "    internal const int GlFirst = 64, GlEs3First = GlFirst + 142;"]
    host.extend(f"    private const int Gl{name[2:]} = {64 + symbols.index(name)};" for name in NAMED_OPS)
    host += ["", "    internal static readonly string[] GlNames =", "    ["]
    host.extend(f'        "{name}",' for name in symbols)
    host += ["    ];", "}", ""]
    return {
        ROOT / "guest/gles.generated.c": "\n".join(guest),
        HOST / "AngleDevice.Gl.g.cs": "\n".join(host),
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    for path, content in generate().items():
        if args.check:
            if path.read_text() != content:
                parser.exit(1, f"{path} is out of date\n")
        else:
            path.write_text(content)
