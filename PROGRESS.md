# Glide: Progress Handoff

Snapshot taken 2026-10-30, after v0.5.0.

| | |
|---|---|
| Repository | `D:\Kodingan\GitHub\Glide` (moved here from `D:\Glide`) |
| Remote | `https://github.com/rfibyzan/Glide.git`, branch `main` |
| HEAD | `b95a321` plus the v0.5.0 working tree |
| Tests | 122/122 passing (`dotnet test Glide.sln -c Release`) |
| Build | .NET 8, NativeAOT, single 64-bit process, no WPF/WebView2 |

Goal: a Listary-shaped launcher that stays under **50 MB** idle. Reference point
on this machine: Listary 6.3.5.94 idles at 426 MB working set, 389 MB of it in a
WPF UI process.

---

## 1. What is done

| Version | Scope | Evidence |
|---|---|---|
| v0.1.0 | Query parser, fuzzy matcher, ranking | 30 tests |
| v0.1.1 | Native-AOT shell, memory gate | 8.3 MB p95 idle |
| v0.2.0 | Snapshot store, parallel scan, latency gate | 5.2 ms p95 on 100k, 32.9 MB |
| v0.3.0 | Popup window, global hotkey, folder scan | 38.5 MB with 100k shown, UI smoke 6/6 |
| v0.4.0 | Whole-drive NTFS USN index + LocalSystem service | 19.2 ms p50 at 1M, 18.4 MB with index loaded |
| v0.5.0 | Settings, dark mode, configurable hotkey, result actions, calculator, bookmarks, history | 19.5 MB with 100k index, UI smoke 6/6 in both themes |

### 1.1 Search core (v0.1.0)

- Query is split into terms; every term must match the file name or the full path.
- Scoring is a pure function shared by both backends, documented in
  `docs/ranking.md`: name match beats path-only, consecutive beats scattered,
  word starts beat mid-word, shorter names win ties.
- `EntryScorer` holds tier bonuses and the compare order; char and byte overloads
  are asserted to produce identical output.

### 1.2 Launcher window (v0.3.0)

- One topmost popup, custom-drawn with Win32 + GDI, one UI thread, no child
  controls, no XAML.
- Global hotkey via `RegisterHotKey`, no keyboard hook.
- Type to filter, arrows move selection, Enter opens, Escape hides, focus loss
  hides.
- Two real-desktop bugs found and fixed via `scripts/ui-smoke.ps1`:
  `SWP_SHOWWINDOW` activating before state was set (broke auto-hide), and
  unconditional `AttachThreadInput` suppressing `WM_ACTIVATE` (broke deactivate).
  Hiding is additionally guarded by a 400 ms foreground-recheck timer.

### 1.3 Folder scan (v0.3.0, stopgap)

Enumeration works, but every entry is a managed string at ~390 bytes on real
profile paths, so the default cap is 75,000 entries (43.8 MB). 200k reaches
88 MB. This is the reason v0.4.0 exists.

### 1.4 On-disk index (v0.4.0)

Format v2, magic `GLIDX002`:

```
header  40 bytes
record  [flags:1][UTF-8 path][NUL]
flags   0x01 = directory, 0x02 = ASCII-only path
```

- 50 bytes per entry for 50-byte paths; a 1M-entry index is 49,967,481 bytes and
  builds in 83 ms.
- `IndexSearchSource` streams the file with `RandomAccess`, one handle per worker,
  up to 8 workers, 128 KB buffer each, per-worker top list merged with the shared
  comparator. `SearchMany` merges several volumes.
- Memory does not scale with index size: the launcher holds only the winning rows.
  600 queries over a 1M index: 36.8 MB average, 39.2 MB peak.
- The ASCII bit removes a per-query `Ascii.IsValid` pass over every path.

Three changes took the 1M query from 38 ms to 22 ms: byte-level scoring for ASCII
paths, the stored ASCII bit, and vectorized `IndexOfAny` against the folded byte
plus its uppercase twin instead of a scalar fold loop.

### 1.5 USN indexer and service (v0.4.0)

Runtime layout (`src/Glide.Core/Index/Usn`):

| File | Role |
|---|---|
| `UsnVolume.cs` | opens `\\.\X:`, `FSCTL_ENUM_USN_DATA` / `QUERY_USN_JOURNAL` / `READ_USN_JOURNAL` |
| `UsnRecordParser.cs` | parses `USN_RECORD_V2`, skips v3/v4 |
| `UsnPathResolver.cs` | FRN → path by walking parent records, memoized directories, cycle/orphan handling, metadata skip, `RootFileId = 5` |
| `IndexBuilder.cs` | enumerate → resolve → write, returns `IndexBuildResult` |

`src/Glide.Service` (`glide-service.exe`, NativeAOT, LocalSystem):

- `ServiceHost.cs`: SCM dispatcher through P/Invoke with `[Stdcall]` function
  pointers, so the service runs without the hosting package.
- `IndexWorker.cs`: polls every 3 s, waits for 5 s of journal quiet, rebuilds the
  volume's index, retries after 30 s on failure. Full rebuild, not incremental
  replay, in this version.
- `ServiceInstaller.cs`: `sc.exe` create/delete; `--build` for a one-shot index;
  `--console` for foreground debugging.
- Output: `%ProgramData%\Glide\index-<letter>.gidx`, which already inherits
  `BUILTIN\Users` read/execute, so the launcher reads it without elevation.

The launcher auto-discovers `index-*.gidx` in `%ProgramData%\Glide`
(`Search/IndexLocator.cs`) and falls back to a folder scan when none exists.
`--index <path>` and `--no-index` override.

### 1.6 Settings, theme, hotkey (v0.5.0)

`%APPDATA%\Glide\settings.json`, written with defaults on first run by
`SettingsManager.LoadOrCreate()`:

```json
{
  "hotkey": { "modifiers": ["Ctrl", "Shift", "Alt"], "key": "/" },
  "theme": "system",
  "maxResults": 8
}
```

- `SettingsManager.Load` clamps unknown themes to `system`, restores missing
  hotkey fields, and turns `maxResults <= 0` back into 8. A malformed file logs
  to stderr and falls back to defaults; it never blocks startup. Saves go to a
  temp file and are moved over the target.
- `HotkeyParser` (in `Glide.Core.Settings`, so tests no longer reference the App)
  converts the spec to `RegisterHotKey` modifiers and a virtual key. It rejects
  unknown modifiers and unknown keys, and rejects an unmodified letter so a
  stray `"g"` cannot swallow typing desktop-wide. Bare function keys are allowed.
- The key is stored as the character the user types, so `/` maps to `VK_OEM_2`
  and a user on another layout can pick that layout's key instead.
- Failure path: if the combo is taken, Glide registers `Ctrl+Alt+Space` instead,
  logs one line to stderr, and paints a teal notice row above the results. The
  first attempt used a modal `MessageBox`, which blocked the message loop and
  made the launcher look frozen during the smoke run.
- `--hotkey <spec>` and `--theme <system|dark|light>` override the file for one
  run. `--max-entries` is back as the scan cap; `maxResults` is only the number
  of rows drawn, clamped 1..8.

Theme: light and dark are two color sets consumed by the same paint code.
`theme: "system"` reads `AppsUseLightTheme` under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize` and re-reads
it on `WM_SETTINGCHANGE`, so a live switch repaints. Placeholder contrast was
corrected in both themes after inspecting the screenshots
(light `0x8A8A8A` → `0x767676`, dark `0x757575` → `0x8A8A8A`).

### 1.7 Actions, calculator, bookmarks, history (v0.5.0)

- `Enter` opens, `Ctrl+Enter` opens the containing folder, `Shift+Enter` and
  `Ctrl+C` copy the full path, `F2` / the context-menu key / right click opens the
  actions menu (open, open folder, copy path, add/remove bookmark, properties).
- Calculator: `src/Glide.Core/Calculator.cs`, recursive descent over `+ - * /`,
  parentheses, unary minus, decimals. Triggered when the query starts with a
  digit, `-`, `(` or `=`; the result is the first row and Enter copies it.
  Division by zero reports `Cannot divide by zero` rather than failing.
- `BookmarkStore` and `HistoryStore` replaced `BookmarkManager` / `HistoryManager`.
  Both cache their JSON array in memory (the query path reads them on every
  keystroke), dedupe case-insensitively, and cap at 50 entries. `BookmarkStore`
  writes; `HistoryStore` records every opened path.
- Bookmarks merge ahead of file results and are labelled from
  `SearchResultKind.Bookmark`, not from score. The old code inferred the label
  from `Score >= 2000`, which mislabelled high-scoring index rows. History items
  are `SearchResultKind.History`.
- Bookmark add/remove lives in the actions menu. There is no `Ctrl+B` binding.

---

## 2. Measured numbers

Published native-AOT binary, this machine.

### Latency

| Case | p50 | p95 | p99 | Budget |
|---|---:|---:|---:|---|
| 1M entries, `report` | 19.2 ms | 22.6 ms | 25.0 ms | p95 < 30 ms |
| 1M entries, `project1` | 21.5 ms | 24.1 ms | 26.2 ms | |
| 1M entries, `users documents` | 22.7 ms | 25.8 ms | 27.4 ms | |
| 1M entries, `zzzzzz` (no match) | 9.8 ms | 10.8 ms | 11.3 ms | |
| 100k entries, `report` | 3.4 ms | 4.2 ms | — | p95 < 10 ms |

Median p95 of five runs of 60 queries after 15 warmups.

### Memory

| Case | Working set |
|---|---:|
| Idle, hidden, no index | 8.3 MB p95 |
| 100k index loaded, hidden | 18.4 MB (v0.4.0) |
| 100k index shown, dark theme | 19.5 MB (v0.5.0) |
| 100k index shown, light theme | 19.5 MB (v0.5.0) |
| Querying a 1M index, 600 queries | 36.8 MB avg / 39.2 MB peak |
| v0.3 folder scan, 100k entries | 53.2 MB (over budget, hence the cap) |

### Speed

- 50 MB index read: 16 ms with one handle, 9 ms with one handle per worker.
- Index build: 1M entries in 83 ms.

Everything is reproducible with `scripts/bench-query.ps1`,
`scripts/measure-memory.ps1`, and `glide --index-bench`.

---

## 3. Verified on a real desktop

`scripts/ui-smoke.ps1` drives the published binary end to end: it presses the
shortcut given in `-Hotkey`, checks the window takes foreground, posts
keystrokes, screenshots the result, revokes focus and checks the window hides,
reopens, and closes with Escape.

| Run | Result |
|---|---|
| `--index artifacts\bench-100k.gidx --hotkey Ctrl+Shift+Alt+F9 --theme dark` | 6/6, 19.5 MB, `artifacts/ui-dark.png` |
| same with `--theme light` | 6/6, 19.5 MB, `artifacts/ui-light.png` |
| no `--hotkey`, default taken by Listary | notice row, `artifacts/ui-notice.png` |

Both theme screenshots show real painted result rows and highlighted matches,
not a blank window.

---

## 4. Not verified yet

1. **The default shortcut on a machine where it is free.** `Ctrl+Shift+Alt+/`
   returns Win32 1409 on this machine while Listary (`Listary.exe`,
   `Listary.Service.exe`, `ListaryHookHost32/64.exe`) is running. Close or rebind
   Listary, then run `glide` with no `--hotkey` and confirm the hotkey works.
2. **Live USN enumeration and service install/uninstall.** The development shell
   is not elevated (`admin=False`), and opening `\\.\C:` fails with Win32 error 5.
   The USN code paths are covered by unit tests against synthetic records
   (`tests/Glide.Core.Tests/UsnTests.cs`), never against a live journal.
   To close this, from an elevated prompt:
   ```
   artifacts\publish\service\glide-service.exe --install
   artifacts\publish\service\glide-service.exe --console   # watch one rebuild
   ```
   Then confirm `%ProgramData%\Glide\index-C.gidx` exists and `glide` finds it with
   no arguments.
3. **Real-drive index size and latency.** Benchmarks use synthetic 50-byte paths,
   but real paths average ~70 bytes, so a 1M-entry index is realistically
   70–90 MB, and 1M-path latency may rise to 30–40 ms. Re-measure with
   `glide --index-bench "%ProgramData%\Glide\index-C.gidx"` on real data.
4. **No autostart story yet.** The service is installable but nothing creates a
   Start-menu shortcut, starts the launcher at logon, or restarts the service
   automatically.
5. **The actions menu and calculator were not driven by the smoke script.**
   They are covered by unit tests (`CalculatorTests`, `BookmarkStoreTests`) and
   by reading the code, but no synthetic right-click or `2+3` query was posted to
   the real window.

---

## 5. Known limits

- Full index rebuild on any journal change. A million-entry volume gets re-read
  every time the journal moves; incremental USN replay is future work.
- Hard links map to one path only. Mount points and junctions are not followed;
  the index covers what the volume's own journal reports.
- The index is per volume and per machine state; it can lag the journal by up to
  ~8 s (3 s poll + 5 s quiet window).
- Folder-scan fallback is capped at 75,000 entries to stay inside the budget.
- At most 8 result rows are drawn; `maxResults` is clamped, and the window height
  is fixed.
- No settings UI: the JSON file is the interface, and changes need a restart.
- No theme preview outside the two built-in palettes.
- No quick switch in file dialogs yet (v0.6.0).

---

## 6. v0.5.0 fix list, as executed

These were found by reading the half-finished v0.5.0 tree and by running the
binary:

1. `--max-entries` had disappeared; `settings.MaxResults` was being used as the
   scan cap. Split into `maxEntries` (scan, default 75,000) and `maxResults`
   (rows, default 8).
2. `SettingsManager` could not be tested and did not create the file. Added
   optional paths, `LoadOrCreate`, `Normalize`, and an atomic save.
3. `HotkeyParser` lived in `Glide.App`, so the test project had to reference the
   whole app. Moved to `Glide.Core.Settings` and hardened.
4. Bookmark and history stores re-read and re-parsed their JSON on every
   keystroke. Rewritten as cached `BookmarkStore` / `HistoryStore`.
5. Broken hotkey was silent, then modal. Now: fallback, stderr, notice row.
6. Bookmark and history rows were labelled by score, which mislabelled index
   rows. Replaced with `SearchResultKind`.
7. Bookmarks could appear twice (bookmark + index hit). Merge now skips paths
   already in the result list.
8. `DeviceIoControl` and `CreateFile` `LibraryImport` declarations lacked
   `EntryPoint`, so they bound to the wrong export. Fixed.
9. `ui-smoke.ps1` hardcoded `Ctrl+Alt+Space`, pressed before `RegisterHotKey` ran,
   and hid the app's stderr. It now takes `-Hotkey <spec>`, waits 800 ms, settles
   1.2 s between runs, and prints stderr on failure.
10. Placeholder contrast in both themes.

---

## 7. Working notes

```
dotnet build Glide.sln -c Release
dotnet test Glide.sln -c Release
dotnet publish src\Glide.App\Glide.App.csproj -c Release -r win-x64 -o artifacts\publish
dotnet publish src\Glide.Service\Glide.Service.csproj -c Release -r win-x64 -o artifacts\publish\service
powershell -File scripts\ui-smoke.ps1 -AppArgs "--index artifacts\bench-100k.gidx --hotkey Ctrl+Shift+Alt+F9 --theme dark" -Hotkey "Ctrl+Shift+Alt+F9"
```

- Kill `glide.exe` (`taskkill /IM glide.exe /F`) before publishing; publish can
  race and emit only PDBs, so re-run it.
- `git push` writes progress to stderr, so PowerShell reports exit 1 on success.
  The `main -> main` line is the signal.
- `RegisterHotKey` + `/` = `VK_OEM_2` (0xBF). A free combo for tests is
  `Ctrl+Shift+Alt+F9` (`0x78`).
- Back-to-back smoke runs need at least 1.2 s between killing and restarting
  glide, otherwise `RegisterHotKey` sees the dying instance as the owner.
- A stale WerFault dialog titled `glide.exe` can steal focus during UI smoke runs;
  close it with `WM_CLOSE` first.
- Index format changes must bump `IndexFile.Version` and the `GLIDX` magic.
- `%APPDATA%\Glide\settings.json` is created on first run; delete it to reset.
