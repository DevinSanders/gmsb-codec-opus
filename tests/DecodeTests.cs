using System;
using System.IO;
using FluentAssertions;
using NAudio.Wave;
using Xunit;

namespace OpusCodecPlugin.Tests;

/// <summary>
/// Exercises the two <c>CreateStream</c> overloads end to end against a
/// synthesized Ogg-Opus clip.
/// </summary>
public class DecodeTests
{
    private readonly OpusCodecPlugin _plugin = new();

    private static int ReadAll(WaveStream stream)
    {
        // Pull until EOF the way the host's mixer does, accumulating the
        // total decoded byte count.
        var buffer = new byte[16384];
        int total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            total += read;
        return total;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void File_decodes_to_ieee_float_at_48k(int channels)
    {
        byte[] clip = TestAudio.CreateOpusClip(channels, seconds: 0.5);
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.opus");
        File.WriteAllBytes(path, clip);

        try
        {
            using WaveStream stream = _plugin.CreateStream(path);

            stream.WaveFormat.Encoding.Should().Be(WaveFormatEncoding.IeeeFloat);
            stream.WaveFormat.SampleRate.Should().Be(48000);
            stream.WaveFormat.Channels.Should().Be(channels);
            stream.CanSeek.Should().BeTrue("a local file is seekable");

            int bytesDecoded = ReadAll(stream);
            bytesDecoded.Should().BeGreaterThan(0);

            // ~0.5 s at 48 kHz * channels * 4 bytes/float. Opus pre-skip and
            // page padding fuzz the exact count, so allow a generous band
            // rather than asserting an exact length.
            int bytesPerSecond = 48000 * channels * sizeof(float);
            bytesDecoded.Should().BeInRange((int)(bytesPerSecond * 0.3), (int)(bytesPerSecond * 0.7));

            stream.Length.Should().BeGreaterThan(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Stream_overload_decodes_and_takes_ownership()
    {
        byte[] clip = TestAudio.CreateOpusClip(channels: 2, seconds: 0.5);
        var input = new TrackingStream(clip);

        WaveStream stream = _plugin.CreateStream(input, ".opus");

        stream.WaveFormat.Encoding.Should().Be(WaveFormatEncoding.IeeeFloat);
        stream.WaveFormat.Channels.Should().Be(2);
        // The Stream path buffers into a seekable MemoryStream (Concentus
        // can't decode a forward-only stream), so it scrubs like a file.
        stream.CanSeek.Should().BeTrue("the inter-plugin stream path decodes from a seekable in-memory copy");

        ReadAll(stream).Should().BeGreaterThan(0);

        // SDK contract: disposing the WaveStream disposes the input stream.
        input.Disposed.Should().BeFalse("ownership transfers but disposal only happens on WaveStream.Dispose");
        stream.Dispose();
        input.Disposed.Should().BeTrue("disposing the WaveStream must dispose the input it was handed");
    }

    [Fact]
    public void Position_round_trips_on_a_seekable_file()
    {
        byte[] clip = TestAudio.CreateOpusClip(channels: 2, seconds: 1.0);
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.opus");
        File.WriteAllBytes(path, clip);

        try
        {
            using WaveStream stream = _plugin.CreateStream(path);
            stream.CanSeek.Should().BeTrue();

            long target = stream.WaveFormat.AverageBytesPerSecond / 2; // ~0.5 s in
            stream.Position = target;

            // Opus seeks to the enclosing Ogg page, not a sample-exact
            // offset, so allow ~0.1 s of slack on the round-trip.
            long slack = stream.WaveFormat.AverageBytesPerSecond / 10;
            stream.Position.Should().BeCloseTo(target, (ulong)slack);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
