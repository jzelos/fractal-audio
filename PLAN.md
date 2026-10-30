# Plan

**Task:** Implement Tier 1 ("audio in, fractal out") from IDEAS.md, folding in the Tier 0
unblockers it explicitly depends on.
**Status:** in-review (Phase 9 implemented; awaiting playtest)

## Scope

In scope (Tier 1 goal, from IDEAS.md):
- WASAPI loopback capture on its own thread into a ring buffer.
- Audio feature block: 2048-pt Hann-windowed FFT, 7 log-spaced bands, RMS, peak, spectral
  centroid.
- Asymmetric envelope followers (fast attack / slow release) per band.
- Spectral-flux onset/beat detection with adaptive threshold.
- `AudioFrame` double-buffered snapshot contract between audio and render threads.
- Mandelbrot/Julia escape-time fragment shader with smooth iteration coloring.
- Audio-driven Julia constant `c`.
- Palette LUT system (IQ cosine palettes, phase rotation, beat-triggered switching).
- Continuous-vs-discrete mapping rule.

Folded in as prerequisites (Tier 0 from IDEAS.md — not optional, there is nowhere to plug Tier 1
into the current flat loop without these):
- `AllowUnsafeCode` -> `AllowUnsafeBlocks` csproj fix.
- App skeleton split (`App`/`Window`/`Clock`, fixed-step update, decoupled present).
- Replace `SDL_Renderer` with `SDL_GL_CreateContext` + Silk.NET.OpenGL fullscreen-quad
  fragment-shader pipeline.
- Shader hot-reload from disk with last-good fallback on compile error.

Out of scope (explicitly deferred, do not build now): feedback/FBO persistence, debug overlay/HUD,
kaleidoscopic/IFS shaders, keyboard VJ controls / fullscreen toggle / device picker, JSON presets,
tempo/BPM + phase-locked LFOs, latency compensation, adaptive quality, deep-zoom arbitrary
precision, flame fractals, formula morphing, export, ImGui panel, MIDI input, L-systems. These are
Tier 2/3 in IDEAS.md and must not be pulled forward even opportunistically.

## Files affected

New files (paths relative to the project root; the project was `SDL_Demo` until 2026-10-30,
now `fractal-audio` / `FractalAudio.csproj` — historical entries below keep the old names):

- `SDL_Demo.csproj` — fix `AllowUnsafeCode` -> `AllowUnsafeBlocks`; add `PackageReference` for
  `NAudio` and `Silk.NET.OpenGL`; add `<ItemGroup>` to copy `shaders/**` to output
  (`CopyToOutputDirectory=PreserveNewest`) so hot-reload watches the same files that ship.
- `Program.cs` — rewritten to a thin entry point: `return App.Run();` (or `new App().Run();`).
  Keeps top-level-statement style but no longer contains SDL/GL calls directly.
- `App.cs` — owns the SDL window + GL context lifetime, the main loop (poll events, fixed-step
  update via `Clock`, draw, swap), and orderly teardown (dispose GL resources, stop audio capture,
  `SDL_Quit`).
- `Window.cs` — wraps `SDL_CreateWindow` with `SDL_WINDOW_OPENGL` flag; sets
  `SDL_GL_SetAttribute` (context major/minor version, profile mask = core, doublebuffer) *before*
  window creation as SDL requires; exposes the raw `IntPtr` window handle and current
  width/height (updated on `SDL_WINDOWEVENT_RESIZED`).
- `Clock.cs` — fixed-step accumulator (e.g. 1/120s logical step) + measured wall frame time,
  decoupled from present (present stays vsync-gated via `SDL_GL_SwapWindow`). Exposes elapsed
  real time, delta, and a rolling average frame time for later use (no adaptive-quality logic yet
  — that's Tier 2 — just the measurement hook IDEAS.md asks for).
- `Graphics/GlLoader.cs` — one-shot spike/shim: loads a Silk.NET `GL` instance bound to the SDL GL
  context via `SDL.SDL_GL_GetProcAddress`. Isolated here because its exact shape is the single
  biggest unverified risk in the plan (see Risks).
- `Graphics/ShaderProgram.cs` — compiles/links a vertex+fragment pair, holds the GL program
  handle, exposes `SetUniform1f/2f/1i` etc. and `Use()`. Throws a specific
  `ShaderCompileException` with the GL log on failure (never lets a bad shader take down the
  program — caller decides fallback).
- `Graphics/FullscreenQuad.cs` — VAO/VBO/EBO for a single screen-filling quad (2 triangles, NDC
  coords + UV), `Draw()` issues the draw call. No per-frame allocation.
- `Graphics/ShaderHotReloader.cs` — `FileSystemWatcher` on `shaders/`, debounced (coalesce rapid
  save events), recompiles on a *flag checked once per frame on the render thread* (GL is
  thread-affine — the watcher callback only sets a "dirty" flag, never calls GL directly). Keeps
  the last-good `ShaderProgram` bound if recompilation fails; logs the GL error to console.
- `Graphics/PaletteTexture.cs` — generates a 256-entry 1D LUT (GL_TEXTURE_1D or a 256x1
  GL_TEXTURE_2D if 1D textures prove awkward under the loaded GL profile) from Iñigo Quilez
  cosine-palette coefficients (4 vec3: a, b, c, d); supports swapping the active palette index and
  re-uploading.
- `shaders/quad.vert` — passthrough vertex shader (NDC position + UV out).
- `shaders/solid.frag` — throwaway test shader (uniform solid color) used only in Phase 2/3 to
  prove the GL pipeline and hot-reload before the real fractal shader exists. Can be deleted once
  Phase 4 lands, or kept as a minimal fallback shader.
- `shaders/fractal.frag` — Mandelbrot/Julia escape-time shader: smooth (continuous) iteration
  count via the standard `n - log2(log2(|z|))` correction, derivative-based distance estimate for
  boundary crispness, samples `PaletteTexture` by normalized smooth-iteration value, uniforms:
  `u_resolution`, `u_c` (vec2, the Julia constant), `u_zoom`, `u_center`, `u_maxIter`,
  `u_paletteOffset` (phase).
- `Audio/AudioFrame.cs` — immutable snapshot contract (see "AudioFrame contract" below).
- `Audio/AudioFrameBus.cs` — lock-free single-writer/single-reader publish/consume via
  `Interlocked.Exchange` on a reference field (see below).
- `Audio/CaptureStatus.cs` — enum `{ Capturing, SilenceTimeout, DeviceUnavailable }`, set by
  `AudioCapture`, read by `AudioAnalyzer`/console logging (and later, Tier 2's overlay — not built
  now).
- `Audio/AudioCapture.cs` — wraps `NAudio.CoreAudioApi.WasapiLoopbackCapture` on the default
  render endpoint, runs it on its own thread with its own ring buffer
  (`System.Threading.Channels.Channel<float[]>` or a simple fixed-capacity circular buffer of
  floats — pick the circular buffer, see Approach), handles the three WASAPI gotchas explicitly
  (see below).
- `Audio/AudioAnalyzer.cs` — pulls mono frames from the ring buffer, applies Hann window, runs
  `NAudio.Dsp.FastFourierTransform.FFT` (ships inside the `NAudio` package — no extra FFT
  dependency needed), bins into 7 log-spaced bands computed from the *actual* capture sample rate
  (not hardcoded for 48kHz), computes RMS/peak/spectral centroid, runs `EnvelopeFollower` per
  band, runs `OnsetDetector`, and publishes a new `AudioFrame` to `AudioFrameBus` once per
  analysis tick.
- `Audio/EnvelopeFollower.cs` — per-band fast-attack/slow-release smoother (single-pole IIR,
  separate attack/release coefficients derived from configurable attack-ms/release-ms + the
  analyzer's tick rate).
- `Audio/OnsetDetector.cs` — normalized spectral flux (half-wave rectified bin-energy delta)
  against a rolling-median/mean adaptive threshold; emits a one-tick-wide `bool` pulse, not a
  level.
- `Mapping/AudioToFractalMapper.cs` — the continuous-vs-discrete rule and the Julia-`c` path
  (curated waypoints + glide — see "Julia mapping" below); owns the current palette index and
  palette phase; reads the latest `AudioFrame` once per render frame and writes the shader
  uniforms + palette state. This is the only place IDEAS.md's "continuous vs discrete" design
  guard is enforced, so keep all mapping logic here rather than scattered into `App`/shader.

No other existing files are touched. `Source.sln` needs no changes (single project).

## Approach

Each phase below must build and visibly/audibly demonstrate its own milestone before the next
phase starts — do not combine phases even though some are small.

### Phase 0 — csproj fix (Tier 0)
Fix `AllowUnsafeCode` -> `AllowUnsafeBlocks`. Build. No behavior change yet (the flat loop still
runs). Trivial, effort: small.

### Phase 1 — App skeleton, still on SDL_Renderer (Tier 0)
Split `Program.cs` into `App`/`Window`/`Clock` *without* touching rendering — keep
`SDL_CreateRenderer` + clear-to-skyblue exactly as-is, just move it behind the new classes, and
add the fixed-step accumulator + frame-time measurement in `Clock`. This isolates "did I break the
loop/skeleton" from "did I break GL" — if Phase 2 goes wrong you can bisect against a known-good
skeleton.
**Test:** app behaves identically to today (skyblue window, clean quit), console optionally logs
frame time once/sec.

### Phase 2 — GL context + solid-color fullscreen quad (Tier 0)
Replace `SDL_Renderer` with `SDL_GL_CreateContext`. Steps, each independently checkable:
1. Spike: call `SDL_GL_SetAttribute` (CONTEXT_MAJOR=3, MINOR=3, PROFILE_MASK=CORE,
   DOUBLEBUFFER=1) before `SDL_CreateWindow` (with `SDL_WINDOW_OPENGL` flag), then
   `SDL_GL_CreateContext`. If this returns `IntPtr.Zero` or `SDL_GetError()` complains, the
   bundled native `SDL2.dll`'s GL support ceiling is lower than assumed — fall back to requesting
   GL 2.1/compatibility and lowering the GLSL version target everywhere downstream (see Risks).
   Log `glGetString(GL_VERSION)` to confirm what you actually got.
2. Build `Graphics/GlLoader.cs` to get a working Silk.NET `GL` instance from
   `SDL_GL_GetProcAddress`. This is the other unverified surface — if Silk.NET.OpenGL's loader
   doesn't accept a bare delegate cleanly, fall back to hand-written `DllImport`s for just the
   handful of GL entry points actually needed (shader compile/link, buffers, textures, draw,
   uniforms) rather than fighting the binding.
3. `ShaderProgram` + `FullscreenQuad` + `shaders/quad.vert` + `shaders/solid.frag`: draw a static
   solid color via `SDL_GL_SwapWindow` instead of `SDL_RenderPresent`.
4. Handle `SDL_WINDOWEVENT_RESIZED` -> `glViewport`.
**Test:** window shows a flat color rendered through GL, vsync still holds frame rate, resizing
the window doesn't crash or distort.

### Phase 3 — Shader hot reload (Tier 0)
Add `ShaderHotReloader` watching `shaders/`. Edit `solid.frag`'s color uniform/constant while the
app runs — confirm it updates live. Then deliberately break the file (syntax error) — confirm the
app keeps rendering the last-good program and prints a GL compile/link error instead of
crashing/exiting.
**Test:** both the "live update works" and "bad shader doesn't kill the app" cases observed
manually.

### Phase 4 — Fractal shader, no audio yet (Tier 1 shader half)
Write `shaders/fractal.frag` (Mandelbrot/Julia escape-time, smooth coloring, distance-estimate
boundary) and `Graphics/PaletteTexture.cs` (IQ cosine palette LUT). Wire static/time-driven
uniforms only (e.g. animate `c` on a fixed circle using `Clock`'s elapsed time) — no audio code
exists yet. This isolates "is the fractal math/palette correct" from "is audio capture correct".
**Test:** recognizable, correctly-colored Mandelbrot/Julia set renders and visibly changes shape
as the hardcoded `c` animates; palette LUT colors look right (no banding from the LUT sampling,
boundary isn't jagged thanks to the distance estimate).

### Phase 5 — WASAPI capture, console-only (Tier 1 audio half, part 1)
Add `NAudio` package reference. Build `Audio/AudioCapture.cs` with
`WasapiLoopbackCapture` on its own thread, writing samples into a fixed-capacity circular float
buffer (choose a circular buffer over `Channel<T>` — simpler, no allocation per callback, and the
analyzer only ever needs "the most recent N samples", not strict FIFO delivery). No analyzer, no
FFT, no shader wiring: just log RMS of the most recent window to console once per second.
Explicitly implement and manually verify all three WASAPI gotchas here:
1. **Silence timeout** — loopback raises no `DataAvailable` during digital silence. Track
   `lastDataAvailableUtc`; if nothing arrives within a configurable timeout (propose 200–300ms —
   longer than one video frame's worth of silence but short enough to feel responsive), set
   `CaptureStatus.SilenceTimeout` and keep producing zero-filled analysis windows at a steady
   cadence rather than freezing the last real window. (Decouple "analysis tick" from "capture
   callback" — the analyzer runs on its own timer and just reads whatever's newest in the ring
   buffer, defaulting to silence if nothing new arrived.)
2. **WaveFormat handling** — read `capture.WaveFormat` after construction; do not assume
   stereo/48kHz. Downmix to mono by averaging all channels. Compute FFT-bin-to-band-Hz mapping
   from the *actual* `SampleRate` at startup (and if the device's format can change at runtime,
   recompute on restart — not expected mid-session, but don't hardcode constants that would
   silently misbehave on a 44.1kHz or 96kHz device).
3. **Exclusive-mode device** — if `StartRecording()` succeeds but *no* data arrives at all within
   a longer startup grace window (propose 2s, distinct from the steady-state silence timeout),
   set `CaptureStatus.DeviceUnavailable` and log clearly ("default render endpoint may be held in
   WASAPI exclusive mode by another application") rather than silently sitting at "silence"
   forever with no diagnostic.
**Test:** play music, see RMS move in console; pause music, see RMS decay/drop to the silence
state within the configured timeout instead of freezing; (manual, optional) lock the output device
into exclusive mode with another app and confirm the `DeviceUnavailable` log appears instead of a
hang or crash.

### Phase 6 — Audio feature analysis (Tier 1 audio half, part 2)
Build `Audio/AudioAnalyzer.cs`, `EnvelopeFollower.cs`, `OnsetDetector.cs`, `AudioFrame.cs`,
`AudioFrameBus.cs`. Concretely:
- 2048-sample Hann-windowed FFT via `NAudio.Dsp.FastFourierTransform.FFT` (power-of-two size,
  already included in the `NAudio` package — no extra FFT dependency).
- 7 log-spaced bands by Hz range (edges to start from, tune later):
  sub-bass 20–60, bass 60–150, low-mid 150–400, mid 400–1000, high-mid 1000–2500,
  presence 2500–6000, air 6000–12000 Hz — convert each edge to an FFT bin index from the
  capture's actual sample rate (bin width = sampleRate/2048), not a hardcoded table.
- RMS and peak over the analysis window; spectral centroid = Σ(bin freq × bin magnitude) /
  Σ(bin magnitude), normalized to 0..1 against a running max for display/shader use.
- `EnvelopeFollower` per band: fast attack (~10ms), slow release (~300ms) single-pole smoothing,
  coefficients derived from the analyzer's tick interval.
- `OnsetDetector`: half-wave-rectified spectral flux (sum of positive bin-magnitude deltas vs
  previous frame) compared to a rolling mean/median + multiplier adaptive threshold; emits a
  one-tick `OnsetDetected` pulse (not sustained).
- Publish a new `AudioFrame` once per analysis tick via `AudioFrameBus.Publish(frame)`.
**Test:** console-print (throttled to ~2/sec) the 7 band envelope values + onset boolean while
music plays; confirm by ear/eye that bass band jumps on kicks, air/presence jump on hats/cymbals,
and onset pulses roughly track perceived beats. Tuning the exact band edges/threshold multiplier
is expected to take iteration — this phase's bar is "mechanism works and is directionally
correct", not "perfectly tuned".

### Phase 7 — Wire audio into the fractal shader (Tier 1 integration)
Build `Mapping/AudioToFractalMapper.cs`. Each render frame: read `AudioFrameBus.Latest`
(non-blocking), compute and push shader uniforms. This is where the continuous/discrete rule and
the Julia-`c` mapping live (see dedicated sections below). Remove the Phase-4 time-driven `c`
animation once this is wired.
**Test:** with real music playing, Julia shape visibly changes with bass energy and spectral
centroid, palette phase visibly cycles with RMS, and a detected beat visibly snaps `c` toward a
new region and/or switches the palette. Pausing music settles the visual to a calm/idle state
(via the silence-timeout path from Phase 5) rather than freezing mid-shape or going black/NaN.

### Phase 8 — Robustness pass
- Clamp `u_zoom`/`u_maxIter` to safe ranges given float32 precision (see "Float precision" below).
- Rate-limit per-frame deltas on continuous uniforms (palette phase speed, rotation) to avoid
  strobing from sudden loud transients — a cap on max change-per-frame is enough for v1; full
  adaptive-quality/flicker-guard work is Tier 2.
- Confirm `CaptureStatus.DeviceUnavailable`/`SilenceTimeout` never throws into the render loop —
  `AudioFrameBus.Latest` must always return *some* valid (possibly all-zero) `AudioFrame`, never
  null, from frame one (publish an initial silent frame at startup before capture has produced
  anything).
- Dispose GL objects (programs, textures, VAO/VBO), stop/dispose `WasapiLoopbackCapture`, and
  `SDL_GL_DeleteContext`/`SDL_DestroyWindow`/`SDL_Quit` in `App`'s teardown path, in that order.
**Test:** run 10+ minutes with real playback including at least one pause/resume and one track
change; no unhandled exceptions, no visual freeze, clean process exit on window close.

### Phase 9 — Playtest fixes (IDEAS.md "Tier 1 fixes")
Scoped to exactly the four playtest items in IDEAS.md; `AudioToFractalMapper` stays the sole
writer of fractal uniforms and the continuous/discrete split still lives only there.
1. **Breathing zoom, not a ramp.** Replace the monotonic `_zoom` drift with a bounded,
   bass-driven zoom around `BaseZoom` (plus a short beat punch). Move `u_center` off `(0,0)`
   toward the Julia set's repelling fixed point β = (1 + √(1−4c))/2 — always on the Julia
   set, so it's a boundary point computed analytically from the live `c` — scaled by how far
   zoomed-in we are, so zooming reads as moving toward the boundary, not into the interior.
2. **Visible shape change.** Normalise band energies with a per-band decaying running max
   (raw FFT magnitudes are far below 1, so the old `Clamp(bass, 0, 1) * 0.05` barely moved `c`).
   Widen `PerturbRadius` to 0.12 and drive the angle from centroid plus a low-mid/presence band
   term. Rate-limit only the perturbation, not the waypoint glide (the old code rate-limited the
   sum, which silently slowed every glide).
3. **Palette crossfade.** Bake all palettes into one 256×N atlas texture once; the shader blends
   two rows by `u_paletteBlend`. No runtime re-upload, no second texture unit. Palette changes
   are discrete (general onset) but held for a minimum time and crossfaded over ~1.5s.
4. **Beat-specific trigger.** Second `OnsetDetector` over 20–150 Hz bins only, with a
   refractory period against sustained-bass double triggers → `AudioFrame.BeatDetected`.
   Beats drive the waypoint change with a fast ease-out (~0.25s) plus a decaying zoom punch.
   Also fixes a latent bug: the render thread (60Hz) re-reads the same analyzer frame (50Hz),
   so a one-tick `OnsetDetected` pulse could be acted on twice (or missed). `AudioFrame` gains
   monotonic `OnsetCount`/`BeatCount`, and the mapper edge-detects on those instead.
**Test:** with music: silhouette changes shape and no longer grows unboundedly; palette changes
fade rather than snap; waypoint jumps and zoom punches land on kicks, not hi-hats.

## Tempo test tracks (user-confirmed BPM)

Recordings of these (FRACTAL_AUDIO_DUMP) can be scored with `tools/TempoReplay`; the dumps live
outside the repo (they are tens of MB). Replay scores at `HEAD` (exact = within 3%, readings
every 0.5s after 10s): Diesel Power, Overdrive, Disco, Teardrop, Catch, PNG 99-100%;
Hyperspeed 79% (misses are its half-time intro, corrected once the full beat enters); KLF 46%
(riff-only intro reads 79 until the kick enters — Catch, KLF and PNG dumps predate the
audio-time framing fix).

Re-check these live after any change to capture, analysis or `TempoTracker`. Status as of
`842d612`, confirmed by the user on the overlay unless noted.

| Track | True BPM | Reads | What it exercises |
| --- | --- | --- | --- |
| Kosheen — Catch | 130 | 130 (pre-framing-fix) | bass line carries no beat → needs full-band flux |
| KLF — What Time Is Love | 120 | not re-checked since fixes | 1.5-beat riff (3:2 rival) |
| Warpaint — Disco | 97 | 97 | strong half-beat (8th-note) periodicity |
| Lazerhawk — Overdrive | 133 | 133 | kick/snare alternation → half-tempo trap |
| Future Sound of London — Papua New Guinea | 125 | 124 after ~175 at startup | two kicks per 2-beat cycle |
| Massive Attack — Teardrop | 77 | 77 (offline, from a recording) | slow tempo; must not be doubled |
| The Prodigy — Hyperspeed | 150 | 150; dropped to 75/120 until `22ed884` | half-time intro; 5-sixteenth acid riff (120) |
| The Prodigy — Diesel Power | 106 | 144-149 until the bass term was dropped; replay now 106 | breakbeat; bass peaks at 53/132, kick dotted-8th (4:3 trap) |
| Public Service Broadcasting — I Was Always Dreaming | unknown | never locks (conf 0.10-0.18) | low-confidence / "searching" path |

## AudioFrame contract (concrete)

`Audio/AudioFrame.cs` — an immutable **sealed class** (not a struct — see rationale below):

```csharp
public sealed class AudioFrame
{
    public static readonly AudioFrame Empty = new(/* all-zero, IsSilent = true */);

    public required double TimestampSeconds { get; init; }   // capture-side clock, monotonic
    public required CaptureStatus Status { get; init; }
    public required bool IsSilent { get; init; }              // true once silence timeout fires
    public required float Rms { get; init; }
    public required float Peak { get; init; }
    public required float SpectralCentroid01 { get; init; }   // normalized 0..1
    public required float Flux { get; init; }                 // normalized spectral flux
    public required bool OnsetDetected { get; init; }          // one-tick pulse, not sustained
    public required ImmutableArray<float> BandEnergies { get; init; }      // 7, post-envelope
    public required ImmutableArray<float> BandEnergiesRaw { get; init; }   // 7, pre-envelope (debug)
}
```

`Audio/AudioFrameBus.cs` — the double-buffer/atomic-swap mechanism:

```csharp
public sealed class AudioFrameBus
{
    private volatile AudioFrame _latest = AudioFrame.Empty;

    public AudioFrame Latest => _latest;                       // render thread reads this, lock-free

    public void Publish(AudioFrame frame) => _latest = frame;  // audio thread writes this
}
```

Rationale for class-over-struct: reference assignment/read in .NET is atomic, so a `volatile`
reference field gives a correct single-writer/single-reader swap with zero locking and zero risk
of the render thread observing a torn mix of old/new fields — which a multi-field mutable struct
behind a lock would also achieve, but with needless contention, and a plain struct field write is
*not* guaranteed atomic across all its fields. `ImmutableArray<float>` for the bands keeps the
whole snapshot genuinely immutable without per-publish heap churn beyond one array fill (7
floats — negligible at ~30–60 publishes/sec). The audio analyzer thread is the sole writer; the
render thread is the sole reader; there is exactly one `Publish` call per analysis tick, matching
IDEAS.md's "one atomic swap per frame".

## Julia mapping (audio-driven `c`)

IDEAS.md proposes "bass energy -> radius, spectral centroid -> angle" directly. Flagging a
refinement rather than silently diverging: a raw, unconstrained radius/angle mapping from live
audio has no built-in guard against wandering into visually dead regions (deep in the M-set
interior, where Julia becomes a filled blob, or far outside it, where Julia becomes an empty
disc) — IDEAS.md itself calls this out as the risk and says "clamp to a hand-picked region or a
parametric curve". Concretely, for Tier 1:

- Maintain a small curated array (propose ~8) of known-interesting Julia constants near the
  Mandelbrot boundary (e.g. values in the neighborhood of `-0.8+0.156i`, `-0.70176-0.3842i`,
  `0.285+0.01i`, `-0.4+0.6i`, etc. — exact set to be eyeballed during Phase 4/7, since "visually
  interesting" is a judgment call, not something to hardcode blind in this plan).
- **Continuous**: bass envelope and spectral centroid perturb `c` *around* the current target
  waypoint within a small bounded radius (e.g. ±0.05), giving constant subtle motion without
  risking a jump into a dead zone.
- **Discrete**: on `OnsetDetected`, pick the *next* waypoint (sequential, or audio-feature-biased
  — e.g. higher centroid picks a "spikier" waypoint — tune later) as a new glide target.
- The actual uniform value glides toward the current target over a fixed short window (propose
  0.5–2s) each frame, rather than snapping — this is what makes the "discrete" trigger still read
  as smooth motion on screen, satisfying both IDEAS.md's discrete-trigger guidance and basic
  visual smoothness.
- This is a concrete default, not a hard requirement — if whoever implements this prefers the
  literal radius/angle mapping from IDEAS.md, that's a reasonable alternative; the waypoint
  approach is proposed here specifically because it's easier to make robust (bounded, curated) on
  the first pass.

## Palette + continuous/discrete mapping rule

- **Continuous** (every frame, smooth): zoom velocity (small constant drift, optionally
  RMS-scaled), palette phase rotation (driven by RMS), the small in-waypoint `c` perturbation
  above.
- **Discrete** (only on `OnsetDetected`): Julia waypoint target change, palette *index* switch
  (cycle through the IQ cosine palette list).
- Enforce this split entirely inside `Mapping/AudioToFractalMapper.cs` — no other code should
  write to shader uniforms, so this rule has exactly one place it can be violated or verified.

## Float precision / clamping

- GLSL `float` (32-bit) ULP-driven iteration breakdown happens around `zoom` ~1e-5 to 1e-6 for
  escape-time fractals — fine for Tier 1 (no deep zoom requirement here; that's Tier 3). Clamp
  `u_zoom` to a safe maximum (propose 1e4) so the mapper can never drive the fractal past the
  point where float cracking becomes visible, even if a future continuous-zoom feature is added
  carelessly.
- Clamp `u_maxIter` to a fixed band (propose 100–500) — both for visual consistency and to avoid
  a runaway frame-time cost if it's ever accidentally driven by an unbounded audio value.
- Bail-out radius fixed at 4.0 (`|z|^2 > 4.0`), standard for escape-time smooth coloring — not an
  audio-driven parameter.

## Risks / open questions

- **Silk.NET.OpenGL loader shape vs. SDL's GL context is unverified.** Silk.NET.OpenGL expects to
  load function pointers from *some* proc-address source; I have not confirmed its exact
  `GL.GetApi(...)` overload against `SDL_GL_GetProcAddress`'s delegate signature in this repo.
  Resolve this in Phase 2 step 2 as a tiny spike before building anything on top. Fallback if it
  doesn't fit cleanly: hand-written `DllImport`s for the handful of GL entry points actually
  needed (shaders, buffers, textures, draw calls, uniforms) — more boilerplate, zero binding risk.
- **Whether `Sayers.SDL2.Core` 1.0.11 actually exports correct `SDL_GL_*` P/Invoke signatures** is
  taken on faith from IDEAS.md's "grounded" claim; I did not independently decompile the wrapper.
  Cheaply verified for free in Phase 2 step 1 (first call to `SDL_GL_CreateContext` either works
  or doesn't).
- **The bundled native `SDL2.dll`'s actual GL support ceiling is unknown** — IDEAS.md flags that
  Sayers bundles an "older 2.0.x" SDL2 build, which affects the *wrapper*, not necessarily the GL
  version the *native DLL's host GPU driver* can create a context for (those are independent).
  Phase 2 step 1 logs `glGetString(GL_VERSION)` to settle this; if GL 3.3 core isn't obtainable,
  fall back to a 2.1/compatibility context and a lower GLSL `#version` in all shaders (palette LUT
  via `sampler2D` instead of `sampler1D` if needed — already the plan's fallback above).
- **Exact FFT band-edge Hz values and onset threshold multiplier are not tunable from first
  principles** — Phase 6 ships a mechanism with reasonable starting numbers; expect an iterative
  by-ear tuning pass, which this plan does not attempt to pre-specify further.
  No debug overlay exists in Tier 1 scope (that's Tier 2), so this tuning happens via temporary
  console logging, not a visual instrument — slower than ideal, but in scope as stated.
- **Julia-`c` mapping**: proposed curated-waypoint approach (above) is a deliberate refinement of
  IDEAS.md's literal radius/angle suggestion, flagged as such rather than silently substituted —
  confirm this is acceptable, or implement the literal mapping with its own clamp region instead.
- **GL thread affinity**: `ShaderHotReloader`'s `FileSystemWatcher` callback fires on its own
  thread pool thread; it must only set a dirty flag, never call GL directly. All GL calls —
  including shader recompilation — must happen on the single render thread, checked once per
  frame. Call this out in code comments at the point of implementation; it's an easy mistake to
  reintroduce later (e.g. if a future device-picker or preset-reload feature reuses the pattern).
- **NAudio package shape**: assuming the single `NAudio` meta-package (not a separate
  `NAudio.Wasapi` sub-package) provides both `WasapiLoopbackCapture` and `NAudio.Dsp.
  FastFourierTransform` in one reference, per current NAudio package layout. Verify the actual
  namespaces/version at `dotnet add package` time in Phase 5; if `FastFourierTransform` isn't
  present in the resolved version, the fallback is a small hand-rolled radix-2 FFT (no new
  dependency needed either way — avoid pulling in MathNet.Numerics for this).
- **Ambiguity flagged, not resolved by this plan**: IDEAS.md doesn't specify exact attack/release
  milliseconds, exact silence-timeout duration, or the exact number/identity of curated Julia
  waypoints. This plan proposes concrete starting numbers throughout (200–300ms silence timeout,
  10ms attack/300ms release, ~8 waypoints, 0.5–2s glide) explicitly as starting points for the
  implementer to tune, not as settled requirements — flagging so they aren't mistaken for
  carefully-derived constants.

## Done when (Tier 1 acceptance checklist)

- [ ] `SDL_Demo.csproj` builds clean with `AllowUnsafeBlocks` (no invalid-property warning).
- [ ] App renders through an OpenGL context + fullscreen quad; `SDL_Renderer` is fully removed;
      vsync still holds; window resize works without crashing.
- [ ] Editing a shader file on disk while the app is running changes the visual without a
      restart; introducing a shader syntax error logs a GL error and keeps rendering the last-good
      program instead of crashing.
- [ ] `WasapiLoopbackCapture` runs on its own thread against the default render endpoint; RMS/band
      values visibly track real system audio playback.
- [ ] Pausing playback decays all audio features to a silent/idle state within the configured
      timeout instead of freezing the last values.
- [ ] If the output device is held in WASAPI exclusive mode by another app, the capture logs a
      clear `DeviceUnavailable` status and the app keeps running (idle visuals), not a hang/crash.
- [ ] `AudioFrameBus.Latest` always returns a valid non-null `AudioFrame` from the first render
      frame onward, with no locks on the render thread's read path.
- [ ] Mandelbrot/Julia escape-time shader renders with smooth iteration coloring and a working
      IQ-cosine-palette LUT (no banding, clean boundary via distance estimate).
- [ ] With real music playing: Julia `c` visibly responds to bass energy and spectral centroid
      continuously, and visibly jumps to a new region on detected beats/onsets.
- [ ] Palette phase visibly cycles with RMS (continuous) and the active palette switches on a
      detected beat (discrete).
- [ ] The continuous-vs-discrete mapping split is enforced in one place
      (`Mapping/AudioToFractalMapper.cs`) and documented there in a comment.
- [ ] Sustained 10+ minute run with real playback, including a pause/resume and a track change,
      produces no unhandled exceptions and exits cleanly on window close.
- [ ] (Phase 9) Zoom stays bounded and breathes with bass; the dark interior no longer grows
      over time regardless of input.
- [ ] (Phase 9) Julia shape visibly morphs with the music, not just the camera.
- [ ] (Phase 9) Palette changes crossfade; no instant full-palette swaps.
- [ ] (Phase 9) Waypoint jumps/zoom punches track kicks rather than every transient.
- [ ] (Phase 9) On a track that locks, palette fades and shape changes start on the beat and the
      zoom punch peaks on it (not after it).

## Change log
- 2026-10-21 14:00 [planner] Created plan. Verified csproj bug, Program.cs contents, and
  bin/Debug native DLL set directly against the repo (not taken on faith from IDEAS.md). Phased
  Tier 0 (csproj fix, app skeleton, GL pipeline, hot reload) ahead of Tier 1 as explicit
  prerequisite phases 0-3, with Tier 1 phases 4-8 each independently buildable/testable per the
  task's requirement (solid-color quad before fractal shader; console RMS before shader wiring).
  Flagged open risks: Silk.NET.OpenGL loader shape vs SDL proc-address, actual native SDL2.dll GL
  ceiling, and NAudio package/FFT availability — all cheaply verified early in their respective
  phases rather than blocking planning. Proposed (and flagged as a refinement, not a silent
  deviation) a curated-waypoint Julia-`c` mapping in place of IDEAS.md's literal radius/angle
  suggestion, for robustness against dead-zone wandering.
- 2026-10-21 22:10 [worker] Phase 0: fixed `AllowUnsafeCode` -> `AllowUnsafeBlocks` in
  SDL_Demo.csproj. Build clean.
- 2026-10-21 22:15 [worker] Phase 1: split `Program.cs` into `App.cs`/`Window.cs`/`Clock.cs`.
  `App` still uses `SDL_CreateRenderer` + clear-to-skyblue, identical behavior to the original
  flat loop. `Clock` adds a 1/120s fixed-step accumulator and an EMA rolling average frame time,
  logged once/sec. Verified via a 5s timed run: window creation succeeds, frame time settles at
  ~16.65ms (vsync-gated), clean exit.
- 2026-10-21 22:30 [worker] Phase 2: added `Silk.NET.OpenGL` (2.23.0) and `Silk.NET.Core`
  (2.23.0, explicit — transitively required by Silk.NET.OpenGL's `GetApi` overloads) package
  references; added `shaders/**` CopyToOutputDirectory ItemGroup. `Window.cs` now sets
  `SDL_GL_SetAttribute`(CONTEXT_MAJOR=3, MINOR=3, PROFILE_MASK=CORE, DOUBLEBUFFER=1) before
  `SDL_CreateWindow` with the `SDL_WINDOW_OPENGL` flag. `App.cs` replaced
  `SDL_CreateRenderer`/`SDL_RenderPresent` with `SDL_GL_CreateContext` + `SDL_GL_SwapWindow`,
  draws a solid-color fullscreen quad via `Graphics/ShaderProgram.cs` + `Graphics/FullscreenQuad.cs`
  + `shaders/quad.vert` + `shaders/solid.frag`, and calls `glViewport` on
  `SDL_WINDOWEVENT_RESIZED`. Both flagged risks resolved and verified by running the built
  binary: (1) Silk.NET.OpenGL 2.23.0's `GL.GetApi` has a direct `Func<string, IntPtr>` overload
  (confirmed via a temporary reflection probe before writing `GlLoader.cs`) — binds cleanly to
  `SDL_GL_GetProcAddress`, no custom `IBindingsContext`/DllImport fallback needed. (2) The
  bundled native SDL2.dll can create a GL 3.3 core context on this machine — a timed run logged
  `[GL] GL_VERSION: 3.3.0 Core Profile Context 25.10.30.02.250923`; all shaders target
  `#version 330 core`, no fallback needed. Vsync still holds (~16.66ms/frame). Resize ->
  `glViewport` path is implemented but only logic-level verified (no display access to confirm
  visually).
- 2026-10-21 22:50 [worker] Phase 3: added `Graphics/ShaderHotReloader.cs` — `FileSystemWatcher`
  on the `shaders` output directory sets a `volatile` dirty flag + debounce timestamp from its
  callback thread only; `Update()` (called once per frame from `App.Draw`, render thread) checks
  the flag, debounces 150ms, and recompiles. On compile/link failure it logs the GL error and
  keeps the previous `ShaderProgram` bound (no swap, no crash); on success it disposes the old
  program and swaps in the new one. `App.cs` now owns a single `ShaderHotReloader` instead of a
  bare `ShaderProgram`. Verified live, end-to-end, against the running built binary (not just
  code review): started the app in the background, edited the *output* `solid.frag` (the file
  the watcher actually watches) to change the color — console logged
  `[ShaderHotReloader] shader reloaded.` within ~2s, confirming live update works without a
  restart. Then edited it again with a deliberate syntax error (missing comma in a `vec4(...)`
  call) — console logged `[ShaderHotReloader] reload failed, keeping last-good program: ...
  syntax error ...` and the process kept running/logging frame times normally afterward,
  confirming the last-good-program fallback. Restored the file to its working state afterward.
  Note: the background test process (PID 499 in that run) could not be terminated from this
  session — `kill`/`taskkill` require interactive approval that wasn't grantable here; flagged in
  the final report for the user to close manually if still running. (Resolved during Phase 4 —
  the process exited on its own before the Phase 4 build, see below.)
- 2026-10-21 23:10 [worker] Phase 4: added `shaders/fractal.frag` (Julia escape-time, smooth
  `n - log2(log2(|z|))` iteration coloring, derivative-based distance estimate for boundary
  crispness, samples a `sampler2D` palette LUT) and `Graphics/PaletteTexture.cs` (8 hand-picked
  IQ cosine palettes baked into a 256x1 `GL_TEXTURE_2D` — used `TEXTURE_2D` directly rather than
  `TEXTURE_1D` per the plan's stated fallback option, to avoid an extra unverified surface).
  `App.cs` now loads `fractal.frag` through the Phase-3 `ShaderHotReloader` instead of
  `solid.frag`, binds the palette texture to texture unit 0, and drives `u_c` on a fixed circle
  using `Clock.ElapsedSeconds` (explicitly flagged in code as a Phase-4-only stand-in, to be
  replaced by `Mapping/AudioToFractalMapper` in Phase 7); `u_zoom`/`u_center`/`u_maxIter` are
  static for now. Verified: build succeeds; a timed run shows clean startup (`GL_VERSION` log,
  steady ~16.7ms frame time) with no shader compile/link exception thrown on startup (the initial
  `ShaderHotReloader` load has no fallback, so a failure here would have thrown and crashed —
  it didn't). Visual correctness of the fractal shape/palette (no banding, clean DE boundary,
  recognizable Julia set, shape animating with `c`) was NOT verified — no display access in this
  session; left for a human to confirm per the task's instructions.
  Incidental: while iterating, a leftover background test process from the Phase 3 hot-reload
  verification was found holding a lock on `bin\Debug\net8.0\SDL_Demo.dll`, blocking normal
  `dotnet build`. Worked around by building to a temporary `buildout\` directory for that one
  verification cycle; the process exited on its own shortly after (observed via the background
  task's own completion notification), and normal `dotnet build` against `bin\Debug\net8.0`
  succeeded again afterward with no leftover processes.
- 2026-10-21 23:30 [worker] Phase 5: added `NAudio` 2.2.1 (not the `NAudio` 3.3.0 meta-package
  latest, which only targets `net9.0` and is incompatible with this project's `net8.0` TFM — pinned
  to 2.2.1, which resolves `NAudio.Core`/`NAudio.Wasapi`/etc. against `netstandard2.0` and is
  compatible). **Namespace deviation from the plan, found and fixed via a reflection probe**: the
  plan assumed `NAudio.CoreAudioApi.WasapiLoopbackCapture`; the actual type in this resolved
  version is `NAudio.Wave.WasapiLoopbackCapture` (confirmed by enumerating the loaded
  `NAudio.Wasapi` assembly's exported types before writing `AudioCapture.cs`). Confirmed
  `NAudio.Dsp.FastFourierTransform.FFT` is present and compiles against this NAudio version — no
  hand-rolled FFT fallback needed. Added `Audio/CaptureStatus.cs` (enum exactly as specified) and
  `Audio/AudioCapture.cs`: wraps `WasapiLoopbackCapture` on the default render endpoint, runs a
  dedicated monitor thread (distinct from NAudio's own internal capture callback thread) that
  implements both timeout gotchas (2000ms startup grace -> `DeviceUnavailable`; 250ms
  steady-state silence -> `SilenceTimeout`), downmixes to mono by averaging whatever channel
  count `WaveFormat.Channels` actually reports (handles both 32-bit IEEE-float and 16-bit PCM;
  logs and drops unsupported encodings rather than crashing), and writes into a
  fixed-capacity (~2s) circular float buffer guarded by a plain lock (not the lock-free
  `AudioFrameBus` path, which is a separate/later concern). `TryReadLatestMono` returns
  zero-filled silence (not stale buffer content) whenever `Status != Capturing`, which is what
  realizes the "don't freeze on silence" requirement. **Verified live against real system
  audio**: a temporary console probe (`Program.cs`, reverted after) started `AudioCapture`,
  confirmed `actual WaveFormat: 32 bit IEEEFloat: 48000Hz 2 channels` (gotcha #2's non-hardcoded
  format read working), and printed RMS once/sec for ~8s — RMS was non-zero and time-varying
  (0.0035 to 0.051), i.e. this machine had real ambient system audio playing and the full
  capture-to-RMS pipeline (format detection -> downmix -> ring buffer -> RMS) was exercised
  end-to-end with real data, not just a build check. Status stayed `Capturing` throughout since
  audio was actually present, so the `SilenceTimeout`/`DeviceUnavailable` transition paths were
  NOT observed live in this run (no scriptable way to silence/exclusive-lock the device in this
  session) — those paths were verified by code review only (structurally symmetric with the
  working `Capturing` path) and should be re-checked by a human pausing/resuming playback per the
  plan's own Phase 5 test note.
- 2026-10-21 23:50 [worker] Phase 6: added `Audio/AudioFrame.cs` and `Audio/AudioFrameBus.cs`
  exactly per the plan's concrete contract (sealed immutable class, `ImmutableArray<float>`
  bands, `volatile` reference bus, `AudioFrame.Empty` as the bus's initial value). Added
  `Audio/EnvelopeFollower.cs` (single-pole asymmetric IIR, attack/release coefficients derived
  from `exp(-dt/tau)` with `dt` = the analyzer's actual tick interval, not hardcoded) and
  `Audio/OnsetDetector.cs` (half-wave-rectified spectral flux vs rolling-mean*multiplier
  threshold, one-tick pulse). Added `Audio/AudioAnalyzer.cs`: runs on its own thread at a ~50Hz
  tick, pulls 2048 mono samples from `AudioCapture`, applies `NAudio.Dsp.
  FastFourierTransform.HannWindow` + `.FFT(forward, m, Complex[])` (signature confirmed via a
  reflection probe before writing the analyzer -- `m` is `log2(N)`, not `N` itself; `Complex` has
  public `float X`/`Y` fields for real/imaginary), bins into the plan's 7 Hz-range bands by
  converting edges to bin indices from `AudioCapture.SampleRate` (recomputed if the sample rate
  ever changes, not hardcoded), computes RMS/peak from the raw time-domain window and spectral
  centroid (normalized against a slowly-decaying running max, per the plan's suggestion) from the
  magnitude spectrum, runs one `EnvelopeFollower` per band plus `OnsetDetector`, and publishes
  one `AudioFrame` per tick via `AudioFrameBus.Publish`. Also logs a throttled (~2/sec) one-line
  summary to console (band energies, flux, onset) as a permanent operational diagnostic, not just
  a one-off test probe -- mirrors `AudioCapture`'s existing status-transition logging style.
  Verified live against the same real ambient system audio as Phase 5 (temporary console
  harness, reverted after): RMS tracked real signal (0.05-0.25), band energies were bass-skewed
  as expected for real music/audio content, spectral centroid and flux varied meaningfully
  tick-to-tick, and `OnsetDetected` pulsed intermittently (not constantly, not never) in a pattern
  consistent with transients in real audio -- meets the plan's own bar for this phase
  ("mechanism works and is directionally correct", not "perfectly tuned"). Exact
  band-edge/threshold tuning was explicitly left untuned-further per the plan.
- 2026-10-22 00:10 [worker] Phase 7: added `Mapping/AudioToFractalMapper.cs` implementing the
  curated-waypoint Julia-`c` mapping (8 hand-picked points near the Mandelbrot boundary;
  continuous bass+centroid perturbation bounded to a +/-0.05 radius around a glide-interpolated
  waypoint target; onset-triggered discrete waypoint switch with a 1s smoothstep glide, not a
  snap) and the continuous-vs-discrete split for palette state (continuous RMS-driven phase
  rotation; discrete onset-driven palette index advance) exactly as PLAN.md specifies, with the
  enforcement comment in the class doc. `App.cs` now owns `AudioCapture`/`AudioAnalyzer`/
  `AudioFrameBus`/`AudioToFractalMapper`, starts capture+analyzer before entering the main loop,
  and `Draw()` reads `AudioFrameBus.Latest` once per frame and calls `_mapper.Apply(...)` instead
  of writing any fractal uniform directly (the Phase-4 time-driven `c` stand-in was removed, per
  the plan). Teardown order updated to dispose audio capture/analyzer between GL-resource
  disposal and GL-context deletion. Verified: build succeeds; a timed run of the full
  integrated app (GL window + audio capture + analyzer + mapper, all running together) shows
  clean startup, steady ~16.7ms frame time throughout (vsync held even with the audio threads
  running concurrently), real-audio-driven onset pulses appearing in the console log, and no
  exceptions/crashes over the run. Visual confirmation that the Julia shape/palette actually
  responds correctly on screen was NOT possible (no display access in this session) — left for a
  human to confirm per the task's instructions.
- 2026-10-22 00:25 [worker] Phase 8: added clamps/rate-limits in `AudioToFractalMapper` --
  `u_zoom` clamped to [0.1, 1e4], `u_maxIter` clamped to [100, 500], continuous `c` perturbation
  and palette-offset deltas rate-limited per-frame to guard against transient-driven strobing.
  Confirmed `AudioFrameBus.Latest` already never returns null and teardown order was already
  correct from Phase 7. Build succeeds; a 15s timed run of the fully integrated app showed
  steady ~16.7ms frame time and continuous real-audio-driven analyzer output with no
  exceptions/crashes. Status set to in-review -- all 9 phases implemented and build-verified;
  visual/audio-reactivity/long-run checks remain for a human with a display + playback control.
- 2026-10-21 [worker] Phase 9 (playtest fixes from IDEAS.md): added the Phase 9 section above,
  then implemented it. `OnsetDetector` takes a span + optional refractory period;
  `AudioAnalyzer` runs a second, 20-150 Hz-only instance (12-tick refractory) for
  `AudioFrame.BeatDetected`, and publishes monotonic `OnsetCount`/`BeatCount`.
  `PaletteTexture` is now a 256x8 atlas baked once (`SetActivePalette` removed); `fractal.frag`
  mixes two rows by `u_paletteBlend`. `AudioToFractalMapper` rewritten per Phase 9: counter
  edge-detection, beat -> waypoint (0.25s ease-out) + zoom punch, onset -> palette crossfade
  (1.5s, 6s minimum hold), per-band AGC, `PerturbRadius` 0.12 with a band-tilted angle,
  perturbation-only rate limit, bounded bass-breathing zoom, center leaning toward the β
  fixed point. `Apply` no longer takes the `PaletteTexture`. Verified: build clean; 14s timed run
  with real system audio — shader compiled (startup load has no fallback, would have thrown),
  ~16.7ms frame time, ~2.4 beats/s vs ~6.3 onsets/s. Log confirmed two root causes numerically:
  raw bass envelopes ~0.02-0.03 (old perturbation ≈ 0.001, i.e. invisible), and ~6 palette
  hard-swaps/sec under the old onset->palette wiring. Visual result NOT verified (no display).
- 2026-10-21 [worker] Phase 9 playtest 2 ("Papua New Guinea", FSOL): palette crossfade and shape
  approved; changes too fast (worst at startup), bass lines read as beats, silhouette left the
  screen. Fixes: `OnsetDetector` waits for a full history window before firing (startup burst);
  beat detector history 75 ticks, multiplier 2.0, refractory 20 ticks (~150 BPM cap); waypoint
  changes held >= 1.5s (beats inside the hold only fire a 5% zoom punch), glide 0.35s. Framing:
  dropped the β-lean center (Julia sets are symmetric about 0, so any lean only pushed the shape
  off-centre); new `Mapping/JuliaBounds.cs` measures the silhouette's half-extents on a coarse CPU
  grid each frame and the mapper zooms to at most that fit minus a 10% border (fit follows fast
  when the shape grows, slowly when it shrinks). Verified: 25s run against the same track — clean,
  ~1.9 beats/s (was ~2.4), no exceptions. Framing not visually verified.
- 2026-10-21 [worker] Phase 9 playtest 3: silhouette motion "disjointed, not smooth"; beat
  still not right. Smoothness fix (this entry): waypoints re-ordered into a loop around the
  Mandelbrot boundary (with mirrored conj(c) points, 14 total) — a CPU-rendered contact sheet
  showed the old order's glide midpoints were nearly all fat cardioid-interior blobs, so every
  change inflated/deflated the shape; the loop's midpoints resemble their neighbours. Glide 0.9s
  smoothstep (was 0.35s ease-out); centroid-skip removed (sequential only). Shape inputs smoothed
  in the mapper (centroid/tilt 0.5s, radius-bass 0.2s, zoom-bass 80ms/500ms asymmetric — the raw
  centroid jumps ~0.4 tick-to-tick); beat pulse rises over ~40ms instead of a one-frame step;
  fit-zoom taus 0.15s/0.8s; `JuliaBounds` grid 128x64. 10s smoke run: clean, 16.7ms frames.
- 2026-10-21 [worker] Phase 9 playtest 3, beat fix: pulled IDEAS.md Tier 2 "tempo estimate +
  beat-phase-locked" forward as `Audio/TempoTracker.cs`. Grounded in a 40s live dump of the
  track (temporary, not committed) analysed offline: strongest periodicity ~61.3 BPM (0.979s,
  found by fold sharpness — integer-lag ACF said 62.5, ~2% off, enough to drift the phase), with
  two near-equal kicks per cycle; the raw detector fired at ~119 BPM with intervals 0.40-0.75s.
  Tracker: ACF + weak log-tempo prior -> fractional refine by fold sharpness -> clamped PLL on
  the fold peak, ignoring isolated half-cycle flips; tempo changes/octave errors need 2s to
  persist; lock/unlock hysteresis; 4s warm-up; beats fire 50ms early for analysis + display
  latency. Prototype on the dump: grid locked at 61.3 BPM, intervals 0.96-1.0s (one displaced
  beat in 32), beats within ±40ms of the accented kick. `AudioFrame` gains `TempoLocked`/
  `TempoBpm`/`BeatPhase`; `BeatDetected`/`BeatCount` are grid beats while locked, raw kicks as
  fallback. Mapper changes shape every N locked beats (N = smallest power of 2 spanning 1.5s).
  Live C# runs locked at ~80 BPM (steady 0.75s grid) and ~109 BPM in later sections of the
  playback — whether those are the musically "right" beat is NOT verified (needs ears).
- 2026-10-21 [worker] On-screen diagnostics (user request, pulls the IDEAS.md Tier 2 overlay
  forward): `Graphics/TextOverlay.cs` (SDL_ttf lines -> ABGR8888 surfaces -> CPU-composited onto
  a translucent panel -> one GL texture, drawn 1:1 via `shaders/overlay.*` on FullscreenQuad's
  VAO; re-uploads only when text changes; disables itself with a console message if the font or
  shaders fail). `Diagnostics/DiagnosticsPanel.cs` builds the text; `Diagnostics/TapTempo.cs`
  gives a tapped-BPM ground truth and tracker/tap ratio (octave errors show as x2.00/x0.50).
  `AudioFrame` gains `TempoConfidence`/`RawKickCount`; mapper exposes a read-only
  `MapperDiagnostics` snapshot + normalised bands (it remains the only uniform writer). Keys:
  D/F1 toggle, T/Space tap. Verified by screenshotting the live window: overlay renders,
  updates, locked tempo shown. Tap input NOT verified live (injected keys didn't reach the
  window).
- 2026-10-21 [worker] The first overlay screenshot showed a dust set (c = -0.799-0.295i)
  spilling off-screen: `JuliaBounds` at 48 iterations found no surviving grid point, so the fit
  kept the stale zoom. TestIter 48 -> 16: dust now gets a real extent (fit 0.78, rendered inside
  the 10% border), connected sets unchanged (checked offline). Re-screenshot: silhouette within
  the border.
- 2026-10-21 [worker] Tempo test against a known-BPM track, "Catch" (Kosheen, 130 BPM): tracker
  wandered 52/74/90/57. Offline on a 45s dump (temporary, not committed): bass flux has ~no
  periodicity at 130 (ACF 0.07 — sustained bass line); every higher band peaks exactly at 130,
  full-band log flux strongest (0.57). Tick timing ruled out (18-22ms, resampling changes
  nothing). Tracker input is now full-band log flux + 0.5 x bass flux, each mean-normalised (0.5
  the only weight good on both this and the PNG dump). Live, it then held ~130 for 30s and
  flipped to a syncopated 91 BPM that out-scored it for 2s, so added score hysteresis (keep the
  current tempo while its prior-weighted ACF >= 0.8 x best). Final live run, 75s: locked at 130
  within 5s; all 122 readings after 12s in 129.5-131.5 BPM; overlay screenshot shows 130.3 LOCKED.
  PNG re-checked only offline (on a 64-bin dump), not live.
- 2026-10-21 [worker] "What Time Is Love" (KLF, 120 BPM) locked at 79. 60s dump (temporary):
  a riff repeating every 1.5 beats dominates every band (ACF 0.76 at 79 vs 0.46 at 120); during
  the riff-only section even the bass follows it, and once the four-on-the-floor kick enters the
  bass ACF flips to 120 — but score stickiness then held 79. A metrical comb (ACF summed at
  1-4x lag) was tried and rejected offline (broke Catch and PNG: any beat-periodic pattern also
  correlates at the riff's multiples). Added 3:2 rules to `TempoTracker`: promote a rival at
  ~2/3 of the best lag when it scores >= 0.5x and the bass ACF supports it (>= 0.2 and >= bass at
  the best lag — PNG has a 3:2 rival its kick does not support); protect an established tempo
  from its own 1.5x-period riff unless it drops below 0.5x. `Process` now takes the bass onset
  signal too. Offline full-tracker simulation: 100% of estimates right on all three dumps (KLF
  ~119, Catch ~130, PNG ~61). Live re-run read a steady 131.7 with the raw kick detector
  independently at 132/min — most likely a different track by then; KLF NOT re-verified live.
- 2026-10-21 [worker] ROOT CAUSE found via "Disco" (Warpaint, 97 BPM), which locked at ~197 then
  ~80: the analyzer framed by wall clock ("every 20ms, FFT the latest 2048 samples") but WASAPI
  loopback delivers audio in ~50ms bursts (NAudio polls at half its 100ms buffer), so ~60% of
  ticks re-analysed an identical window (verified on all dumps: 1352-1804 duplicate-spectrum
  ticks per ~45-60s). The onset signal was effectively ~20Hz with jittered timing, and the
  burst/tick aliasing (delivery pattern period 38 ticks) put a one-lag ACF needle at 79 BPM
  into every recording — likely behind KLF's "79" too. All earlier tempo tuning was done on
  that corrupted signal. Fix: `AudioAnalyzer` now frames by audio time — every 20ms hop of
  captured samples is analysed exactly once, in order (`AudioCapture.TotalSamplesWritten` /
  `TryReadMonoEndingAt`); wall-clock silent frames only while no audio arrives. Re-recorded
  Disco: duplicates 11/2202, 38-lag needle gone, tempo 96.7-97.4 from 10s, confidence up to
  0.75. The PNG/Catch/KLF tunings (bass weight, 3:2 rules, stickiness) were derived on the
  aliased signal and need live re-checks; the old dumps can't be re-used.
- 2026-10-21 [worker] Capture latency: `Audio/LowLatencyLoopbackCapture.cs` subclasses NAudio's
  `WasapiCapture` with a 20ms buffer + the Loopback stream flag (NAudio's loopback class is fixed
  at 100ms and polls at half that). `AudioCapture` now logs delivery stats every 10s. Measured
  on Disco: 19.7 packets/s @ 50.7 ms -> 86.5 packets/s @ 11.6 ms; effective sample rate stayed
  47978-48192 Hz (no overruns/dropped audio). Tempo 96.7-97.6, locked within ~5s.
  `BeatLeadSeconds` (0.05) was not re-tuned — true audio-to-screen latency needs a human check.
- 2026-10-21 [worker] "Overdrive" (Lazerhawk, 133 BPM) locked at 66.5 (conf 0.8): half-tempo
  octave error. Clean 60s dump (temporary): fold sharpness can't detect it (folding at 2x the
  period gives two equal peaks, same peak/mean), and ACF prefers the slower octave (kick/snare
  alternation repeats more exactly every 2 beats). Discriminator: the bass (kick) fold *shape* —
  a second peak half a cycle from the main one at >= 0.6 of it, with bass fold sharpness >= 2,
  means the kick lands twice per "beat" → halve the period. Half-cycle ratios: Overdrive 0.80,
  Disco 0.17-0.52, Teardrop (77 BPM, recorded by accident) 0.16-0.29. Offline full-tracker
  simulation, exact-tempo metric: Overdrive 0 -> 104/104; Disco, Teardrop, Catch, KLF unchanged
  (all 100%). Papua New Guinea (old aliased dump) moves 61.3 -> ~122.6 — its kicks do fall every
  0.49s and "61" was never ground truth, so possibly a fix; unverified. Live check of Overdrive
  missed (track had changed); a live run on an unknown-BPM track stayed unlocked at conf
  0.10-0.18 ("searching", raw-detector beats), as designed.
- 2026-10-30 [worker] Renamed the project `SDL_Demo` -> `fractal-audio` (`FractalAudio.csproj`/
  `.sln`, namespace `FractalAudio`, `fractal-audio.exe`, dump env var `FRACTAL_AUDIO_DUMP`).
- 2026-10-30 [worker] Beat sync, palette: while the tempo is locked, palette changes now fall on
  the grid — every M grid beats (smallest power of two spanning the 6s hold), counted from the
  moment of lock, as are shape changes (every N beats, N | M), so each palette crossfade starts
  together with a waypoint glide on a beat. Previously palettes followed general onsets (hi-hats,
  vocals) after a 6s hold, i.e. at a time unrelated to the beat. Unlocked behaviour unchanged.
  Build clean; 20s smoke run against live system audio (tempo unlocked, conf ~0.15) clean. Locked
  behaviour NOT verified live.
- 2026-10-30 [worker] Beat sync, zoom punch: first beat-phase-locked LFO (IDEAS.md Tier 2 item,
  the "real fix" for feedback #5). While locked, the zoom-punch target is computed from
  `AudioFrame.BeatPhase` (shifted by `BeatPipeline.BeatLeadSeconds` so phase 0 is the fired grid
  beat, extrapolated between ~50Hz analyzer frames at the ~60Hz render rate) instead of being
  set by the beat event: 1 for the last 40ms before the beat, then the same 0.25s decay, so the
  smoothed pulse peaks on the beat instead of ~40ms after it and carries no analyzer/render frame
  jitter. Unlocked, the event-driven pulse is unchanged. Build clean; 30s smoke run clean, but the
  live audio never locked, so the phase path is NOT verified live.
- 2026-10-30 [worker] Beat sync, shape glide: while locked, a waypoint glide lasts the whole
  number of beats nearest 0.9s (at least 1, at most the beats between changes), so the morph
  starts and settles on grid beats; unlocked it stays 0.9s. Build clean; 15s smoke run clean
  (unlocked audio only).
