namespace Podish.Core;

public sealed record OciStoredLayer(
    string Digest,
    string MediaType,
    long Size,
    string BlobPath,
    string IndexPath);

public sealed record OciStoredImage(
    string ImageReference,
    string Registry,
    string Repository,
    string Tag,
    string ManifestDigest,
    string StoreDirectory,
    IReadOnlyList<OciStoredLayer> Layers);
