# SDL_Demo — audio-reactive fractal visualizer

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

Shaders in `shaders/` are copied to the output folder and hot-reloaded: edit the copy under
`bin/Debug/net8.0/shaders/` while the app runs and it recompiles live. A shader with errors is
reported to the console and the last working version keeps rendering.

## Layout

| Path | What it does |
| --- | --- |
| `App.cs`, `Window.cs`, `Clock.cs` | SDL window + GL context, main loop, fixed-step timing |
| `Audio/` | Loopback capture, FFT analysis, envelope followers, onset/beat detectors, `AudioFrame` snapshot bus |
| `Graphics/` | GL loader, shader program + hot reload, fullscreen quad, palette atlas |
| `Mapping/` | `AudioToFractalMapper` — the single place audio features become shader uniforms; `JuliaBounds` keeps the shape on screen |
| `shaders/` | `quad.vert`, `fractal.frag` (Julia escape-time with smooth colouring) |

## Docs

- `IDEAS.md` — feature backlog, ordered by value-to-effort, with playtest feedback.
- `PLAN.md` — the current implementation plan and its change log.

## Stack

C# / .NET 8, [Sayers.SDL2.Core](https://www.nuget.org/packages/Sayers.SDL2.Core) (SDL2),
[Silk.NET.OpenGL](https://github.com/dotnet/Silk.NET), [NAudio](https://github.com/naudio/NAudio).
