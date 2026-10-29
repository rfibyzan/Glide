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
| v0.1.1 | Memory gate harness | planned |
| v0.2.0 | Window, global hotkey, search over a snapshot | planned |
| v0.3.0 | NTFS USN index for whole drives | planned |
| v0.4.0 | Actions, calculator, bookmarks, history | planned |
| v0.5.0 | Quick switch in file dialogs | planned |

Nothing in this repository runs as a Windows shell extension yet. The core is a
plain library and can be exercised from tests.

## Build

```
dotnet build Glide.sln
dotnet test Glide.sln
```

Requires the .NET 8 SDK. The app project added in v0.2.0 will publish with
NativeAOT, which needs the MSVC build tools on the machine that publishes.

## Design

- Single 64-bit process. No WPF, no WebView2, no gRPC, no telemetry.
- UI drawn directly with Win32 and Direct2D, one UI thread.
- One low-level keyboard hook covers 32-bit and 64-bit applications.
- Index stored in a memory-mapped file; entries are not kept on the heap.
- Ranking is a pure function, documented in `docs/ranking.md`.

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
