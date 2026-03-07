using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Fiberish.Core.Native;

internal static class NativeLibraryResolver
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> LibraryNames = new(StringComparer.Ordinal);
    private static bool _installed;
    private static Assembly? _assembly;

    public static void Register(Type markerType, string libraryName)
    {
        lock (Gate)
        {
            if (_assembly == null)
                _assembly = markerType.Assembly;
            else if (!ReferenceEquals(_assembly, markerType.Assembly))
                throw new InvalidOperationException("NativeLibraryResolver supports one assembly per process.");

            LibraryNames.Add(libraryName);

            if (_installed)
                return;

            try
            {
                NativeLibrary.SetDllImportResolver(markerType.Assembly, Resolve);
            }
            catch (InvalidOperationException)
            {
                // Resolver already registered in this AssemblyLoadContext.
            }

            _installed = true;
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        lock (Gate)
        {
            if (!LibraryNames.Contains(libraryName))
                return IntPtr.Zero;
        }

        // Unified strategy: NativeAOT/static host -> __Internal first.
        if (!RuntimeFeature.IsDynamicCodeSupported &&
            NativeLibrary.TryLoad("__Internal", assembly, searchPath, out var internalHandle))
            return internalHandle;

        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var handle))
            return handle;

        return IntPtr.Zero;
    }
}
