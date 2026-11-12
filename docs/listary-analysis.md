# Listary 6 analysis (v0.6.0)

What Glide v0.6.0 learned from the installed Listary, and how. The goal was a
feature inventory and an architecture picture, not code: nothing below was
copied, and Glide loads nothing from Listary's folder.

## Method

All of it is static metadata from `C:\Program Files\Listary`, read without
running Listary's code:

| Artifact | Tool | What was read |
|---|---|---|
| `Listary.exe` (2.1 MB, .NET Framework, x86) | REA `inspect_managed_members`, then a `System.Reflection.Metadata` dump of type names | 1,848 type definitions; field and property names of settings classes and enums |
| `listary-core.exe` (15.3 MB, native x64) | REA `inspect_pe_imports` | 14 DLLs, 248 imported functions |
| Install folder | directory listing | process and plugin layout |

Many types in `Listary.exe` are obfuscated (`?`), so behaviour hidden in method
bodies is unknown. The enums and settings classes are not obfuscated; they are
the source for the inventory below.

## Architecture

| Process | Role | Evidence |
|---|---|---|
| `Listary.exe` | WPF UI, settings, launcher, file-manager integration | WPF/MahApps/HandyControl assemblies; `Listary.Main.Views.*` |
| `listary-core.exe` | Indexer and search engine | `uniffi.listary_engine.SortBy`; imports `DeviceIoControl`, `FindFirstVolumeW`, `ReadDirectoryChangesW`, `CreateFileMappingW`/`MapViewOfFile`, Winsock |
| `Listary.Service.exe` | Elevated helper | separate process, 16 MB private |
| `ListaryHookHost32/64.exe` + `Hooks\ListaryHook*.dll` | Injected hooks into Explorer and file dialogs | `Listary.Core.Hook.WND_MAINTYPE`: `WND_EXPLORER`, `WND_FD_COMMON`, `WND_FD_MSOFFICE`, … |

The UI talks to the engine over a local socket: gRPC (`Grpc.Net.Client`,
`Google.Protobuf`), with request types such as `IndexedSearchSearchParams`.

Measured on this machine: `Listary.exe` 222.9 MB working set / 362.8 MB
private; `Listary.Service.exe` 44.9 MB; the two hook hosts 8–9 MB each.
Nearly all of the memory is the WPF process, not the index.

## Feature inventory and Glide's answer

| Listary (type, members) | What it is | Glide v0.6.0 |
|---|---|---|
| `SearchFilter`: `Keyword`, `Extensions`, `SearchForFolders`, `SearchForFiles`, `SearchInFolders` | Named filters typed as keywords | `searchFilters` setting, `doc:`, `folder:`, `ext:pdf,docx`; checked on index bytes |
| `IndexedSearchSearchParams.OnlySearchInPaths`, `FolderParts` | Search inside a folder | `C:\path\ terms` scopes the index scan and skips other volumes' files |
| `WebsiteItem`: `Keyword`, `Url`, `SuggestionProvider` | Web searches by keyword | `webSearches` setting, `gg terms`; fallback row when nothing on disk matches. No suggestions (network) |
| `BuiltinCommandType.QuickSwitch`, `ListarySettings.QuickSwitchHotkey`, `EnableAutoQuickSwitch` | Jump a file dialog to the folder open in Explorer | `Ctrl+G` registered only while a file dialog is in front; Explorer folders read through `IShellWindows`. No auto mode |
| `SpecialMenuItemType.CurrentlyOpenedFolders` | List of open folders | Opening Glide over a dialog lists Explorer's folders first |
| `ListarySettings.ShowListaryHotkey`, `DisableHotkeyForFullScreenApps`, `PressShowListaryHotkeyAgainBehavior` | Activation | `hotkey` with a recorder in Options, `disableInFullScreen`, `pressAgainHides`; double Ctrl was tried and removed in v0.6.1, ignored over full-screen windows |
| `HotkeyScope`: `Local`, `FileManager`, `Global` | Shortcuts scoped to a window type | Quick Switch is the scoped shortcut, via a WinEvent foreground hook |
| `BuiltinCommandType`: `Options`, `Exit`, `OpenCommandPrompt`, … | Commands among results | `Glide Settings`, `Exit Glide`, `Recycle Bin`, …; "Open in Terminal" action |
| `FileSearchSettings.FolderPriorities`, `FileNamePriorities`, `LowPriorityForSystemAndHiddenFiles` | Ranking priorities | `fileSearch` priorities (`PriorityEvaluator`) |
| `CustomFolderIndex`, `DisabledNtfsVolumes` | Non-NTFS folders | Path browsing reads any folder from disk; no separate custom index yet |
| `AppConfig.EnableInstantTypeSearch` (Explorer type-to-search) | Typing inside Explorer | Not done: requires injecting a hook DLL into `explorer.exe` |
| `PinyinMatch`, `cnmatch.bin` | Chinese matching | Not done |

## Why Glide stays small where Listary does not

- One NativeAOT process with a GDI window, against a WPF process with
  MahApps, HandyControl, WebView2, Sentry, gRPC and analytics loaded.
- The index is a file streamed per query; the launcher holds none of it.
- The indexer runs as a service that only reacts to create, delete and rename
  records, and ignores folders such as `%TEMP%` and its own index folder (see
  `docs/index.md`). Before v0.6.0 the service rewrote `index-c.gidx` on every
  journal move, and that write was itself a journal move on C:.
- No hook DLLs in other processes. Dialog integration uses window messages,
  and the two hooks Glide does install (`WH_KEYBOARD_LL`, `WinEvent`) run
  inside Glide.

## Explorer and file dialog integration (v0.6.1)

`Hooks\ListaryHook64-6.1.10.1.dll`, read with REA `inspect_native_exports` and
`inspect_pe_imports`, plus its string table:

| Evidence | Meaning |
|---|---|
| Exports `InstallHook`, `RemoveHook`, `HookShellWindow`, `CreateHookHelper` | A global hook DLL installed by the hook hosts |
| Imports `SetWindowsHookExW`, `CallNextHookEx` | Loaded into every GUI process by a Windows hook |
| COMCTL32 ordinals 410/412/413 (`SetWindowSubclass`, `DefSubclassProc`, `RemoveWindowSubclass`) | Subclasses Explorer and dialog windows from inside their process |
| `GetGUIThreadInfo`, `GetFocus`, `AccessibleObjectFromWindow` | Finds the focused file list |
| Strings `OpenFolderPidl`, `SHELLDLL_DefView`, `Folder dialog message: …` | Navigates the folder view in-process |
| `CreateFileMappingW`, strings `ListaryX_MainSharedMemory`, `WM_LISTARY_SENDMESSAGE` | Talks to `Listary.exe` through shared memory and a registered message |

Glide gets the same behaviour without injecting anything:

- Typing in a file list is caught by Glide's own low-level keyboard hook,
  which checks that focus is a `DirectUIHWND` under `SHELLDLL_DefView` inside
  `CabinetWClass` (Explorer) or `#32770` (a file dialog).
- Explorer is navigated through the shell's automation objects:
  `IShellWindows` → `IWebBrowser2` (`HWND`, `LocationURL`, `Navigate2`), bound
  by name through `IDispatch`. These calls are marshaled into explorer.exe, so
  the folder opens in the same window. An earlier version used
  `IShellBrowser::BrowseObject`, which is not meant to be called from another
  process; `glide --explorer-tabs` prints what the automation route sees.
- A file dialog is driven with its file name box and OK button
  (`docs/integration.md`).

The bars are compact, as Listary's are: about 440 px wide, single-line rows,
inside Explorer's bottom-right corner, or under the dialog.

## Options window (v0.6.1)

The pages and their settings come from the option view models in
`Listary.exe` (`Listary.Core.OptionsWindow.*ViewModel`), read with the same
metadata dump. Where Glide has the feature, the option does the same thing:

| Listary page / member | Glide |
|---|---|
| General: `EnableAutoStart` | Start Glide when Windows starts (HKCU Run key) |
| General: `OpenFolderPath`, `OpenFolderParam` | Open folder with: program and arguments, `%1` = folder |
| `ListarySettings.HideWhenLosingFocus` | Hide the search window when it loses focus |
| Appearance: `Theme` | Windows setting / Light / Dark, applied live |
| Hotkey: `ShowListaryHotkey`, `ResetShowListaryHotkeyCommand` | Recorder field, Reset to default; re-registered on Apply, refused with a message when taken |
| Hotkey: `PressShowListaryHotkeyAgainBehavior` | Pressing it while Glide is open: hide / keep open |
| Hotkey: `DisableHotkeyForFullScreenApps` | Ignore the hotkey while a full-screen app is in front |
| Hotkey: `QuickSwitchHotkey` | Quick Switch recorder |
| File Search: `SearchFilters` (add, remove, move up/down) | Search filters table with the same buttons |
| File Search: `PriorityItems` | Priorities page: high, low, never shown |
| Web Search: `Websites` (add, remove) | Web Search table: keyword, name, URL |
| Commands: `Commands`, `CustomCommand` | Built-in list and your own commands with arguments |
| Integration: `AppConfig.EnableInstantTypeSearch` | Type to search in Explorer, in Open/Save dialogs |

Not carried over: language, analytics, update channels, software rendering,
Listary 5 import, the popup menu and favorites pages, and Pro licensing.
