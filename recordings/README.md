# Tempo recordings

A test set for the beat tracker: 19 commercial tracks with known BPMs, recorded from real
playback. `tools/TempoReplay` replays them through the tracker and scores its tempo against the
true BPM, so tracker changes can be checked without replaying the music.

## What's committed: `signals/`

`signals/*.tsig` hold only the two values per 20ms frame that the tempo tracker reads (onset
strength and bass flux), with the true BPM in each file's header — about 100 KB per track, and
not audio. Score the whole set:

```powershell
dotnet run --project tools/TempoReplay -- recordings/signals
```

Add `--timeline` for each track's tempo over time, or `--trace=FROM-TO` (seconds) for the
tracker's decision steps. The tool exits non-zero if any track scores below 90% — current
scores are in PLAN.md "Tempo test tracks".

These files test `Audio/TempoTracker.cs` only: they are the *output* of the spectrum analysis,
so a change to how the onset signals are computed (`Audio/BeatPipeline.cs`, band layout, FFT)
needs the full recordings below — record the tracks yourself.

## What stays local: spectrum dumps (`*.csv`)

The app writes the full FFT magnitudes of every frame when `FRACTAL_AUDIO_DUMP` is set. Those
are tens of MB per track and close enough to the music to resynthesise a recognisable excerpt,
so they are git-ignored and never published. Record about 60 seconds of a track, from the repo
root, with playback already running:

```powershell
$env:FRACTAL_AUDIO_DUMP = "recordings\catch.csv"; dotnet run --project FractalAudio.csproj
```

(or use the "record tempo dump" launch configuration in VS Code). Replay dumps with their true
BPM, and export them to signals files for committing:

```powershell
dotnet run --project tools/TempoReplay -- recordings\catch.csv=130 recordings\disco.csv=97
dotnet run --project tools/TempoReplay -- recordings\catch.csv=130 --export=recordings/signals
```

A signals file replays to exactly the same scores as the dump it came from.

## Tracks

| File | Track | BPM |
| --- | --- | --- |
| `catch.csv` | Kosheen — Catch | 130 |
| `klf.csv` | KLF — What Time Is Love | 120 |
| `disco.csv` | Warpaint — Disco | 97 |
| `overdrive.csv` | Lazerhawk — Overdrive | 133 |
| `png.csv` | Future Sound of London — Papua New Guinea | 125 |
| `teardrop.csv` | Massive Attack — Teardrop | 77 |
| `hyperspeed.csv` | The Prodigy — Hyperspeed | 150 |
| `diesel.csv` | The Prodigy — Diesel Power | 106 |
| `learningtofly.csv` | Maceo Plex - Learning to fly  | 122 |
| `dark.csv` | Tiefundton - The Dark  | 145 |
| `researchchemicals.csv` | Viagra Boys - Research Chemicals  | 137 |
| `blacksheep.csv` | Scott Pilgrim Vs. The World - Black Sheep  | 124 |
| `anvil.csv` | LORN - ANVIL  | 100 |
| `acidrain.csv` | Lorn - Acid Rain  | 56 |
| `starburster.csv` | Fontaines D.C. - Starburster | 98 |
| `heyboyheygirl.csv` | The Chemical Brothers - Hey Boy Hey Girl | 127 |
| `reflektor.csv` | Arcade Fire - Reflektor | 112 |
| `gosh.csv` | Jamie xx - Gosh | 139 |
| `kissme.csv` | Artemas - i like the way you kiss me | 152 |

The signals file for each is `signals/<name>.tsig`. Recordings start wherever playback was when
recording began, not necessarily at the start of the track.
