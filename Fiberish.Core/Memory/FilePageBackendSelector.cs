namespace Fiberish.Memory;

internal static class FilePageBackendSelector
{
    public static IFilePageBackend Create(string path)
    {
        var geometry = HostMemoryMapGeometryProvider.GetCurrent();
        if (geometry.SupportsMappedFileBackend)
            return new WindowedMappedFilePageBackend(path, geometry);

        return new BufferedPageBackend();
    }
}