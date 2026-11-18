using Podish.Pulse.Protocol;

namespace Podish.Pulse.Audio;

public static class PulseAudioFormats
{
    public static bool IsSupported(SampleSpec sampleSpec)
    {
        return sampleSpec.Format is SampleFormat.U8 or SampleFormat.S16Le &&
               (sampleSpec.Channels == 1 || sampleSpec.Channels == 2) &&
               sampleSpec.SampleRate > 0;
    }
}
