# Beta stress baseline

_Measured 2026-09-22 on the development Windows x64 host with a Release build. Timing varies by CPU, GPU/backend, storage and background load; these values are a comparison baseline, not universal requirements._

The headless runner models the current live-preview path: it paints a sinusoidal stroke, flattens the document after every pointer update, commits one undoable stroke, atomically saves a `.comp`, reopens it, and requires exact rendered-pixel equality.

| Profile | Canvas / brush | Updates | Preview median | Preview p95 | Commit | Save | Reopen | Working set | Result |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| Quick | 2048² / 400 px | 40 | 3.83 ms | 4.84 ms | 4.64 ms | 395 ms | 19 ms | 116 MiB | Pixel round-trip passed |
| Full | 4096² / 800 px | 80 | 17.26 ms | 20.37 ms | 10.27 ms | 1.42 s | 66 ms | 347 MiB | Pixel round-trip passed |

Run locally:

```powershell
.\scripts\stress.ps1 -Profile quick -Output .\artifacts\stress-quick.json
.\scripts\stress.ps1 -Profile full -Output .\artifacts\stress-full.json
```

The quick profile runs in x64 CI. The full profile is a beta-candidate/manual gate. The `0.9.0-beta.1` candidate measured a 15.23 ms median and 16.64 ms p95 preview, 1.45 s save, 63 ms reopen, and 350 MiB working set with exact pixel round-trip. Broader hardware results will determine whether region/tile preview invalidation becomes a beta blocker. Corruption, exceptions, unbounded growth across repeated runs, or a substantial regression from this baseline is always blocking.
