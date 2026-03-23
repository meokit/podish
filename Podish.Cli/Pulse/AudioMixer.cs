using System.Buffers.Binary;

namespace Podish.Cli.Pulse;

internal static class AudioMixer
{
    public static float CubicInterpolate(float p0, float p1, float p2, float p3, float t)
    {
        float a = (-0.5f * p0) + (1.5f * p1) - (1.5f * p2) + (0.5f * p3);
        float b = p0 - (2.5f * p1) + (2.0f * p2) - (0.5f * p3);
        float c = (-0.5f * p0) + (0.5f * p2);
        return ((a * t + b) * t + c) * t + p1;
    }

    public static short FloatToS16(float sample)
    {
        float clamped = Math.Clamp(sample, -1.0f, 1.0f);
        if (clamped >= 1.0f)
            return short.MaxValue;
        if (clamped <= -1.0f)
            return short.MinValue;
        return (short)MathF.Round(clamped * short.MaxValue);
    }

    public static void WriteS16LeStereo(Span<byte> destination, ReadOnlySpan<float> mixBuffer, int frames)
    {
        int sampleCount = frames * 2;
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = FloatToS16(mixBuffer[i]);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(i * 2, sizeof(short)), sample);
        }
    }
}
