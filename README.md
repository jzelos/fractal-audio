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
idle state.

| Key | Action |
| --- | --- |
| **F11** / **Alt+Enter** | Borderless fullscreen on/off (**Esc** leaves it) |
| **M** | Move to the next display (stays fullscreen if it was) |
| **F** | Motion trails (feedback) on/off |
| **D** / **F1** | Diagnostics overlay on/off |
| **T** / **Space** | Tap tempo |

The window can be resized and maximised freely. Command-line options (after `dotnet run --`):
`--fullscreen` starts in fullscreen, and `--display N` starts on display N — the connected
displays are numbered from 1 in the console output at startup.

Shaders in `shaders/` are copied to the output folder and hot-reloaded: edit the copy under
`bin/Debug/net8.0/shaders/` while the app runs and it recompiles live. A shader with errors is
reported to the console and the last working version keeps rendering.

## Tempo diagnostics

The on-screen overlay (toggle with **D**) shows the tempo estimate, lock state and confidence;
tap **T** or **Space** along with the music to compare your tempo with the tracker's.

`tools/TempoReplay` scores the tracker's tempo against a committed test set of 19 tracks with
known BPMs — the two onset signals per frame the tracker reads, not audio — so tracker changes
can be checked without replaying any music:

```powershell
dotnet run --project tools/TempoReplay -- recordings/signals
```

`--timeline` shows each track's tempo over time, `--trace=FROM-TO` the tracker's decision steps
within that range (seconds); the tool exits non-zero if any track scores below 90%. To record
your own tracks (full spectrum dumps, which stay local) and add them to the set, see
[recordings/README.md](recordings/README.md).

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
| `recordings/` | Tempo test set: committed onset signals (`signals/`), local spectrum dumps (see its README) |

## Docs

- `IDEAS.md` — feature backlog, ordered by value-to-effort, with playtest feedback.
- `PLAN.md` — the current implementation plan and its change log.

## Stack

C# / .NET 8, [Sayers.SDL2.Core](https://www.nuget.org/packages/Sayers.SDL2.Core) (SDL2),
[Silk.NET.OpenGL](https://github.com/dotnet/Silk.NET), [NAudio](https://github.com/naudio/NAudio).

## License

MIT — see [LICENSE](LICENSE).
