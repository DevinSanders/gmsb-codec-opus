# gmsb-codec-opus

Opus playback codec plugin for [Game Master Sound Board](https://github.com/DevinSanders/game-master-soundboard).

Adds `.opus` (Ogg-encapsulated Opus) playback via the pure-managed
[Concentus](https://github.com/lostromb/concentus) decoder. No native
binaries to ship — the plugin folder is just managed DLLs.

It also exposes an Opus **encoder and decoder** through the SDK's
`IAudioCodecPlugin` encoder surface (`SupportsEncoding = true`,
`CreateEncoder` / `CreateDecoder`), so audio-bridge plugins (e.g.
`gmsb-bridge-discord`) can borrow a pure-managed Opus codec via
`IPluginContext.CodecRegistry` instead of bundling their own.

## Install

**Paid plugin.** The source is open here for reference, but the pre-built
binary is distributed pay-what-you-want on itch.io:

**→ https://dsand64.itch.io/gmsb-codec-opus**

Download the `.zip` from that page and drop it onto **Settings → Plugin
Manager** in Game Master Sound Board. Restart when prompted, then enable it under **Settings → Plugins**.

## Build

Requires .NET 10 SDK. `SoundBoard.PluginApi` is restored from NuGet, so no sibling checkout is required. To build against a local, unreleased SDK you can optionally check out the main `Game Master Sound Board` repo beside this one — optional layout:

```
D:\My Projects\
├── Game Master Sound Board\
└── gmsb-codec-opus\         ← this repo
```

Then:

```powershell
dotnet build src/OpusCodecPlugin.csproj
pwsh scripts/package.ps1
# → dist/github.DevinSanders-codec.opus-1.0.0.zip
```

Released versions are built by CI from a `v*` git tag — the tag's SemVer (stripped of the leading `v`) is stamped into the manifest, the assembly metadata, and the zip filename, so the release artifact's version always matches its tag.

## Plugin manifest

| Field     | Value                       |
|-----------|-----------------------------|
| publisher | `github.DevinSanders`       |
| id        | `codec.opus`                |
| entryDll  | `OpusCodecPlugin.dll`       |
| isTheme   | `false`                     |

## License

Released under the [MIT License](LICENSE).

Third-party components used by this plugin:

- Concentus (MIT) for pure-managed Opus encoding and decoding.
- Concentus.OggFile (MIT) for Ogg-Opus container parsing.
