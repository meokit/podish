namespace Fiberish.VFS;

/// <summary>Double-buffered scanout for an in-process compositor using the virtual DRM card.</summary>
public sealed class VirtualDrmScanout
{
    private readonly Lock _gate = new();
    private byte[][]? _buffers;
    internal DrmScanoutBuffer[]? GpuBuffers { get; private set; }
    private DrmCardInode? _card;
    private int _front;
    private bool _flipPending;
    private bool _hasNewFrame;
    private uint _flipTimestamp;

    public VirtualDrmScanout(int width, int height)
    {
        if (width is < 1 or > FramebufferDevice.DimensionLimit)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height is < 1 or > FramebufferDevice.DimensionLimit)
            throw new ArgumentOutOfRangeException(nameof(height));

        Width = width;
        Height = height;
    }

    public int Width { get; }
    public int Height { get; }
    public int Pitch => Width * 4;
    public event Action? FrameReady;

    public void SetGpuBuffers(DrmScanoutBuffer first, DrmScanoutBuffer second)
    {
        lock (_gate)
        {
            if (_card != null || GpuBuffers != null || ReferenceEquals(first, second))
                throw new InvalidOperationException("Configure two distinct GPU buffers before attaching the virtual card.");
            foreach (var buffer in new[] { first, second })
                if (buffer.Width != Width || buffer.Height != Height || buffer.Pitch != Pitch)
                    throw new ArgumentException("GPU scanout buffers must match the virtual mode.");
            GpuBuffers = [first, second];
        }
    }

    internal void Attach(DrmCardInode card)
    {
        lock (_gate)
            _card = card;
    }

    /// <summary>Releases the compositor session on the kernel scheduler thread.</summary>
    public void Detach()
    {
        lock (_gate)
        {
            _card?.ReleaseCompositorScanout();
            _card = null;
            _flipPending = false;
            _hasNewFrame = false;
        }
    }

    // The capture function runs on the display thread, which also consumes completed frames.
    public bool TryQueueFrame(Action<Memory<byte>> capture) => TryQueue(buffer =>
    {
        if (GpuBuffers != null)
            throw new InvalidOperationException("The virtual card is configured for GPU scanout.");
        int size = Pitch * Height;
        _buffers ??= [new byte[size], new byte[size]];
        capture(_buffers[buffer]);
    });

    public bool TryQueueGpuFrame(Action<DrmScanoutBuffer> capture) =>
        TryQueue(buffer => capture((GpuBuffers ?? throw new InvalidOperationException("GPU scanout is not configured."))[buffer]));

    private bool TryQueue(Action<int> capture)
    {
        DrmCardInode? card;
        int back;
        lock (_gate)
        {
            card = _card;
            if (card == null || _flipPending || _hasNewFrame)
                return false;

            back = 1 - _front;
            _flipPending = true;
        }

        try
        {
            capture(back);
            lock (_gate)
            {
                if (_card != card)
                    return false;

                card.SubmitCompositorFlip(back);
                return true;
            }
        }
        catch
        {
            lock (_gate)
                _flipPending = false;
            throw;
        }
    }

    internal void CompleteFlip(int bufferIndex, uint timestamp)
    {
        lock (_gate)
        {
            if (_card == null)
                return;

            _front = bufferIndex;
            _flipTimestamp = timestamp;
            _hasNewFrame = true;
            _flipPending = false;
        }
        FrameReady?.Invoke();
    }

    /// <summary>Returns a completed frame for immediate use on the display thread.</summary>
    public bool TryTakeFrame(out ReadOnlyMemory<byte> pixels, out uint timestamp)
    {
        lock (_gate)
        {
            bool ready = ConsumeFlip(out timestamp);
            pixels = ready ? _buffers![_front] : default;
            return ready;
        }
    }

    public bool TryTakeGpuFrame(out DrmScanoutBuffer? buffer, out uint timestamp)
    {
        lock (_gate)
        {
            bool ready = ConsumeFlip(out timestamp);
            buffer = ready ? GpuBuffers![_front] : null;
            return ready;
        }
    }

    private bool ConsumeFlip(out uint timestamp)
    {
        timestamp = _flipTimestamp;
        bool ready = _hasNewFrame;
        _hasNewFrame = false;
        return ready;
    }
}
