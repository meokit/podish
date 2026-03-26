using System.Runtime.InteropServices;

namespace Podish.Wayland;

internal sealed class WaylandKeyboardKeymap
{
    private readonly Mount _mount;
    private readonly Dentry _fileDentry;
    private readonly LinuxFile _readOnlyFile;

    public WaylandKeyboardKeymap()
    {
        string keymap = XkbCommon.GenerateDefaultKeymap();
        byte[] keymapBytes = System.Text.Encoding.UTF8.GetBytes(keymap);
        if (keymapBytes.Length == 0 || keymapBytes[^1] != 0)
            keymapBytes = [.. keymapBytes, 0];

        var fsType = new FileSystemType
        {
            Name = "tmpfs",
            Factory = static devMgr => new Tmpfs(devMgr)
        };
        FileSystem fs = fsType.CreateAnonymousFileSystem();
        SuperBlock sb = fs.ReadSuper(fsType, 0, "wayland-keymap", null);
        _mount = new Mount(sb, sb.Root);

        _fileDentry = new Dentry("keymap.xkb", null, sb.Root, sb);
        sb.Root.Inode!.Create(_fileDentry, 0x1A4, 0, 0);

        var writer = new LinuxFile(_fileDentry, FileFlags.O_RDWR, _mount);
        try
        {
            int rc = _fileDentry.Inode!.Write(writer, keymapBytes, 0);
            if (rc < 0)
                throw new IOException($"Failed to write Wayland keymap: rc={rc}");
        }
        finally
        {
            writer.Close();
        }

        // Keep a dedicated read-only handle for wl_keyboard.keymap, matching wlroots' fd-pair model.
        _readOnlyFile = new LinuxFile(_fileDentry, FileFlags.O_RDONLY, _mount);

        Size = (uint)keymapBytes.Length;
    }

    public uint Size { get; }

    public LinuxFile OpenReadOnly()
    {
        return _readOnlyFile;
    }

    private static class XkbCommon
    {
        private const int XkbContextNoFlags = 0;
        private const int XkbKeymapCompileNoFlags = 0;
        private const int XkbKeymapFormatTextV1 = 1;

        private static readonly Lazy<Api> _api = new(Api.Load);

        public static string GenerateDefaultKeymap()
        {
            Api api = _api.Value;
            IntPtr context = api.ContextNew(XkbContextNoFlags);
            if (context == IntPtr.Zero)
                throw new InvalidOperationException("xkb_context_new failed.");

            try
            {
                IntPtr keymap = api.KeymapNewFromNames(context, IntPtr.Zero, XkbKeymapCompileNoFlags);
                if (keymap == IntPtr.Zero)
                    throw new InvalidOperationException("xkb_keymap_new_from_names failed.");

                try
                {
                    IntPtr textPtr = api.KeymapGetAsString(keymap, XkbKeymapFormatTextV1);
                    if (textPtr == IntPtr.Zero)
                        throw new InvalidOperationException("xkb_keymap_get_as_string failed.");

                    return Marshal.PtrToStringUTF8(textPtr)
                           ?? throw new InvalidOperationException("xkb keymap string was null.");
                }
                finally
                {
                    api.KeymapUnref(keymap);
                }
            }
            finally
            {
                api.ContextUnref(context);
            }
        }

        private sealed class Api
        {
            private readonly IntPtr _handle;

            private Api(IntPtr handle, XkbContextNewDelegate contextNew, XkbContextUnrefDelegate contextUnref,
                XkbKeymapNewFromNamesDelegate keymapNewFromNames, XkbKeymapGetAsStringDelegate keymapGetAsString,
                XkbKeymapUnrefDelegate keymapUnref)
            {
                _handle = handle;
                ContextNew = contextNew;
                ContextUnref = contextUnref;
                KeymapNewFromNames = keymapNewFromNames;
                KeymapGetAsString = keymapGetAsString;
                KeymapUnref = keymapUnref;
            }

            public XkbContextNewDelegate ContextNew { get; }
            public XkbContextUnrefDelegate ContextUnref { get; }
            public XkbKeymapNewFromNamesDelegate KeymapNewFromNames { get; }
            public XkbKeymapGetAsStringDelegate KeymapGetAsString { get; }
            public XkbKeymapUnrefDelegate KeymapUnref { get; }

            public static Api Load()
            {
                string[] candidates =
                [
                    "libxkbcommon.dylib",
                    "xkbcommon",
                    "/opt/local/lib/libxkbcommon.dylib",
                    "/opt/homebrew/lib/libxkbcommon.dylib",
                    "/usr/local/lib/libxkbcommon.dylib"
                ];

                List<Exception> failures = [];
                foreach (string candidate in candidates)
                {
                    try
                    {
                        IntPtr handle = NativeLibrary.Load(candidate);
                        return new Api(
                            handle,
                            GetExport<XkbContextNewDelegate>(handle, "xkb_context_new"),
                            GetExport<XkbContextUnrefDelegate>(handle, "xkb_context_unref"),
                            GetExport<XkbKeymapNewFromNamesDelegate>(handle, "xkb_keymap_new_from_names"),
                            GetExport<XkbKeymapGetAsStringDelegate>(handle, "xkb_keymap_get_as_string"),
                            GetExport<XkbKeymapUnrefDelegate>(handle, "xkb_keymap_unref"));
                    }
                    catch (Exception ex)
                    {
                        failures.Add(ex);
                    }
                }

                throw new InvalidOperationException("Unable to load libxkbcommon.", new AggregateException(failures));
            }

            private static T GetExport<T>(IntPtr handle, string name) where T : Delegate
            {
                return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, name));
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr XkbContextNewDelegate(int flags);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void XkbContextUnrefDelegate(IntPtr context);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr XkbKeymapNewFromNamesDelegate(IntPtr context, IntPtr names, int flags);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr XkbKeymapGetAsStringDelegate(IntPtr keymap, int format);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void XkbKeymapUnrefDelegate(IntPtr keymap);
    }
}
