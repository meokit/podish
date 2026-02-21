using Fiberish.Core.VFS.TTY;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fiberish.Tests.VFS;

public class PtyTests
{
    private readonly ILogger _logger = NullLogger.Instance;

    [Fact]
    public void PtyManager_EncodeRdev_CorrectEncoding()
    {
        // Test encoding
        var rdev = PtyManager.EncodeRdev(5, 2);
        Assert.Equal((uint)0x502, rdev);

        rdev = PtyManager.EncodeRdev(136, 0);
        Assert.Equal((uint)0x8800, rdev);

        rdev = PtyManager.EncodeRdev(136, 5);
        Assert.Equal((uint)0x8805, rdev);
    }

    [Fact]
    public void PtyManager_DecodeRdev_CorrectDecoding()
    {
        // Test decoding
        var (major, minor) = PtyManager.DecodeRdev(0x502);
        Assert.Equal(5u, major);
        Assert.Equal(2u, minor);

        (major, minor) = PtyManager.DecodeRdev(0x8800);
        Assert.Equal(136u, major);
        Assert.Equal(0u, minor);

        (major, minor) = PtyManager.DecodeRdev(0x8805);
        Assert.Equal(136u, major);
        Assert.Equal(5u, minor);
    }

    [Fact]
    public void PtyManager_GetPtmxRdev_ReturnsCorrectValue()
    {
        var rdev = PtyManager.GetPtmxRdev();
        Assert.Equal((uint)0x502, rdev); // Major 5, Minor 2

        var (major, minor) = PtyManager.DecodeRdev(rdev);
        Assert.Equal(PtyManager.PTMX_MAJOR, major);
        Assert.Equal(PtyManager.PTMX_MINOR, minor);
    }

    [Fact]
    public void PtyManager_GetPtsRdev_ReturnsCorrectValue()
    {
        var rdev = PtyManager.GetPtsRdev(0);
        Assert.Equal((uint)0x8800, rdev); // Major 136, Minor 0

        rdev = PtyManager.GetPtsRdev(5);
        Assert.Equal((uint)0x8805, rdev); // Major 136, Minor 5

        var (major, minor) = PtyManager.DecodeRdev(rdev);
        Assert.Equal(PtyManager.PTS_MAJOR, major);
        Assert.Equal(5u, minor);
    }

    [Fact]
    public void PtyManager_AllocatePty_ReturnsNonNull()
    {
        var manager = new PtyManager(_logger);
        var pair = manager.AllocatePty();

        Assert.NotNull(pair);
        Assert.Equal(0, pair.Index);
        Assert.NotNull(pair.Master);
        Assert.NotNull(pair.Slave);
    }

    [Fact]
    public void PtyManager_AllocateMultiplePtys_IncrementsIndex()
    {
        var manager = new PtyManager(_logger);

        var pair1 = manager.AllocatePty();
        var pair2 = manager.AllocatePty();
        var pair3 = manager.AllocatePty();

        Assert.NotNull(pair1);
        Assert.NotNull(pair2);
        Assert.NotNull(pair3);

        Assert.Equal(0, pair1.Index);
        Assert.Equal(1, pair2.Index);
        Assert.Equal(2, pair3.Index);
    }

    [Fact]
    public void PtyManager_GetPty_ReturnsCorrectPair()
    {
        var manager = new PtyManager(_logger);
        var pair = manager.AllocatePty();

        var retrieved = manager.GetPty(0);
        Assert.Same(pair, retrieved);
    }

    [Fact]
    public void PtyManager_PtyExists_ReturnsTrueForAllocated()
    {
        var manager = new PtyManager(_logger);

        Assert.False(manager.PtyExists(0));

        manager.AllocatePty();
        Assert.True(manager.PtyExists(0));
        Assert.False(manager.PtyExists(1));
    }

    [Fact]
    public void PtyBuffer_WriteAndRead_WorksCorrectly()
    {
        var buffer = new PtyBuffer(1024);
        var data = new byte[] { 1, 2, 3, 4, 5 };

        var written = buffer.Write(data);
        Assert.Equal(5, written);
        Assert.True(buffer.HasData);
        Assert.Equal(5, buffer.Count);

        var readBuffer = new byte[10];
        var read = buffer.Read(readBuffer);
        Assert.Equal(5, read);
        Assert.Equal(1, readBuffer[0]);
        Assert.Equal(5, readBuffer[4]);
    }

    [Fact]
    public void PtyBuffer_WriteBeyondCapacity_Truncates()
    {
        var buffer = new PtyBuffer(10);
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };

        var written = buffer.Write(data);
        Assert.Equal(10, written);

        // Buffer is full, next write should return 0
        written = buffer.Write(new byte[] { 13, 14 });
        Assert.Equal(0, written);
    }

    [Fact]
    public void PtyBuffer_ReadPartial_ReturnsAvailable()
    {
        var buffer = new PtyBuffer(1024);
        buffer.Write(new byte[] { 1, 2, 3, 4, 5 });

        var readBuffer = new byte[3];
        var read = buffer.Read(readBuffer);
        Assert.Equal(3, read);
        Assert.Equal(1, readBuffer[0]);
        Assert.Equal(3, readBuffer[2]);

        // Read remaining
        read = buffer.Read(readBuffer);
        Assert.Equal(2, read);
        Assert.Equal(4, readBuffer[0]);
        Assert.Equal(5, readBuffer[1]);
    }

    [Fact]
    public void PtyPair_Unlock_SetsIsLockedToFalse()
    {
        var manager = new PtyManager(_logger);
        var pair = manager.AllocatePty();

        // Default is unlocked (modern behavior)
        Assert.False(pair.IsLocked);

        // Test unlock is idempotent
        pair.Unlock();
        Assert.False(pair.IsLocked);
    }

    [Fact]
    public void PtyMaster_ReadWrite_WorksCorrectly()
    {
        var manager = new PtyManager(_logger);
        var pair = manager.AllocatePty();

        // Write from master (goes to slave's input)
        var data = new[] { (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o' };
        var written = pair.Master!.Write(data);
        Assert.Equal(5, written);

        // Read from slave's input buffer
        var readBuffer = new byte[10];
        var read = pair.Master!.InputBuffer.Read(readBuffer);
        Assert.Equal(5, read);
    }

    [Fact]
    public void PtyMaster_HasDataAvailable_ReflectsBufferState()
    {
        var manager = new PtyManager(_logger);
        var pair = manager.AllocatePty();

        Assert.False(pair.Master.HasDataAvailable);

        // Write to slave output buffer (simulating slave output)
        pair.Master!.OutputBuffer.Write(new byte[] { 1, 2, 3 });

        Assert.True(pair.Master.HasDataAvailable);
    }
}