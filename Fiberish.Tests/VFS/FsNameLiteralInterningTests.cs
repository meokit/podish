using System.Reflection;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class FsNameLiteralInterningTests
{
    private static readonly FieldInfo BytesField =
        typeof(FsName).GetField("_bytes", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void FromString_Literal_ReusesGeneratedBackingStore()
    {
        var first = FsName.FromString("fsname_generator_from_string_literal");
        var second = FsName.FromString("fsname_generator_from_string_literal");

        Assert.Same(GetBackingBytes(first), GetBackingBytes(second));
    }

    [Fact]
    public void FromBytes_Utf8Literal_ReusesGeneratedBackingStore()
    {
        var first = FsName.FromBytes("fsname_generator_from_bytes_literal"u8);
        var second = FsName.FromBytes("fsname_generator_from_bytes_literal"u8);

        Assert.Same(GetBackingBytes(first), GetBackingBytes(second));
    }

    private static byte[] GetBackingBytes(FsName name)
    {
        return Assert.IsType<byte[]>(BytesField.GetValue(name));
    }
}
