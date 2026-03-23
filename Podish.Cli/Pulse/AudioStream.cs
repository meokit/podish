using System.Buffers.Binary;
using Podish.Pulse.Protocol;

namespace Podish.Cli.Pulse;

internal sealed class AudioStream
{
    private readonly object _gate = new();
    private readonly byte[] _ring;
    private readonly int _inputBytesPerFrame;
    private readonly byte _inputChannels;
    private readonly uint _inputRate;
    private readonly byte _outputChannels;
    private readonly uint _outputRate;
    private int _readOffset;
    private int _writeOffset;
    private int _queuedBytes;
    private double _sourcePosition;
    private bool _paused;
    private float _gain;
    private bool _hasPreviousFrame;
    private float _previousLeft;
    private float _previousRight;

    public AudioStream(
        uint channelIndex,
        SampleSpec inputSpec,
        ChannelMap channelMap,
        int capacity,
        float initialGain,
        bool paused,
        SampleSpec? outputSpec = null)
    {
        _ = channelMap;
        if (inputSpec.Format != SampleFormat.S16Le)
            throw new ArgumentOutOfRangeException(nameof(inputSpec), "Only S16Le is supported");
        if (inputSpec.Channels is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(inputSpec), "Only mono/stereo streams are supported");
        if (inputSpec.SampleRate == 0)
            throw new ArgumentOutOfRangeException(nameof(inputSpec), "Sample rate must be positive");

        SampleSpec resolvedOutput = outputSpec ?? new SampleSpec(SampleFormat.S16Le, 2, 48000);
        if (resolvedOutput.Format != SampleFormat.S16Le || resolvedOutput.Channels != 2 || resolvedOutput.SampleRate == 0)
            throw new ArgumentOutOfRangeException(nameof(outputSpec), "Output spec must be S16Le stereo");

        ChannelIndex = channelIndex;
        InputSpec = inputSpec;
        OutputSpec = resolvedOutput;
        _inputChannels = inputSpec.Channels;
        _inputRate = inputSpec.SampleRate;
        _outputChannels = resolvedOutput.Channels;
        _outputRate = resolvedOutput.SampleRate;
        _inputBytesPerFrame = inputSpec.BytesPerSample * inputSpec.Channels;
        Capacity = AlignToFrame(Math.Max(_inputBytesPerFrame, capacity), _inputBytesPerFrame);
        _ring = new byte[Capacity];
        _gain = initialGain;
        _paused = paused;
    }

    public uint ChannelIndex { get; }
    public SampleSpec InputSpec { get; }
    public SampleSpec OutputSpec { get; }
    public int Capacity { get; }

    public int QueuedInputBytes
    {
        get
        {
            lock (_gate)
                return _queuedBytes;
        }
    }

    public int QueuedOutputEstimateBytes
    {
        get
        {
            lock (_gate)
            {
                int availableFrames = _queuedBytes / _inputBytesPerFrame;
                if (availableFrames <= 0)
                    return 0;

                double remainingInputFrames = Math.Max(0.0, availableFrames - _sourcePosition);
                int estimatedOutputFrames = (int)Math.Ceiling(remainingInputFrames * _outputRate / _inputRate);
                return estimatedOutputFrames * _outputChannels * sizeof(short);
            }
        }
    }

    public bool HasPendingOutput => QueuedOutputEstimateBytes > 0;

    public void SetPaused(bool paused)
    {
        lock (_gate)
            _paused = paused;
    }

    public void SetGain(float gain)
    {
        lock (_gate)
            _gain = gain;
    }

    public int PutData(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return 0;

        lock (_gate)
        {
            int writable = Capacity - _queuedBytes;
            int bytesToWrite = Math.Min(writable, data.Length);
            bytesToWrite -= bytesToWrite % _inputBytesPerFrame;
            if (bytesToWrite <= 0)
                return 0;

            int first = Math.Min(bytesToWrite, Capacity - _writeOffset);
            data.Slice(0, first).CopyTo(_ring.AsSpan(_writeOffset, first));
            int remaining = bytesToWrite - first;
            if (remaining > 0)
                data.Slice(first, remaining).CopyTo(_ring.AsSpan(0, remaining));

            _writeOffset = (_writeOffset + bytesToWrite) % Capacity;
            _queuedBytes += bytesToWrite;
            return bytesToWrite;
        }
    }

    public int MixInto(Span<float> mixBuffer, int frames)
    {
        if (frames <= 0)
            return 0;

        lock (_gate)
        {
            if (_paused)
                return 0;

            int mixedFrames = 0;
            double step = (double)_inputRate / _outputRate;
            for (; mixedFrames < frames; mixedFrames++)
            {
                int availableFrames = _queuedBytes / _inputBytesPerFrame;
                if (availableFrames <= 0)
                    break;

                if (_sourcePosition >= availableFrames)
                    break;

                ReadInterpolatedStereoFrame(_sourcePosition, availableFrames, out float left, out float right);
                int sampleIndex = mixedFrames * _outputChannels;
                mixBuffer[sampleIndex] += left * _gain;
                mixBuffer[sampleIndex + 1] += right * _gain;

                _sourcePosition += step;
                DiscardFullyConsumedFrames();
            }

            return mixedFrames;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _readOffset = 0;
            _writeOffset = 0;
            _queuedBytes = 0;
            _sourcePosition = 0;
            _hasPreviousFrame = false;
            _previousLeft = 0;
            _previousRight = 0;
        }
    }

    private void DiscardFullyConsumedFrames()
    {
        int availableFrames = _queuedBytes / _inputBytesPerFrame;
        int framesToDrop = Math.Min((int)Math.Floor(_sourcePosition), availableFrames);
        if (framesToDrop <= 0)
            return;

        ReadFrameStereo(framesToDrop - 1, out _previousLeft, out _previousRight);
        _hasPreviousFrame = true;

        int bytesToDrop = framesToDrop * _inputBytesPerFrame;
        _readOffset = (_readOffset + bytesToDrop) % Capacity;
        _queuedBytes -= bytesToDrop;
        _sourcePosition -= framesToDrop;

        if (_queuedBytes == 0)
        {
            _readOffset = 0;
            _writeOffset = 0;
            _sourcePosition = 0;
            _hasPreviousFrame = false;
        }
    }

    private void ReadInterpolatedStereoFrame(double position, int availableFrames, out float left, out float right)
    {
        int baseFrame = (int)Math.Floor(position);
        float t = (float)(position - baseFrame);

        ReadSampleWithBoundary(baseFrame - 1, availableFrames, out float p0L, out float p0R);
        ReadSampleWithBoundary(baseFrame, availableFrames, out float p1L, out float p1R);
        ReadSampleWithBoundary(baseFrame + 1, availableFrames, out float p2L, out float p2R);
        ReadSampleWithBoundary(baseFrame + 2, availableFrames, out float p3L, out float p3R);

        left = AudioMixer.CubicInterpolate(p0L, p1L, p2L, p3L, t);
        right = AudioMixer.CubicInterpolate(p0R, p1R, p2R, p3R, t);
    }

    private void ReadSampleWithBoundary(int frameIndex, int availableFrames, out float left, out float right)
    {
        if (availableFrames <= 0)
        {
            left = 0;
            right = 0;
            return;
        }

        if (frameIndex < 0)
        {
            if (_hasPreviousFrame)
            {
                left = _previousLeft;
                right = _previousRight;
                return;
            }

            frameIndex = 0;
        }
        else if (frameIndex >= availableFrames)
        {
            frameIndex = availableFrames - 1;
        }

        ReadFrameStereo(frameIndex, out left, out right);
    }

    private void ReadFrameStereo(int frameIndex, out float left, out float right)
    {
        int byteIndex = (_readOffset + (frameIndex * _inputBytesPerFrame)) % Capacity;
        short sample0 = ReadInt16(byteIndex);
        if (_inputChannels == 1)
        {
            left = sample0 / 32768.0f;
            right = left;
            return;
        }

        short sample1 = ReadInt16((byteIndex + sizeof(short)) % Capacity);
        left = sample0 / 32768.0f;
        right = sample1 / 32768.0f;
    }

    private short ReadInt16(int byteIndex)
    {
        if (byteIndex <= Capacity - sizeof(short))
            return BinaryPrimitives.ReadInt16LittleEndian(_ring.AsSpan(byteIndex, sizeof(short)));

        Span<byte> temp = stackalloc byte[sizeof(short)];
        temp[0] = _ring[byteIndex];
        temp[1] = _ring[(byteIndex + 1) % Capacity];
        return BinaryPrimitives.ReadInt16LittleEndian(temp);
    }

    private static int AlignToFrame(int bytes, int frameSize)
    {
        int remainder = bytes % frameSize;
        return remainder == 0 ? bytes : bytes + (frameSize - remainder);
    }
}
