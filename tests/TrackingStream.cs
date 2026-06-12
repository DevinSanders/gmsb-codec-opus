using System.IO;

namespace OpusCodecPlugin.Tests;

/// <summary>
/// A <see cref="MemoryStream"/> that records whether it was disposed, so a
/// test can assert the <c>CreateStream(Stream, hint)</c> ownership contract:
/// disposing the returned <see cref="NAudio.Wave.WaveStream"/> must dispose
/// the input stream the codec was handed.
/// </summary>
internal sealed class TrackingStream : MemoryStream
{
    public bool Disposed { get; private set; }

    public TrackingStream(byte[] buffer) : base(buffer, writable: false) { }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
