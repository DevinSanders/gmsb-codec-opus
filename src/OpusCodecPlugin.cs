using System;
using System.Collections.Generic;
using System.IO;
using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using NAudio.Wave;
using SoundBoard.PluginApi;

namespace OpusCodecPlugin;

/// <summary>
/// <see cref="IAudioCodecPlugin"/> that adds Opus (<c>.opus</c>) playback to
/// Game Master Sound Board via the pure-managed
/// <a href="https://github.com/lostromb/concentus">Concentus</a> decoder
/// paired with <c>Concentus.OggFile</c> for the Ogg-Opus container.
/// Pure-managed — no native libraries — so the plugin folder only needs
/// this DLL and <c>Concentus.dll</c> + <c>Concentus.Oggfile.dll</c>
/// alongside.
///
/// <para><b>Inter-plugin dispatch.</b> This codec implements the
/// <see cref="IAudioCodecPlugin.CreateStream(System.IO.Stream, string)"/>
/// overload, so transport plugins (e.g. <c>codec.webstream</c>) can hand
/// a pre-opened Ogg-Opus stream here for decode. MIME type registered:
/// <c>"audio/opus"</c>. Note that <c>"audio/ogg"</c> is also valid for
/// Ogg-Opus, but that MIME is shared with Vorbis — declaring it would
/// race the OGG codec for routing; URL-extension fallback handles that
/// case.</para>
/// </summary>
public sealed class OpusCodecPlugin : IAudioCodecPlugin
{
    public string Id => "codec.opus";
    public string Name => "Opus Codec";
    public string Description => "Adds .opus playback support via the Concentus managed Opus decoder.";
    public string Version => PluginVersion.OfAssembly(typeof(OpusCodecPlugin));
    public string Author => "Devin Sanders";

    public IEnumerable<string> SupportedPatterns => new[] { ".opus" };

    public IEnumerable<string> SupportedContentTypes => new[]
    {
        "audio/opus",
        // Intentionally NOT claiming "audio/ogg" — that's claimed by
        // codec.ogg (Vorbis) and the registry's first-claim-wins policy
        // would race ambiguously. Servers that emit Opus content with a
        // bare "audio/ogg" Content-Type are unambiguously identifiable
        // only via the URL extension or the OpusHead probe; the
        // webstream plugin falls back to extension matching when MIME
        // resolution misses.
    };

    public bool SupportsStreamInput => true;

    public WaveStream CreateStream(string source) => new OpusOggWaveStream(source);

    /// <summary>Decode an already-open Ogg-Opus <see cref="Stream"/>.
    /// Ownership of <paramref name="source"/> transfers — the returned
    /// <see cref="WaveStream"/>'s <c>Dispose</c> closes the input
    /// Stream. <paramref name="formatHint"/> is advisory; Concentus
    /// validates the Ogg page framing itself.</summary>
    public WaveStream CreateStream(Stream source, string formatHint)
        => new OpusOggWaveStream(source);

    // ── Bridge-borrow support ─────────────────────────────────────────
    //
    // Bridge plugins (gmsb-bridge-discord, gmsb-bridge-zoom, …) need raw
    // Opus encode/decode for their real-time voice path. Rather than each
    // bridge bundling its own copy of Concentus, they ask the host's
    // codec registry for the codec.opus plugin and borrow these factories.
    // The bridge installs this plugin as a hard dependency in its docs;
    // if it's not installed, the bridge surfaces a clear error rather
    // than crashing.

    public bool SupportsEncoding => true;

    /// <summary>Create a streaming Opus encoder. 20 ms frames at 48 kHz
    /// (960 samples per channel). Stereo and mono only — Opus supports
    /// channel-mapping for up to 8 channels but bridges only ever ask
    /// for 1 or 2 here. <paramref name="bitrate"/> is in bits per second;
    /// Opus's reasonable range is 6000..510000, with 64000 being the
    /// usual "VoIP-grade stereo" setting.</summary>
    public IAudioFrameEncoder? CreateEncoder(int sampleRate, int channels, int bitrate)
        => OpusFrameEncoder.TryCreate(sampleRate, channels, bitrate);

    /// <summary>Create a streaming Opus decoder for packet-at-a-time
    /// decode. Always emits 48 kHz stereo float (the host mixer's native
    /// format); upmix and resample happen inside the decoder if the
    /// remote sent mono / a different rate.</summary>
    public IAudioFrameDecoder? CreateDecoder()
        => new OpusFrameDecoder();

    public void Initialize(IPluginContext context) { }
    public void Shutdown() { }
}

/// <summary>
/// <see cref="IAudioFrameEncoder"/> wrapping Concentus's
/// <c>IOpusEncoder</c>. Stateful across frames — Opus's predictor /
/// lookahead benefit from continuous input, so the bridge keeps one
/// instance per outbound stream and disposes on disconnect.
///
/// <para><b>PCM contract.</b> 20 ms frames at 48 kHz (960 samples /
/// channel), interleaved float in <c>[-1, 1]</c>. Input length must equal
/// <c>FrameSamples * Channels</c>. Output goes into the caller-owned
/// destination Span; <see cref="Encode"/> returns the actual packet
/// length, typically 80–400 bytes for VoIP-grade stereo.</para>
/// </summary>
internal sealed class OpusFrameEncoder : IAudioFrameEncoder
{
    // 20 ms @ 48 kHz. Opus also supports 2.5/5/10/40/60 ms; bridges that
    // need a different frame size can negotiate via a future overload.
    public const int FrameSamplesPerChannel = 960;

    private readonly IOpusEncoder _encoder;
    private readonly short[] _shortBuffer;

    public int FrameSamples => FrameSamplesPerChannel;
    public int Channels { get; }
    public int SampleRate { get; }

    private OpusFrameEncoder(IOpusEncoder encoder, int sampleRate, int channels)
    {
        _encoder = encoder;
        SampleRate = sampleRate;
        Channels = channels;
        _shortBuffer = new short[FrameSamplesPerChannel * channels];
    }

    public static OpusFrameEncoder? TryCreate(int sampleRate, int channels, int bitrate)
    {
        // Opus's native sample rates are 8/12/16/24/48 kHz. 48 kHz is the
        // host's mixer rate; anything else means the caller is bringing
        // input from a different pipeline (don't expect that today).
        if (sampleRate != 48000) return null;
        if (channels is < 1 or > 2) return null;
        if (bitrate < 6000 || bitrate > 510000) return null;

        var encoder = OpusCodecFactory.CreateEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = bitrate;
        return new OpusFrameEncoder(encoder, sampleRate, channels);
    }

    public int Encode(ReadOnlySpan<float> pcm, Span<byte> packet)
    {
        int expectedSamples = FrameSamples * Channels;
        if (pcm.Length != expectedSamples)
            throw new ArgumentException(
                $"OpusFrameEncoder expects exactly {expectedSamples} interleaved samples per Encode call (got {pcm.Length}).",
                nameof(pcm));

        // Concentus 2.x's IOpusEncoder operates on ReadOnlySpan<short>.
        // Convert ±1.0 float → ±32767 short with clamp. Inlined to avoid
        // an extra delegate call per sample — this loop runs at ~50 Hz
        // (one per 20 ms frame) so it's not hot, but tight code is good
        // hygiene either way.
        for (int i = 0; i < expectedSamples; i++)
        {
            float s = pcm[i];
            if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
            _shortBuffer[i] = (short)(s * 32767f);
        }

        return _encoder.Encode(_shortBuffer, FrameSamples, packet, packet.Length);
    }

    public void Dispose()
    {
        // IOpusEncoder in Concentus 2.x is pure-managed; no native handles
        // to release. Method exists to satisfy IDisposable so future
        // unmanaged backends (libopus P/Invoke) plug in cleanly.
    }
}

/// <summary>
/// <see cref="IAudioFrameDecoder"/> wrapping Concentus's
/// <c>IOpusDecoder</c>. Stateful — the decoder maintains internal state
/// across packets for Opus's predictor; bridges keep one instance per
/// inbound stream.
///
/// <para>Decodes whatever the remote sent (any legal Opus channel /
/// frame size combo) and emits 48 kHz stereo IEEE float interleaved.
/// Mono input is duplicated to both channels.</para>
/// </summary>
internal sealed class OpusFrameDecoder : IAudioFrameDecoder
{
    // Opus's maximum frame is 120 ms = 5760 samples @ 48 kHz.
    public const int MaxFrameSamplesPerChannel = 5760;

    private readonly IOpusDecoder _decoder;
    private readonly short[] _shortBuffer;

    public int Channels => 2;
    public int SampleRate => 48000;
    public int MaxFrameSamples => MaxFrameSamplesPerChannel;

    public OpusFrameDecoder()
    {
        // Decoder is constructed for the OUTPUT format (what we hand back
        // to the mixer) — stereo at 48 kHz. Opus's container packets
        // self-describe their own channel count and the decoder upmixes
        // mono to stereo automatically.
        _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
        _shortBuffer = new short[MaxFrameSamplesPerChannel * Channels];
    }

    public int Decode(ReadOnlySpan<byte> packet, Span<float> pcm)
    {
        // Concentus's IOpusDecoder.Decode returns the per-channel sample
        // count. Passing 0 for frameSize lets the decoder figure it out
        // from the packet TOC byte — what bridges want for "decode
        // whatever they sent."
        int samples = _decoder.Decode(packet, _shortBuffer, MaxFrameSamplesPerChannel, decode_fec: false);
        int total = samples * Channels;

        // ±32767 short → ±1.0 float.
        for (int i = 0; i < total; i++)
        {
            pcm[i] = _shortBuffer[i] / 32768f;
        }

        return samples;
    }

    public void Dispose() { /* pure-managed; GC handles it */ }
}

/// <summary>
/// Bridges <see cref="OpusOggReadStream"/> (which emits 16-bit interleaved
/// PCM via <c>DecodeNextPacket</c>) to <see cref="WaveStream"/> (which
/// <see cref="IAudioCodecPlugin.CreateStream"/> must return — the host
/// wraps it in <c>GenericSeekableSampleProvider</c>). Output format is
/// IEEE float at Opus's native 48 kHz with the file's channel count.
///
/// <para>Two constructors: file path (opens + owns the file) and
/// <see cref="Stream"/> (takes ownership of the caller's stream). Both
/// are seekable. Concentus.OggFile's Ogg page scanner seeks the input —
/// it builds the granule/page index up front and seeks again on every
/// scrub — so a forward-only stream decodes to silence. The Stream path
/// therefore buffers the caller's bytes into a seekable
/// <see cref="MemoryStream"/> before decoding.</para>
///
/// <para>Length and Position are derived from
/// <c>OpusOggReadStream.TotalTime</c> / <c>CurrentTime</c> against the
/// wave format's average bytes-per-second so the host's scrub slider
/// works.</para>
/// </summary>
internal sealed class OpusOggWaveStream : WaveStream
{
    // Largest legal Opus frame is 120 ms at 48 kHz = 5760 samples / channel.
    // Allow up to 8 channels' worth of interleaved samples per packet to be
    // generous; in practice Opus files are mono or stereo.
    private const int MaxSamplesPerPacket = 5760 * 8;

    // OpusHead is in the first Ogg page, well under 4 KB.
    private const int OpusHeadProbeSize = 4096;

    private readonly Stream _stream;        // owned; the seekable stream the decoder reads (file, or in-memory copy)
    private readonly Stream? _ownedSource;  // owned; the caller's original Stream in Stream mode (null in file mode)
    private readonly OpusOggReadStream _ogg;
    private readonly float[] _floatScratch;
    private short[]? _pendingShorts;
    private int _pendingOffsetSamples;
    private int _pendingLengthSamples;

    public override WaveFormat WaveFormat { get; }

    /// <summary>File-path constructor. Always seekable.</summary>
    public OpusOggWaveStream(string path)
    {
        // OpusOggReadStream does not expose the channel count from the
        // OpusHead packet (the OpusHeader type is internal), so sniff
        // ourselves before constructing the decoder.
        int channels = SniffChannelCountFromFile(path);

        _stream = File.OpenRead(path);

        // 48 kHz is Opus's native internal rate — no resampling needed.
        IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(48000, channels);
        _ogg = new OpusOggReadStream(decoder, _stream);

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, channels);
        _floatScratch = new float[MaxSamplesPerPacket];
    }

    /// <summary>Stream constructor for inter-plugin dispatch. Takes
    /// ownership of <paramref name="source"/> — <see cref="Dispose(bool)"/>
    /// disposes both it and the in-memory copy made here.
    ///
    /// <para>Concentus.OggFile needs a seekable stream (its Ogg page
    /// scanner seeks); a forward-only stream decodes to silence. So we
    /// copy the caller's bytes into a seekable <see cref="MemoryStream"/>
    /// and decode from that, which also makes this path scrub-able. The
    /// copy is bounded — transport plugins hand over a finite,
    /// already-buffered resource (a downloaded <c>.opus</c> blob), not an
    /// endless live feed (Concentus can't decode those anyway).</para></summary>
    public OpusOggWaveStream(Stream source)
    {
        _ownedSource = source;

        // Buffer into a seekable stream up front. CopyTo leaves position at
        // the end; rewind before the decoder reads.
        var buffered = new MemoryStream();
        source.CopyTo(buffered);
        _stream = buffered;

        // Sniff OpusHead off the in-memory buffer (GetBuffer doesn't move
        // the position). Then rewind so OpusOggReadStream sees byte 0.
        int channels = SniffChannelCountFromBytes(buffered.GetBuffer(), (int)buffered.Length);
        buffered.Position = 0;

        IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(48000, channels);
        _ogg = new OpusOggReadStream(decoder, _stream);

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, channels);
        _floatScratch = new float[MaxSamplesPerPacket];
    }

    /// <summary>Delegates to the Opus reader. Both constructors hand it a
    /// seekable stream (a file, or the in-memory copy), so this is true in
    /// practice — but reading it from <c>_ogg</c> keeps the WaveStream
    /// honest if that ever changes.</summary>
    public override bool CanSeek => _ogg.CanSeek;

    public override long Length =>
        (long)(_ogg.TotalTime.TotalSeconds * WaveFormat.AverageBytesPerSecond);

    public override long Position
    {
        get => (long)(_ogg.CurrentTime.TotalSeconds * WaveFormat.AverageBytesPerSecond);
        set
        {
            if (!_ogg.CanSeek) return;
            var seconds = value / (double)WaveFormat.AverageBytesPerSecond;
            if (seconds < 0) seconds = 0;
            _ogg.SeekTo(TimeSpan.FromSeconds(seconds));
            // Drop any buffered samples from before the seek.
            _pendingShorts = null;
            _pendingOffsetSamples = 0;
            _pendingLengthSamples = 0;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Host expects IEEE float frames at the rate/channels we advertised.
        // Concentus emits interleaved 16-bit PCM; convert short → float
        // (range ±1.0) and Buffer.BlockCopy into the caller's byte buffer.
        int floatsRequested = count / sizeof(float);
        if (floatsRequested <= 0) return 0;

        int floatsProduced = 0;
        while (floatsProduced < floatsRequested)
        {
            // Drain anything left from the last decoded packet first.
            if (_pendingShorts != null && _pendingOffsetSamples < _pendingLengthSamples)
            {
                int floatsRemaining = floatsRequested - floatsProduced;
                int samplesAvailable = _pendingLengthSamples - _pendingOffsetSamples;
                int samplesToCopy = Math.Min(floatsRemaining, samplesAvailable);
                int scratchLimit = Math.Min(samplesToCopy, _floatScratch.Length);

                for (int i = 0; i < scratchLimit; i++)
                {
                    _floatScratch[i] = _pendingShorts[_pendingOffsetSamples + i] / 32768f;
                }

                Buffer.BlockCopy(
                    _floatScratch, 0,
                    buffer, offset + (floatsProduced * sizeof(float)),
                    scratchLimit * sizeof(float));

                _pendingOffsetSamples += scratchLimit;
                floatsProduced += scratchLimit;
                continue;
            }

            // Packet exhausted — pull the next one.
            if (!_ogg.HasNextPacket) break;

            short[]? next = _ogg.DecodeNextPacket();
            // DecodeNextPacket can return null mid-stream (skipped headers,
            // decode errors). HasNextPacket may still be true after — keep
            // looping rather than treating it as EOF.
            if (next == null || next.Length == 0)
            {
                _pendingShorts = null;
                _pendingOffsetSamples = 0;
                _pendingLengthSamples = 0;
                continue;
            }

            _pendingShorts = next;
            _pendingOffsetSamples = 0;
            _pendingLengthSamples = next.Length;
        }

        return floatsProduced * sizeof(float);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // _stream is the file handle (path mode) or the in-memory copy
            // (Stream mode). _ownedSource is the caller's original stream in
            // Stream mode — disposing it here honours the SDK ownership
            // contract. Concentus's IOpusDecoder is managed-only; GC reclaims it.
            _stream.Dispose();
            _ownedSource?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Same as <see cref="SniffChannelCountFromBytes"/> but
    /// opens the file directly. Used by the file-path constructor;
    /// kept separate so the path mode doesn't pay for an extra
    /// allocation when no Stream wrapper is needed.</summary>
    private static int SniffChannelCountFromFile(string path)
    {
        byte[] probe = new byte[OpusHeadProbeSize];
        int read;
        using (var fs = File.OpenRead(path))
            read = fs.Read(probe, 0, OpusHeadProbeSize);
        return SniffChannelCountFromBytes(probe, read);
    }

    /// <summary>Scan <paramref name="probe"/> for the <c>OpusHead</c>
    /// magic and return the channel count from byte 9 of the
    /// OpusHead packet. Falls back to stereo on parse failure rather
    /// than throwing — the decoder will surface a meaningful error
    /// downstream if the bytes are genuinely garbage.</summary>
    private static int SniffChannelCountFromBytes(byte[] probe, int length)
    {
        ReadOnlySpan<byte> magic = "OpusHead"u8;
        int limit = length - magic.Length - 2; // 1 version byte + 1 channels byte after magic
        for (int i = 0; i <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < magic.Length; j++)
            {
                if (probe[i + j] != magic[j]) { match = false; break; }
            }
            if (match)
            {
                // i+0..i+7 = "OpusHead", i+8 = version, i+9 = channel_count
                int channels = probe[i + 9];
                if (channels < 1) channels = 1;
                if (channels > 8) channels = 8;
                return channels;
            }
        }

        // Fall back to stereo — the most common case.
        return 2;
    }
}
