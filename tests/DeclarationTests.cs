using FluentAssertions;
using Xunit;

namespace OpusCodecPlugin.Tests;

/// <summary>
/// Cheap assertions on the plugin's static declarations — the contract the
/// host registry reads before any audio flows.
/// </summary>
public class DeclarationTests
{
    private readonly OpusCodecPlugin _plugin = new();

    [Fact]
    public void Identity_is_stable()
    {
        _plugin.Id.Should().Be("codec.opus");
        _plugin.Version.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Claims_the_opus_extension()
    {
        _plugin.SupportedPatterns.Should().Contain(".opus");
    }

    [Fact]
    public void Advertises_opus_mime_but_not_ogg()
    {
        // audio/ogg is intentionally NOT claimed — codec.ogg (Vorbis) owns
        // it and first-claim-wins routing would race ambiguously.
        _plugin.SupportedContentTypes.Should().Contain("audio/opus");
        _plugin.SupportedContentTypes.Should().NotContain("audio/ogg");
    }

    [Fact]
    public void Opts_into_stream_input_and_encoding()
    {
        _plugin.SupportsStreamInput.Should().BeTrue();
        _plugin.SupportsEncoding.Should().BeTrue();
    }
}
