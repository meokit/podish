namespace Fiberish.Memory;

internal static class FilePageBackendSelector
{
    public static IFilePageBackend Create(string path)
    {
        return Create(path, HostMemoryMapGeometry.CreateCurrent());
    }

    public static IFilePageBackend Create(string path, HostMemoryMapGeometry geometry)
    {
        if (geometry.SupportsMappedFileBackend)
            return new WindowedMappedFilePageBackend(path, geometry);

        return new BufferedPageBackend();
    }
}
