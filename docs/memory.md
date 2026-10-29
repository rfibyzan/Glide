# Memory

Memory is the constraint that shapes the architecture, so it is measured, not
estimated.

## Baseline: Listary 6.3.5.94

Measured 2026-10-29 on this machine while Listary was running normally.

| Process | Working set | Private | Threads |
|---|---:|---:|---:|
| Listary.exe (WPF, 32-bit) | 389.9 MB | 344.7 MB | 92 |
| Listary.Service.exe | 20.2 MB | 16.1 MB | 5 |
| ListaryHookHost64.exe | 7.9 MB | 1.3 MB | 1 |
| ListaryHookHost32.exe | 8.5 MB | 1.3 MB | 1 |
| **Total** | **426.5 MB** | **363.4 MB** | |

`listary-core.exe` and `listary_engine.dll` were not resident at the time, so
indexing memory is not part of that number.

## Glide floor: v0.1.1

Native-AOT publish of `src/Glide.App`, no UI, idle for 5 seconds, 25 samples of
working set every 200 ms:

| Metric | Value |
|---|---:|
| Average | 8.0 MB |
| p95 | 8.3 MB |
| Max | 8.3 MB |

This is the runtime floor: GC, thread pool, and the loaded image. It is not yet
comparable to the baseline table above because no UI or index exists. It does
show that the chosen stack costs a fraction of the replaced UI process.

## Gate

```
dotnet publish src\Glide.App\Glide.App.csproj -c Release -r win-x64 -o artifacts\publish
powershell -File scripts\measure-memory.ps1 -AppArgs "--snapshot artifacts\bench-100k.txt" -Duration 8
powershell -File scripts\ui-smoke.ps1
```

The script samples `WorkingSet64` and exits non-zero when p95 exceeds the budget.
`-Duration` stops sampling after N seconds, which is how the resident launcher is
measured. Budget for v0.2.0 and later: 50 MB idle. Raise it only with a recorded reason.

## UI, v0.3.0

| Case | Working set |
|---|---:|
| Hidden, 100k snapshot entries | 33.8 MB |
| Window shown, 100k snapshot entries | 38.5 MB |
| Folder scan, 75k profile entries | 43.8 MB |
| Folder scan, 200k profile entries | 88.2 MB, rejected |

The 200k case is why the scan is capped. A path string on the managed heap costs
about 390 bytes per real entry, so the cap is a budget decision, not a product
decision.

## Index, v0.4.0

The same 100k entries, now behind an index file in `%ProgramData%\Glide`:

| Case | Working set |
|---|---:|
| Window shown, 100k index, typing `report` | 18.4 MB |
| Whole process, 1M index, 600 queries | 36.8 MB average, 39.2 MB peak |

The launcher keeps no entries of its own. The 18.4 MB is the window, the fonts
and brushes, and one 128 KB read buffer per worker; the strings are the 50 rows
on screen. The 1M row is the same benchmark loop the latency numbers come from,
which is the worst case: it never lets the process settle between queries.

`scripts/ui-smoke.ps1 -AppArgs "--index artifacts\bench-100k.gidx"` is the run
behind the first row, and it also asserts the window still shows, focuses, hides,
and reopens with an index behind it.

