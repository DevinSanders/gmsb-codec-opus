using System;
using System.IO;
using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;

namespace OpusCodecPlugin.Tests;

/// <summary>
/// Builds tiny Ogg-Opus clips in memory so the decode tests don't need a
/// committed binary fixture. A synthesized sine tone has no licensing
/// strings attached and produces a byte-for-byte deterministic file, which
/// keeps the tests hermetic (see <c>tests/fixtures/README.txt</c>).
/// </summary>
internal static class TestAudio
{
    public const int SampleRate = 48000;

    /// <summary>Encode <paramref name="seconds"/> of a sine tone to a
    /// complete Ogg-Opus byte stream (OpusHead + OpusTags + audio pages),
    /// exactly what a real <c>.opus</c> file contains.</summary>
    public static byte[] CreateOpusClip(int channels = 2, double seconds = 0.5, double frequencyHz = 440.0)
    {
        int samplesPerChannel = (int)(SampleRate * seconds);
        float[] pcm = SineInterleaved(channels, samplesPerChannel, frequencyHz);

        using var ms = new MemoryStream();
        IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(SampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        // leaveOpen: true so we can grab the buffer after the writer flushes.
        var writer = new OpusOggWriteStream(encoder, ms, null, SampleRate, 5, leaveOpen: true);
        // Force fine pagination. The writer's default packs the whole clip
        // into one Ogg page, which leaves nothing to seek between — real
        // .opus files page roughly every ~1 s, so emulate that to keep the
        // seek test meaningful.
        writer.MaxAudioLengthPerPage = TimeSpan.FromMilliseconds(200);
        writer.WriteSamples(pcm, 0, pcm.Length);
        writer.Finish();
        return ms.ToArray();
    }

    /// <summary>Interleaved sine PCM in [-1, 1]; every channel carries the
    /// same tone so stereo round-trips can compare against one expected
    /// per-channel RMS.</summary>
    public static float[] SineInterleaved(int channels, int samplesPerChannel, double frequencyHz, float amplitude = 0.5f)
    {
        var pcm = new float[samplesPerChannel * channels];
        double step = 2.0 * Math.PI * frequencyHz / SampleRate;
        for (int n = 0; n < samplesPerChannel; n++)
        {
            float s = amplitude * (float)Math.Sin(step * n);
            for (int c = 0; c < channels; c++)
                pcm[(n * channels) + c] = s;
        }
        return pcm;
    }

    /// <summary>Root-mean-square of an interleaved buffer across all
    /// samples — a cheap energy proxy for lossy-codec comparisons.</summary>
    public static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return 0;
        double sumSq = 0;
        for (int i = 0; i < samples.Length; i++)
            sumSq += (double)samples[i] * samples[i];
        return Math.Sqrt(sumSq / samples.Length);
    }
}
