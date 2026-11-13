namespace Fiberish.Memory;

internal static class FilePageBackendSelector
{
    private static readonly AsyncLocal<Func<string, HostMemoryMapGeometry, IFilePageBackend?>?> TestFactoryOverride = new();

    internal static Func<string, HostMemoryMapGeometry, IFilePageBackend?>? FactoryOverrideForTests
    {
        get => TestFactoryOverride.Value;
        set => TestFactoryOverride.Value = value;
    }

    public static IFilePageBackend Create(string path)
    {
        return Create(path, HostMemoryMapGeometry.CreateCurrent());
    }

    public static IFilePageBackend Create(string path, HostMemoryMapGeometry geometry)
    {
        if (FactoryOverrideForTests?.Invoke(path, geometry) is { } testBackend)
            return testBackend;

        if (geometry.SupportsMappedFileBackend)
            return new WindowedMappedFilePageBackend(path, geometry);

        return new BufferedPageBackend();
    }
}