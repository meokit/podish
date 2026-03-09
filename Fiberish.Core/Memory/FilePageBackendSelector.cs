namespace Fiberish.Memory;

internal static class FilePageBackendSelector
{
    public static IFilePageBackend Create(string path, bool writable)
    {
        if (SupportsMemoryMappedFiles())
            return new MmapFilePageBackend(path, writable);

        return new BufferedPageBackend();
    }

    private static bool SupportsMemoryMappedFiles()
    {
        if (OperatingSystem.IsBrowser()) return false;
#if NET8_0_OR_GREATER
        if (OperatingSystem.IsWasi()) return false;
#endif
        return true;
    }
}
