using System.Runtime.InteropServices;

namespace Fiberish.X86.Native;

public unsafe partial class X86Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct InstructionVariant
    {
        public uint schema_version;
        public uint opcode;
        public ulong variant_key;
        public IntPtr handler;
        private IntPtr logic_name;
        private IntPtr family_name;
        private IntPtr fusion_parent;
        public int priority;
        public uint bake_with_parent;
        public uint is_fallback;
        public fixed uint constraints[19];
        public string LogicName => Marshal.PtrToStringUTF8(logic_name) ?? "";
        public string FamilyName => Marshal.PtrToStringUTF8(family_name) ?? "";
        public string FusionParent => Marshal.PtrToStringUTF8(fusion_parent) ?? "";
    }

    [LibraryImport(LibName, EntryPoint = "X86_GetInstructionVariantCount")]
    public static partial int GetInstructionVariantCount();

    [LibraryImport(LibName, EntryPoint = "X86_GetInstructionVariant")]
    public static partial int GetInstructionVariant(int index, out InstructionVariant result);

    [LibraryImport(LibName, EntryPoint = "X86_GetInstructionCatalogHash")]
    private static partial IntPtr GetInstructionCatalogHashUtf8();
    public static string GetInstructionCatalogHash() => Marshal.PtrToStringUTF8(GetInstructionCatalogHashUtf8()) ?? "";

}
