# Tempo recordings

`FRACTAL_AUDIO_DUMP` recordings of the known-BPM test tracks (PLAN.md "Tempo test tracks").
The dumps are tens of MB each, so everything here except this file is git-ignored — they live
only on the machine that recorded them.

Record about 60 seconds of each track, from the repo root, with playback already running:

```powershell
$env:FRACTAL_AUDIO_DUMP = "recordings\catch.csv"; dotnet run --project FractalAudio.csproj
```

Use these names, so the replay command below works as-is:

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

Score them all against the current tracker (skip any you haven't recorded):

```powershell
dotnet run --project tools/TempoReplay -- recordings\catch.csv=130 recordings\klf.csv=120 recordings\disco.csv=97 recordings\overdrive.csv=133 recordings\png.csv=125 recordings\teardrop.csv=77 recordings\hyperspeed.csv=150 recordings\diesel.csv=106
```
