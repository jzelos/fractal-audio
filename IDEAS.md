# Feature ideas

Project: `fractal-audio` (originally `SDL_Demo`; renamed 2026-10-30) — C#/.NET 8, `Sayers.SDL2.Core` 1.0.11, currently a 73-line top-level-statement
SDL skeleton (`Program.cs`): init video, 640x480 window, accelerated+vsync renderer, clear-to-skyblue
loop, SDL_QUIT teardown. Target product: a real-time fractal visualizer driven by live system audio
output (WASAPI loopback) on Windows.

Grounded facts used below:
- `SDL_Demo.csproj` declares `<AllowUnsafeCode>true</AllowUnsafeCode>` — **not a real MSBuild property**
  (the correct name is `AllowUnsafeBlocks`), so any `unsafe` pixel-buffer code will fail to compile today.
- `Sayers.SDL2.Core.dll` does export both `SDL_GL_*` (context creation) and `SDL_LockTexture`/
  `SDL_UpdateTexture`, so **both** the GPU-shader path and the CPU-streaming-texture path are reachable
  without replacing the SDL binding.
- `bin/Debug/net8.0` already ships `SDL2_image`, `SDL2_ttf` and `SDL2_mixer` natives, so text overlays
  and image/palette loading are free; `SDL_image` is already initialised in `Program.cs`.
- There is no audio code, no fractal code, no app structure (no classes, no timing, no input beyond quit).

Ordered by value-to-effort, not novelty. Items are marked [grounded] (follows from the code/stack/prior
art found) or [speculative] (plausible, unverified for this codebase).

## Open

### Tier 1 fixes — first playtest feedback (2026-10-21, highest priority — do before any Tier 2 item)

> **Status:** all four items below implemented as PLAN.md Phase 9 (2026-10-21) — awaiting a second
> playtest before moving to Done. Item (c) was then pulled forward too: `Audio/TempoTracker.cs`
> (tempo + phase lock, scored against known-BPM tracks with `tools/TempoReplay`), and as of
> 2026-10-30, while locked, palette fades and shape glides start on grid beats, both last whole
> beats, and the zoom punch is driven by beat phase so it peaks on the beat. Nothing code-side
> is left on these four items. Still needs a playtest
> on a track that locks.

User ran the app with real music and reported: (1) a coloured background with a black silhouette in
the centre that gradually grows, (2) background colour changes that seem to track the audio but
timing is hard to judge, (3) the silhouette needs to *change shape*, not just grow, (4) the
background change needs to be more gradual, (5) the whole thing needs to read as in-time with the
beat. All four are traceable to specific code, not vague polish — see each item.

- **Zoom-only-ever-increases is why it reads as "growing", not "reacting"** — [grounded, root cause
  of feedback #1/#3] `AudioToFractalMapper.Apply` (`Mapping/AudioToFractalMapper.cs:137`) drifts
  `_zoom` upward every frame unconditionally (`ZoomDriftPerSecond * (1f + frame.Rms)`, clamped only
  at the top, never pushed back down), and the Julia set's interior renders as a flat near-black
  colour (`shaders/fractal.frag:62-66`). So regardless of what's playing, the camera creeps into the
  set and the dark interior inevitably fills more of the screen over time — that *is* the "black
  silhouette gradually growing", and it is not audio-reactive, it's just a one-way clock. Fix: make
  zoom breathe (bounded oscillation, bass-driven in/out around a baseline) instead of a monotonic
  ramp, and re-center `u_center` on a boundary point instead of the fixed `(0,0)` so "zoom changes"
  reads as motion through the set rather than only "falling into a blob". · effort: small · risk:
  picking a center that stays near the boundary at all zoom levels needs care — sampling a boundary
  point each glide target (already have the waypoint list) is simplest
- **Shape barely moves because `c`'s perturbation is deliberately tiny/slow** — [grounded, root
  cause of feedback #3] `PerturbRadius = 0.05f` and `GlideSeconds = 1.0f`
  (`Mapping/AudioToFractalMapper.cs:41-42`) were chosen in PLAN.md specifically to be small/slow for
  robustness against dead-zone wandering — correct call at the time, but it means the actual Julia
  constant `c` moves very little next to how much the (previous item's) zoom dominates the frame.
  Once zoom stops being the dominant cue, widen `PerturbRadius` and/or drive the perturbation angle
  from more than just `SpectralCentroid01` (e.g. mix in a per-band term) so the boundary curve
  visibly morphs on its own. · effort: small · risk: widening the radius reintroduces the dead-zone
  risk PLAN.md flagged — re-clamp/re-tune by eye if it starts producing empty-disc or filled-blob
  frames
- **Palette hard-swaps instead of crossfading — that's the "not gradual" background** — [grounded,
  root cause of feedback #2/#4] `PaletteTexture.SetActivePalette` (`Graphics/PaletteTexture.cs:58-62`)
  re-uploads an entirely different 256-entry LUT instantly, and
  `AudioToFractalMapper.Apply` calls it on *every* `OnsetDetected` tick
  (`Mapping/AudioToFractalMapper.cs:95-97`) — so on top of the continuous RMS-driven phase rotation
  (which *is* gradual), the whole colour scheme can snap to something unrelated on every detected
  onset. Fix: crossfade — bind two palette texture units and blend by a `u_paletteBlend` uniform
  driven by the same kind of glide timer already used for `c`, ramped over ~0.5-1s instead of an
  instant swap. · effort: small-medium · risk: needs a second texture unit + a shader-side mix,
  small addition to `fractal.frag` and `PaletteTexture`
- **No tempo/beat-phase signal — only a promiscuous single onset detector — is why timing is "hard
  to tell"** — [grounded, root cause of feedback #5] `OnsetDetector`
  (`Audio/OnsetDetector.cs`) runs one full-spectrum adaptive-threshold flux detector, so it fires on
  *any* spectral transient — hi-hat, snare, vocal sibilance — as readily as a kick, and its only
  visible effect is the 1-second glide above, which smears the hit across roughly a beat's worth of
  time rather than landing sharply on it. This is exactly the Tier 2 gap: IDEAS.md already has
  "Tempo estimate + beat-phase-locked LFOs" and relies on bass specifically being the beat carrier.
  Concretely, pull forward a slice of Tier 2 rather than the whole item: (a) add a second,
  bass-band-only flux detector dedicated to "kick/beat" (the general one stays for
  palette/shape variety triggers); (b) shorten the discrete glide to a fast snap + visible decay
  (attack/release shape, not a flat 1s lerp) so a hit reads as landing *on* the beat, not drifting
  toward it; (c) full autocorrelation-based BPM/phase-lock (existing Tier 2 item) is the real fix for
  "anticipates the beat" but is bigger — do (a)/(b) first and see if that alone makes the sync
  legible. · effort: medium · risk: a bass-only detector can double-trigger on a sustained bass note
  rather than its transient — expect a tuning pass, same caveat PLAN.md already gave the existing
  detector
- **Next step for whoever picks this up**: this is a PLAN.md "Phase 9" (robustness pass was Phase 8,
  status was already "in-review" pending exactly this kind of human visual/audio check), not a new
  IDEAS.md Tier — update PLAN.md with a Phase 9 scoped to these four items before writing code, since
  the continuous/discrete mapping rule and file ownership (`AudioToFractalMapper` as the only writer)
  from the existing plan still apply and should stay enforced.

### Tier 0 — unblockers (do these first; everything else depends on them)

- **Fix `AllowUnsafeCode` → `AllowUnsafeBlocks` in the csproj** — [grounded] One-token fix, but without it
  no `unsafe`/pointer path into an `SDL_LockTexture` pixel buffer compiles, which silently pushes you into
  slow managed-copy rendering. · effort: small · risk: none
- **App skeleton: split `Program.cs` into `App`/`Window`/`Renderer`/`Clock` with a fixed-step update and a
  decoupled present** — [grounded] The current file is a flat script with no place to hang audio, shaders,
  presets or input. Every later idea doubles in cost without this seam. Keep vsync, but measure frame time
  so adaptive quality (below) has a signal. · effort: small · risk: none
- **GL context instead of `SDL_Renderer`, rendering a single fullscreen quad with a fragment shader**
  — [grounded] `SDL_CreateRenderer` gives you blits, not per-pixel math; real-time fractals need a pixel
  shader. Replace with `SDL_GL_CreateContext` + Silk.NET's OpenGL bindings (`Silk.NET.OpenGL` loads from an
  existing context, no windowing takeover). This is the single highest-leverage architectural decision in
  the project. · effort: medium · risk: `SDL_GL_SetAttribute` must run *before* window creation and the
  window needs `SDL_WINDOW_OPENGL`; Sayers' bundled SDL2 is an older 2.0.x, so stick to GL 3.3 core / 4.3
  if you want compute later
- **Hot-reload shaders from disk (`shaders/*.frag`, watch + recompile, keep last-good on error)**
  — [grounded] Fractal work is 95% parameter and formula tweaking. Without hot reload each visual
  experiment is a rebuild. Cheapest possible multiplier on all fractal/mapping work below.
  · effort: small · risk: shader compile errors must not kill the loop — needs a fallback program

### Tier 1 — the actual product (audio in, fractal out)

- **WASAPI loopback capture of the default render endpoint via `NAudio.Wasapi`
  (`WasapiLoopbackCapture`), on its own thread, into a lock-free ring buffer** — [grounded] This is the
  core requirement ("react to whatever's actually playing") and NAudio is the mainstream .NET 8 answer;
  NAudio 3 is Native-AOT-friendly. · effort: medium · risk: three real gotchas — (1) loopback raises **no**
  `DataAvailable` events during digital silence, so you need a silence timeout that decays features to zero
  rather than freezing; (2) you must accept the device's own `WaveFormat` (usually 48 kHz stereo IEEE
  float), so downmix/resample defensively; (3) another app holding the device in WASAPI **exclusive** mode
  makes loopback go silent
- **Audio feature block: Hann-windowed 2048-pt FFT, log-spaced bands (sub-bass/bass/low-mid/mid/
  high-mid/presence/air), RMS, peak, spectral centroid** — [grounded] Prior art converges hard on exactly
  this shape (2048 FFT, ~7 log bands, RMS + centroid, 30–60 analyses/sec). Centroid is the cheap
  "brightness" signal that makes palettes feel correct. · effort: medium · risk: FFT bin→band mapping and
  per-band normalisation are where visualizers usually feel wrong; plan to tune against the overlay below
- **Asymmetric envelope followers (fast attack / slow release) on every band** — [grounded] Raw FFT
  magnitudes are jittery; attack/release smoothing is the difference between "fractal vibrating at 60 Hz"
  and "fractal breathing with the track". Tiny code, enormous perceived-quality delta. · effort: small
  · risk: none
- **Spectral-flux onset detection with an adaptive threshold → discrete `beat` / `onset` pulses**
  — [grounded] Normalised spectral flux with a rolling adaptive threshold is the standard approach in the
  audio-reactive projects surveyed. Gives you *events*, not just levels. · effort: medium · risk: threshold
  tuning per genre; per-band flux (kick vs snare vs hat) is better but more work
- **An `AudioFrame` snapshot struct as the single contract between audio and render threads (double-buffered,
  one atomic swap per frame)** — [grounded] This is the integration seam: audio thread publishes, render
  thread consumes, shader uniforms are written from one place. Prevents the usual tangle of visualizer code
  reaching into FFT buffers. · effort: small · risk: none
- **Mandelbrot/Julia escape-time fragment shader with smooth (continuous) iteration colouring**
  — [grounded best first fractal] Single pass, zero memory, embarrassingly parallel, maps perfectly onto
  the fullscreen-quad shader above. Add derivative-based distance estimation for crisp boundaries instead
  of banded contours. · effort: medium · risk: `float` precision cracks around 1e-5 zoom — fine for v1,
  see deep-zoom item
- **Audio-driven Julia constant: animate `c` along a smooth path (bass energy → radius, spectral centroid →
  angle), with beat pulses nudging it to a new point** — [grounded as the highest-payoff single mapping]
  The Julia set's whole character changes with `c`, so one 2-float mapping gives you the entire
  "the visuals are listening" effect. Everything else is garnish on top of this. · effort: small (once the
  shader + feature bus exist) · risk: unconstrained `c` wanders into visually dead regions — clamp to a
  hand-picked region or a parametric curve near the Mandelbrot boundary
- **Palette system: 256-entry 1D LUT texture + Iñigo Quilez cosine palettes, with phase rotation driven by
  RMS and palette *switching* on beat** — [grounded] Colour is the cheapest dimension of visual variety and
  LUT swaps are near-free. Cosine palettes are 12 floats per palette, so dozens of presets cost nothing.
  · effort: small · risk: none
- **Separate continuous mappings from beat-triggered discrete mappings** — [grounded design guard] If every
  parameter is continuously modulated the result is mush. Continuous: zoom velocity, palette phase,
  rotation, colour-cycle speed. Discrete on beat/onset: Julia seed jump, symmetry fold count, palette index,
  formula variant. This single rule is most of what separates good visualizers from bad ones.
  · effort: small · risk: none

### Tier 2 — depth, variety, usability

- *(Done 2026-10-30: `Graphics/FeedbackBuffer.cs` + `shaders/feedback.frag` — max-composite,
  fixed fade, F toggles; the mapper drives the warp (zoom pushes on the beat pulse, rotation
  follows the band tilt). Fade/strength still need tuning by eye in a playtest.)*
  **Feedback / persistence buffer: ping-pong FBO with a per-frame zoom+rotate+warp of the previous frame**
  — [grounded, this is the Milkdrop/projectM core trick] libprojectM's whole aesthetic is "warp the previous
  frame, then composite". Adding this on top of *any* fractal shader turns static zooms into motion trails
  and makes beats smear into visible energy. Highest value-to-effort item in Tier 2 by a wide margin.
  · effort: medium · risk: needs FBO ping-pong + float texture; decay factor must be audio-independent or
  the frame saturates to white
- *(Mostly done 2026-10-21: text overlay via SDL_ttf — tempo/lock/confidence, tap-tempo
  comparison, beat phase, grid-beat vs raw-kick flashes, capture status, normalised band levels,
  Julia/zoom/palette state, frame time; D toggles. Graphical spectrum bars/flux-vs-threshold plot
  still open.)*
  **Debug/analysis overlay: spectrum bars, per-band envelopes, flux + threshold, detected BPM, FPS,
  capture status** — [grounded, and `SDL2_ttf` is already in `bin/`] You cannot tune audio mappings blind.
  Treat this as a development instrument, toggled with a key. · effort: small · risk: drawing it over a GL
  context means either an ImGui backend or a simple GL text/quad path (SDL_Renderer and GL don't mix)
- **Kaleidoscopic IFS / Kaliset / Apollonian folding shaders** — [grounded in Shadertoy prior art] Cheap
  (a dozen fold-and-scale iterations), and 2–3 parameters change their character dramatically — which is
  exactly what you want to hand to audio features. Better audio-reactivity-per-flop than Mandelbrot.
  · effort: medium · risk: easy to produce epilepsy-grade strobing; rate-limit parameter deltas
- **Keyboard VJ controls + borderless fullscreen (`SDL_WINDOW_FULLSCREEN_DESKTOP`) + display picker**
  — [grounded] Currently only SDL_QUIT is handled. Needed: fullscreen toggle, next/prev preset, next/prev
  palette, freeze/hold, manual override of one or two params, pan/zoom with mouse, hide cursor, panic-to-
  black. Makes the thing demoable on a second monitor. · effort: small · risk: none
- **JSON preset files (shader id + palette + mapping curves + feedback settings), with next/prev/random and
  beat-synced auto-advance** — [grounded] projectM's value is its preset library, not its renderer. Once
  presets are data, every mapping experiment is permanent instead of a recompile. · effort: medium
  · risk: schema churn early on; version the files
- *(Tempo-estimate half done 2026-10-21: `Audio/TempoTracker.cs`, exposing `TempoBpm`/`BeatPhase`
  on `AudioFrame` — see PLAN.md Phase 9 log. First phase-locked LFO done 2026-10-30: the zoom
  punch follows `BeatPhase` while locked. Further LFOs (rotation, palette phase) still open.)*
  **Tempo estimate (autocorrelation over the flux history) and beat-phase-locked parameter LFOs**
  — [grounded] Level-driven motion always lags the beat; phase-locked LFOs let zoom/rotation *anticipate* it,
  which reads as musicality rather than reaction. · effort: medium · risk: tempo estimation is unreliable on
  rubato/ambient material — needs a confidence value and graceful fallback to level-driven motion
- **Audio latency compensation (small configurable delay on the visual side so capture+FFT lag is cancelled)**
  — [grounded] Loopback capture + buffering + FFT window is tens of milliseconds; a one-slider delay buys
  visible tightness. · effort: small · risk: none
- **Adaptive quality: render fractal at a fraction of window resolution and upscale; auto-tune
  max-iterations against a frame-time budget** — [grounded] Vsync is already on, so you will silently drop
  to 30 fps instead of degrading gracefully. Deep zooms and attractor accumulation both need this escape
  valve. · effort: small · risk: resolution changes mid-feedback-chain cause visible pops
- **Output device / capture source picker (which render endpoint to loop back, plus a mic-input fallback)**
  — [grounded] Multi-output machines (speakers + HDMI + virtual cable) are the common case, and a mic mode
  is the universal fallback when exclusive-mode apps block loopback. · effort: small · risk: device
  hot-plug/default-change events need handling, or capture dies silently on a device switch

### Tier 3 — bigger swings

- **Flame fractals / de Jong / Clifford strange attractors via GPU point accumulation (additive blending
  into a float FBO, log-density tone mapping)** — [grounded; different renderer class] Visually the most
  distinct option from escape-time sets — organic, smoky, strongly tempo-expressive, because the attractor
  coefficients can be driven directly by band energies. Requires a second render path (instanced points +
  additive float FBO + histogram tone map), not just another fragment shader. · effort: large · risk:
  needs float render targets and careful exposure control; frame-coherent animation of flame variations is
  genuinely hard (temporal noise)
- **Formula morphing: animate the exponent/variant (`z^p`, Burning Ship, Phoenix, Nova) continuously, with
  tempo-synced crossfades between formulas** — [grounded] Near-free variety reusing the Tier 1 shader; a
  single uniform turns one fractal into a family. · effort: small-medium · risk: non-integer exponents are
  slower (`pow`/`log` per iteration) and some variants need different escape radii
- **Video/GIF export: deterministic offline render to PNG sequence, piped to ffmpeg, muxed with a
  simultaneously recorded loopback WAV** — [grounded as the right shape] Note the fork in the road: real-time
  screen capture (easy, lossy, drops frames) vs deterministic offline render at fixed timestep from a
  recorded audio file (much better output, requires the whole pipeline to be replayable from an audio
  buffer). Choose offline, and make the audio source pluggable (live loopback vs WAV file) early so this
  stays possible. · effort: medium-large · risk: requires ffmpeg on PATH or a bundled binary; needs the
  "audio source abstraction" decision made up front
- **ImGui.NET parameter-tweak panel (live mapping curves, per-band gains, palette editor, preset save)**
  — [speculative on effort] High value for authoring presets, but the Sayers SDL2 binding ships no ImGui
  backend, so you'd be wiring the SDL2+OpenGL3 backend yourself or hand-rolling a minimal panel.
  · effort: medium · risk: binding/backend plumbing; may be cheaper to keep the keyboard+overlay approach
- **Deep zoom beyond float/double limits: arbitrary-precision reference orbit computed on the CPU
  (`BigInteger`/double-double) + GPU perturbation iteration with rebasing, plus BLA for speed**
  — [grounded in prior art, but late] This is how every serious zoomer gets past ~1e-16; GLSL `double` is
  unreliable/absent on many drivers, so double-double in-shader is the practical route. Only worth it once
  "infinite audio-driven zoom" is the actual headline feature. · effort: large · risk: reference-orbit
  recomputation must happen off the render thread; glitch artefacts without rebasing; likely the single
  biggest complexity jump in the project
- **MIDI controller input for VJ knobs (NAudio.Midi is in the same package family)** — [speculative]
  Pleasant for live performance, irrelevant until presets and mappings are stable. · effort: medium
  · risk: none beyond scope creep
- **L-systems / dragon curve / Koch geometry modes** — [grounded but deliberately deprioritised] These are
  vertex/geometry workloads, not pixel workloads, so they fit this shader-centric architecture poorly and
  are weakly audio-reactive (branch angle is about the only interesting knob). Listed so it's a conscious
  skip, not an oversight. · effort: medium · risk: pulls the renderer in a second direction for low payoff

### Explicit non-goals (recorded to prevent wasted effort)

- **Vulkan / compute-shader rewrite** — [grounded] A GL 3.3 fragment shader on a fullscreen quad is already
  bandwidth-free and fully parallel; escape-time fractals gain nothing from compute. Revisit only for flame
  accumulation (where GL 4.3 compute, not Vulkan, is the answer).
- **Wrapping libprojectM instead of building this** — [grounded] projectM is Milkdrop-preset-driven and
  OpenGL-based, with no official C#/.NET bindings found. Great reference for *techniques* (feedback warp,
  beat detection, preset model), wrong dependency for a fractal-specific app.
- **Playing music from a file via SDL2_mixer** — [grounded] `SDL2_mixer` natives are in `bin/` and it's a
  tempting shortcut, but the stated goal is reacting to whatever the system is already playing. Keep
  file playback only as a *test fixture* behind the audio-source abstraction.

## Done

All of Tier 0 and Tier 1 below were implemented via `PLAN.md` ("Implement Tier 1 ... folding in the
Tier 0 unblockers", phases 0-8, status `in-review` as of 2026-10-21) — this list was stale (still
said "nothing yet") until this pass:

- Tier 0: `AllowUnsafeCode` → `AllowUnsafeBlocks` fix; `App`/`Window`/`Clock` skeleton; GL context +
  fullscreen-quad fragment-shader pipeline (`Graphics/`); shader hot-reload with last-good fallback
  (`Graphics/ShaderHotReloader.cs`).
- Tier 1: WASAPI loopback capture (`Audio/AudioCapture.cs`); FFT + 7-band + RMS/peak/centroid
  analysis (`Audio/AudioAnalyzer.cs`); envelope followers (`Audio/EnvelopeFollower.cs`);
  spectral-flux onset detection (`Audio/OnsetDetector.cs`); the `AudioFrame`/`AudioFrameBus`
  double-buffer contract; Julia escape-time shader with smooth colouring + distance-estimate
  boundary (`shaders/fractal.frag`); audio-driven Julia `c` via a curated-waypoint mapping instead
  of the literal unconstrained radius/angle IDEAS.md originally proposed (a deliberate, documented
  refinement — see PLAN.md "Julia mapping"); IQ cosine palette LUT with phase rotation + discrete
  switching (`Graphics/PaletteTexture.cs`); the continuous-vs-discrete mapping rule, enforced solely
  in `Mapping/AudioToFractalMapper.cs`.

First real human playtest against this implementation (2026-10-21) surfaced the issues now at the
top of Open as "Tier 1 fixes" — PLAN.md's own acceptance checklist had several items marked
unverified pending exactly this kind of check, so this is the expected next loop, not a regression.

## Change log
- 2026-10-21 [feature-scout] Created IDEAS.md. Added 31 ideas across 4 tiers + 3 explicit non-goals /
  updated 0 / marked 0 done. Also flagged one existing bug: invalid `<AllowUnsafeCode>` property in
  `SDL_Demo.csproj`.
- 2026-10-21 [session] Updated Done section to reflect that PLAN.md phases 0-8 (all of Tier 0 and
  Tier 1) are actually implemented — the file had gone stale. Added a new "Tier 1 fixes" section at
  the top of Open, grounded directly in the current code, from the user's first real playtest:
  monotonic zoom drift reads as "growing silhouette" instead of audio reactivity
  (`Mapping/AudioToFractalMapper.cs:137`); `c`'s perturbation is too small/slow to read as shape
  change (`:41-42`); palette switches are an instant hard-swap instead of a crossfade
  (`Graphics/PaletteTexture.cs:58-62`, called from `:95-97`); there is no beat-specific (vs.
  general-transient) onset signal, which is why beat-sync is hard to perceive
  (`Audio/OnsetDetector.cs`). Recommended next step: a PLAN.md "Phase 9" scoped to these four items.
- 2026-10-21 [worker] Implemented the four "Tier 1 fixes" as PLAN.md Phase 9; marked them
  awaiting re-playtest (line numbers cited above refer to the pre-Phase-9 code).
- 2026-10-30 [worker] Project renamed `SDL_Demo` -> `fractal-audio`. Tier 1 fix #4: discrete
  changes and the zoom punch now follow the tempo grid while locked (see PLAN.md change log).
- 2026-10-30 [worker] Tier 1 fix #4: palette crossfades also last whole beats while locked —
  the last off-grid discrete timing. Remaining step is the playtest.
- 2026-10-30 [worker] Tier 2: feedback buffer (constant warp, F toggle).
- 2026-10-30 [worker] Tier 2: feedback warp driven by the mapper (beat pulse, band tilt).
