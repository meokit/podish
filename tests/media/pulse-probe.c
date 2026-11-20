#include <pulse/simple.h>
#include <pulse/error.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

int main(void) {
    const pa_sample_spec spec = { PA_SAMPLE_S16LE, 48000, 2 };
    int error;
    pa_simple* stream = pa_simple_new(NULL, "podish-media-smoke", PA_STREAM_PLAYBACK,
                                     NULL, "probe", &spec, NULL, NULL, &error);
    if (!stream) {
        fprintf(stderr, "open PulseAudio playback stream: %s\n", pa_strerror(error));
        return 1;
    }
    /* A short, quiet 440 Hz tone with a linear fade at both ends. */
    int16_t pcm[4800][2];
    for (int i = 0; i < 4800; ++i) {
        int phase = i * 440 % 48000;
        int triangle = phase < 24000 ? phase - 12000 : 36000 - phase;
        int fade = i < 2400 ? i : 4799 - i;
        pcm[i][0] = pcm[i][1] = (int16_t)(triangle * fade / 57600);
    }
    if (pa_simple_write(stream, pcm, sizeof(pcm), &error) < 0 || pa_simple_drain(stream, &error) < 0) {
        fprintf(stderr, "write/drain PulseAudio playback stream: %s\n", pa_strerror(error));
        pa_simple_free(stream);
        return 1;
    }
    pa_simple_free(stream);
    puts("PulseAudio smoke passed");
    return 0;
}
