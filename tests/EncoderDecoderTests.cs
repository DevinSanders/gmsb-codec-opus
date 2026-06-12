using System;
using System.Collections.Generic;
using FluentAssertions;
using SoundBoard.PluginApi;
using Xunit;

namespace OpusCodecPlugin.Tests;

/// <summary>
/// Round-trips the borrowable frame encoder/decoder surface that bridge
/// plugins lean on — encode known PCM, decode it back, confirm the energy
/// survives the (lossy) trip.
/// </summary>
public class EncoderDecoderTests
{
    private readonly OpusCodecPlugin _plugin = new();

    [Theory]
    [InlineData(8000, 2, 64000)]    // rate other than 48 kHz
    [InlineData(48000, 3, 64000)]   // too many channels
    [InlineData(48000, 2, 1000)]    // bitrate below Opus's floor
    public void CreateEncoder_rejects_unsupported_configs(int rate, int channels, int bitrate)
    {
        _plugin.CreateEncoder(rate, channels, bitrate).Should().BeNull();
    }

    [Fact]
    public void Encoder_rejects_wrong_frame_length()
    {
        using IAudioFrameEncoder encoder = _plugin.CreateEncoder(48000, 2, 64000)!;
        var wrongSize = new float[encoder.FrameSamples * encoder.Channels + 1];

        var act = () => encoder.Encode(wrongSize, new byte[4000]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Encode_decode_round_trip_preserves_energy()
    {
        using IAudioFrameEncoder encoder = _plugin.CreateEncoder(48000, 2, 64000)!;
        using IAudioFrameDecoder decoder = _plugin.CreateDecoder()!;

        encoder.FrameSamples.Should().Be(960);
        encoder.Channels.Should().Be(2);
        decoder.SampleRate.Should().Be(48000);
        decoder.Channels.Should().Be(2);

        const float amplitude = 0.5f;
        const int frames = 25;
        int frameInterleaved = encoder.FrameSamples * encoder.Channels;

        var packet = new byte[4000];
        var decoded = new float[decoder.MaxFrameSamples * decoder.Channels];
        var steadyState = new List<float>();

        // Continuous sine across frames so the encoder's predictor sees a
        // coherent signal rather than 25 independent transients.
        double step = 2.0 * Math.PI * 440.0 / 48000.0;
        long sampleIndex = 0;

        for (int f = 0; f < frames; f++)
        {
            var pcm = new float[frameInterleaved];
            for (int n = 0; n < encoder.FrameSamples; n++)
            {
                float s = amplitude * (float)Math.Sin(step * sampleIndex++);
                pcm[(n * encoder.Channels) + 0] = s;
                pcm[(n * encoder.Channels) + 1] = s;
            }

            int packetLen = encoder.Encode(pcm, packet);
            packetLen.Should().BeGreaterThan(0).And.BeLessThan(packet.Length);

            int samplesPerChannel = decoder.Decode(packet.AsSpan(0, packetLen), decoded);
            samplesPerChannel.Should().Be(encoder.FrameSamples);

            // Drop the first few frames — Opus's ~6.5 ms lookahead means the
            // leading output is warmup/transient, not the steady tone.
            if (f >= 5)
                steadyState.AddRange(decoded.AsSpan(0, samplesPerChannel * decoder.Channels).ToArray());
        }

        double expectedRms = amplitude / Math.Sqrt(2.0);   // RMS of a sine
        double actualRms = TestAudio.Rms(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(steadyState));

        // Lossy, but a sustained tone should come back within a comfortable
        // band of its input energy.
        actualRms.Should().BeInRange(expectedRms * 0.7, expectedRms * 1.3);
    }
}
