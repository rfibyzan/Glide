# Glide

A Windows launcher: instant file search, a whole-drive index, actions on results,
calculator and bookmarks, and quick switch inside file dialogs.

Glide is a clean reimplementation focused on one measurable goal: stay far below
the memory footprint of the launcher it replaces. On this machine Listary 6.3.5.94
idles at 426 MB working set, 389 MB of it in the WPF UI process. Glide targets
under 50 MB idle as a single native-AOT process.

## Status

| Version | Scope | State |
|---|---|---|
| v0.1.0 | Search core: query parsing, fuzzy matching, ranking | done, 30 tests |
| v0.1.1 | Native-AOT shell and memory gate | done, 8.3 MB p95 idle |
| v0.2.0 | Snapshot store, latency and memory gates | done, 5.2 ms p95, 32.9 MB with 100k entries |
| v0.3.0 | Window, global hotkey, folder scan | done, 38.5 MB with 100k entries shown, UI smoke green |
| v0.4.0 | NTFS USN index for whole drives | next |
| v0.5.0 | Actions, calculator, bookmarks, history | planned |
| v0.6.0 | Quick switch in file dialogs | planned |

One window, one hotkey, real search. Typing filters the loaded entries, arrow
keys move the selection, Enter opens the result, Escape hides the window, and
losing focus hides it too. `--scan` walks a folder in the background so the
window is usable while indexing.

The v0.3 folder scan is a stopgap: every entry is a path string on the managed
heap, which costs about 390 bytes per entry on real profile paths. The default
cap is 75,000 entries (43.8 MB measured), and a 200,000 entry profile scan reaches
88 MB, over the budget. The USN index in v0.4 replaces the strings with a compact
memory-mapped store, which is the point at which a whole-drive index fits.

Nothing in this repository runs as a Windows shell extension yet. The core is a
plain library and can be exercised from tests.

## Usage

```
glide                            run the launcher (Ctrl+Alt+Space toggles it)
glide --show                     open the window, starting the app if needed
glide --scan <folder>            index a folder instead of the user profile
glide --max-entries <n>          cap the scanned entries (default 75000)
glide --snapshot <file>          load a path-per-line snapshot instead of scanning
glide --snapshot <file> --bench  measure query latency on the snapshot
```

## Build

```
dotnet build Glide.sln
dotnet test Glide.sln
dotnet publish src\Glide.App\Glide.App.csproj -c Release -r win-x64 -o artifacts\publish
powershell -File scripts\ui-smoke.ps1
```

Requires the .NET 8 SDK. NativeAOT publishing needs the MSVC build tools on the
machine that publishes.

## Design

- Single 64-bit process. No WPF, no WebView2, no gRPC, no telemetry.
- One popup window custom-drawn with Win32 and GDI, one UI thread, no child controls.
- One global hotkey (Ctrl+Alt+Space) registered with `RegisterHotKey`; no keyboard hook.
- The v0.4 index will live in a memory-mapped file; until then entries are on the heap.
- Ranking is a pure function, documented in `docs/ranking.md`.
- The memory baseline and the gate are documented in `docs/memory.md`.
- Latency and memory measurements are recorded in `docs/performance.md`.

## Ranking in one paragraph

A query is split into terms. Every term must match either the file name or the
full path. A name match outranks a path-only match, consecutive characters beat
scattered ones, word starts beat mid-word, and shorter names win ties before
path order does. The exact point values are in `docs/ranking.md` and are covered
by tests.

## Non-goals

- Loading or redistributing code, icons, or localization files from Listary.
- Bypassing licensing or integrity checks of any installed application.
- Plugin compatibility with Listary plugins. Glide will define its own surface.
