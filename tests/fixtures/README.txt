Test fixtures
=============

There are no committed binary audio fixtures in this directory.

The decode tests synthesize their Ogg-Opus input at runtime (a 440 Hz sine
tone encoded with Concentus.OggFile's OpusOggWriteStream) — see
tests/TestAudio.cs. A synthesized tone is:

  * deterministic   — the same bytes every run, so tests stay hermetic;
  * license-free    — it's a generated sine wave, not third-party audio,
                      so nothing here needs an attribution / license note;
  * self-validating — it's produced by the same Concentus stack the plugin
                      decodes with, so a valid encode proves a valid decode
                      target.

If you ever need a real-world fixture (e.g. to reproduce a decoder bug
against a specific encoder's output), drop the .opus file here and note its
source + license in this file.
