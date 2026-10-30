# fractal-audio — audio-reactive fractal visualizer

A real-time Julia-set visualizer for Windows that reacts to whatever the system is playing.
It captures system audio output via WASAPI loopback, analyses it (FFT, band energies, onset and
beat detection), and drives a GPU fractal shader from those features.

## Requirements

- Windows (WASAPI loopback capture)
- .NET 8 SDK
- A GPU/driver supporting OpenGL 3.3 core

## Build and run

```sh
dotnet run
```

Play some music in any app; the visuals follow it. With no audio playing the visual settles to an
idle state. Press **F** to turn the motion trails (feedback) on or off.

Shaders in `shaders/` are copied to the output folder and hot-reloaded: edit the copy under
`bin/Debug/net8.0/shaders/` while the app runs and it recompiles live. A shader with errors is
reported to the console and the last working version keeps rendering.

## Tempo diagnostics

The on-screen overlay (toggle with **D**) shows the tempo estimate, lock state and confidence;
tap **T** or **Space** along with the music to compare your tempo with the tracker's.

To record what the beat tracker sees, set `FRACTAL_AUDIO_DUMP` to a file path before running:

```powershell
$env:FRACTAL_AUDIO_DUMP = "recordings\catch.csv"; dotnet run --project FractalAudio.csproj
```

`tools/TempoReplay` feeds such dumps through the app's own beat pipeline and scores the tempo
against known BPMs, so tracker changes can be checked against recordings without replaying
the music:

```powershell
dotnet run --project tools/TempoReplay -- recordings\catch.csv=130 recordings\disco.csv=97 --timeline
```

Keep dumps in `recordings/` (git-ignored). Its README lists the known-BPM test tracks, the file
names to record them under, and a command that scores them all.

`--trace=FROM-TO` prints each estimate's decision steps within that time range (seconds). The
tool exits non-zero if any dump scores below 90%.

## Layout

| Path | What it does |
| --- | --- |
| `App.cs`, `Window.cs`, `Clock.cs` | SDL window + GL context, main loop, fixed-step timing |
| `Audio/` | Loopback capture, FFT analysis, envelope followers, onset/beat detectors, `AudioFrame` snapshot bus |
| `Graphics/` | GL loader, shader program + hot reload, fullscreen quad, palette atlas, feedback (trail) buffer |
| `Mapping/` | `AudioToFractalMapper` — the single place audio features become shader uniforms; `JuliaBounds` keeps the shape on screen |
| `shaders/` | `quad.vert`, `fractal.frag` (Julia escape-time with smooth colouring), `feedback.frag` (motion trails), `present.frag` |
| `Diagnostics/` | On-screen overlay text and tap tempo |
| `tools/TempoReplay/` | Replays tempo dumps through the beat pipeline (see Tempo diagnostics) |
| `recordings/` | Git-ignored tempo dumps of the known-BPM test tracks (see its README) |

## Docs

- `IDEAS.md` — feature backlog, ordered by value-to-effort, with playtest feedback.
- `PLAN.md` — the current implementation plan and its change log.

## Stack

C# / .NET 8, [Sayers.SDL2.Core](https://www.nuget.org/packages/Sayers.SDL2.Core) (SDL2),
[Silk.NET.OpenGL](https://github.com/dotnet/Silk.NET), [NAudio](https://github.com/naudio/NAudio).
