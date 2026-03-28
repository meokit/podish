using System.Runtime.InteropServices.JavaScript;
using Fiberish.X86.Native;
using Podish.Core;

namespace PodishApp.BrowserWasm;

public static partial class BrowserExports
{
    [JSExport]
    public static string GetRuntimeInfo()
    {
        return $"Podish.Core loaded: {typeof(PodishContext).Assembly.GetName().Name}";
    }

    [JSExport]
    public static string ProbeNative()
    {
        var state = X86Native.Create();
        try
        {
            return state == IntPtr.Zero
                ? "X86_Create returned null"
                : $"libfibercpu linked successfully, state=0x{state.ToInt64():x}";
        }
        finally
        {
            if (state != IntPtr.Zero)
                X86Native.Destroy(state);
        }
    }
}
