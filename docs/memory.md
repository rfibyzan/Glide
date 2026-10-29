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
powershell -File scripts\measure-memory.ps1
```

The script samples `WorkingSet64` and exits non-zero when p95 exceeds the budget.
Budget for v0.2.0 and later: 50 MB idle. Raise it only with a recorded reason.
