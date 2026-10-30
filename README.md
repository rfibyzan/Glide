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
| v0.4.0 | NTFS USN index for whole drives | done, 18.4 MB with a 100k index, 19 ms p50 at 1M |
| v0.5.0 | Dark mode, configurable hotkey, actions, calculator, bookmarks, history | done, 19.5 MB with a 100k index, UI smoke 6/6 in both themes |
| v0.6.0 | Quick switch in file dialogs | planned |

One window, one hotkey, real search. Typing filters the loaded entries, arrow
keys move the selection, Enter opens the result, Escape hides the window, and
losing focus hides it too. `--scan` walks a folder in the background so the
window is usable while indexing.

The v0.3 folder scan is a stopgap: every entry is a path string on the managed
heap, which costs about 390 bytes per entry on real profile paths. The default
cap is 75,000 entries (43.8 MB measured), and a 200,000 entry profile scan reaches
88 MB, over the budget.

v0.4.0 replaces that with an index file per volume. A small service reads each
volume's USN journal without a UAC prompt, writes a compact index, and rebuilds
it a few seconds after the journal moves. The launcher streams the file during a
query: it decodes only the 50 rows that survive, so memory no longer grows with
the number of indexed files. See `docs/index.md`.

### Keys

| Key | Action |
|---|---|
| `Enter` | open the selected result |
| `Ctrl+Enter` | open its containing folder |
| `Shift+Enter` or `Ctrl+C` | copy its full path |
| `F2`, the context-menu key, or right click | actions menu: open, open folder, copy path, bookmark, properties |
| `Up` / `Down` | move the selection |
| `Escape` | hide the window |
| `Ctrl+V` | paste a path into the query |

### Calculator

A query that starts with a digit, `-`, `(` or `=` is evaluated as arithmetic:
`2+3*4` shows `14` as the first row, and Enter copies it. The parser is a small
recursive-descent routine in `src/Glide.Core/Calculator.cs`; no scripting engine
and no `DataTable.Compute`.

### Bookmarks and history

Bookmarked paths are matched ahead of file results and labelled `Bookmark`. Add
or remove one from the actions menu. Opened paths land in history (50 entries,
most recent first) and are offered when the query is empty. Both files live next
to the settings, in `%APPDATA%\Glide`.

## Settings

`%APPDATA%\Glide\settings.json` is written with the defaults on first run:

```json
{
  "hotkey": { "modifiers": ["Ctrl", "Shift", "Alt"], "key": "/" },
  "theme": "system",
  "maxResults": 8
}
```

- `theme` is `system`, `dark` or `light`; `system` follows `AppsUseLightTheme`
  and re-reads it on `WM_SETTINGCHANGE`.
- `maxResults` is the number of rows drawn, clamped to 1–8. It no longer doubles
  as the scan cap; that is `--max-entries`.
- `hotkey` is modifiers plus a key character, so `/` resolves to the right
  virtual-key code for the layout.
- If the shortcut is already claimed by another application, Glide keeps running
  on `Ctrl+Alt+Space` and paints a one-line notice in the window instead of
  failing silently.
- A missing or malformed file is logged to stderr and replaced by the defaults;
  it never blocks startup.

Nothing in this repository runs as a Windows shell extension yet. The core is a
plain library and can be exercised from tests.

## Usage

```
glide                            run the launcher (Ctrl+Shift+Alt+/ by default)
glide --show                     open the window, starting the app if needed
glide --hotkey <spec>            override the shortcut, e.g. Ctrl+Shift+Alt+/
glide --theme <system|dark|light> override the theme for this run
glide --index <path>             search index files from the service (file or folder)
glide --no-index                 ignore index files and scan the profile instead
glide --scan <folder>            index a folder instead of the user profile
glide --max-entries <n>          cap the scanned entries (default 75000)
glide --snapshot <file>          load a path-per-line snapshot instead of scanning
glide --snapshot <file> --bench  measure query latency on the snapshot
glide --index-bench <index>      measure query latency on an index file
```

Without arguments the launcher picks up `index-*.gidx` files from
`%ProgramData%\Glide`, so a machine with the service installed needs no flags.

## Service

Reading a volume's USN journal needs administrator rights. Installing the service
once means the index stays current without an elevation prompt at every start:

```
dotnet publish src\Glide.Service\Glide.Service.csproj -c Release -r win-x64 -o artifacts\publish\service
artifacts\publish\service\glide-service.exe --install     (elevated)
```

`--build` writes the indexes once and exits, and `--console` runs the same loop in
the foreground for debugging. `--uninstall` removes the service and leaves the
index files in `%ProgramData%\Glide`. Both need an elevated prompt.

## Build

```
dotnet build Glide.sln
dotnet test Glide.sln
dotnet publish src\Glide.App\Glide.App.csproj -c Release -r win-x64 -o artifacts\publish
dotnet publish src\Glide.Service\Glide.Service.csproj -c Release -r win-x64 -o artifacts\publish\service
powershell -File scripts\ui-smoke.ps1 -AppArgs "--index artifacts\bench-100k.gidx --hotkey Ctrl+Shift+Alt+F9 --theme dark" -Hotkey "Ctrl+Shift+Alt+F9"
```

`ui-smoke.ps1` presses the shortcut in `-Hotkey`, so it must match whatever the
app was started with. Pick a combination that is free on the machine; the script
waits 800 ms after the window appears so the first press does not race
`RegisterHotKey`.

Requires the .NET 8 SDK. NativeAOT publishing needs the MSVC build tools on the
machine that publishes.

## Design

- Single 64-bit process. No WPF, no WebView2, no gRPC, no telemetry.
- One popup window custom-drawn with Win32 and GDI, one UI thread, no child controls.
  Dark and light are two color sets over the same paint code; no control restyle.
- One global hotkey, `Ctrl+Shift+Alt+/` by default, registered with
  `RegisterHotKey`; no keyboard hook. It is read from `settings.json` and can be
  overridden per run with `--hotkey`.
- The index is a file that is streamed per query, not a heap graph; a million
  entries occupy 50 MB on disk and none of the launcher's heap.
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
