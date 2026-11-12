# Glide — File Dialog & Explorer Integration (v0.6.0 design)

**Status:** implemented in v0.6.0 as `src/Glide.App/Integration/`: path injection,
Quick Switch (`Ctrl+G` while a file dialog is in front) and the opened-folders
list. Dialog detection requires a shell view inside the `#32770`, so message
boxes are never mistaken for file dialogs.

This document describes how Glide can provide Listary-style "quick switch" inside Windows file dialogs and Explorer. All mechanisms described below are **documented Windows APIs** (MSDN / learn.microsoft.com), not reverse-engineered Listary internals.

Glide is a single NativeAOT binary. That constraint heavily shapes what integration is realistic.

---

## 1. Integration taxonomy

Windows apps open files through several distinct UI layers. Glide can hook into each, with different effort and risk:

| Layer | Mechanism | Glide feasibility |
|---|---|---|
| Standard file dialog (Open/Save) | `IFileDialog` + `IFileDialogEvents` + `IFileDialogCustomize` | **High** — out-of-process COM, no injection |
| Folder dialog | Same `IFileDialog` with `FOS_PICKFOLDERS` | **High** |
| Explorer context menu | Shell extension COM (`IContextMenu`) | **Low** — requires separate DLL + `regsvr32` |
| Global hotkey anywhere | `RegisterHotKey` / `WM_HOTKEY` | **High** — already works |
| Third-party file managers | Each has its own plugin SDK | **Low** — separate project per host |

---

## 2. File dialog integration (the v0.6.0 target)

### 2.1 What Listary actually does here (documented behavior)

When a standard Windows Open/Save dialog is active, Listary:

1. Detects the dialog window (class `#32770` for legacy dialogs, or `IFileDialog`-based dialogs in modern apps).
2. Exposes its own UI overlay inside the dialog.
3. When the user picks a path in Listary, writes that path back into the dialog's filename edit box so the host app receives it.

Glide can do steps 1 and 3 with **documented APIs only**. Step 2 (custom UI inside the dialog) is optional for v0.6.0 — a global hotkey that "pastes the selected Glide path into the active dialog" already gives most of the value.

### 2.2 Detecting an active file dialog

Standard Open/Save dialogs expose COM interfaces:

```csharp
// CoCreateInstance the dialog to discover its CLSID / IID,
// or use IFileDialog::GetFileType / Show for owned dialogs.
// For foreign dialogs (owned by another process), detect via window class.
```

**Legacy dialogs** (class `#32770`): find windows with `EnumWindows`, compare class name to `#32770`, then inspect child controls (Edit, ComboBox) via `FindWindowEx`.

**Modern dialogs** (Vista+, `IFileDialog`): COM-based. Glide can advise `IFileDialogEvents` to receive `OnFileOk`, `OnFolderChanging`, etc. For foreign dialogs, the app already owns the `IFileDialog`; Glide cannot easily re-interface with it from outside without IPC, but it can still write the path into the dialog's edit control via `WM_SETTEXT`.

### 2.3 Writing a path back into the dialog

| Dialog type | Technique |
|---|---|
| Legacy `#32770` | `FindWindowEx` → edit control → `WM_SETTEXT` with the full path |
| Modern `IFileDialog` | `IFileDialog::SetFileName()` (if Glide hosts/advises the dialog) |
| Fallback any dialog | Simulate `Ctrl+V` paste into the focused filename field |

`SetFileName` is the cleanest path for modern dialogs:
```csharp
// HRESULT IFileDialog::SetFileName([in] LPCWSTR pszName);
```
This tells the dialog the selected file, so when the user presses Open/Save, the host app gets Glide's path.

### 2.4 Injecting a "Glide" button into the places bar

`IFileDialogCustomize` lets you add buttons, separators, and labels to a dialog you create:

```csharp
// HRESULT IFileDialogCustomize::AddButton([in] DWORD dwIDCtl, [in] LPCWSTR pszLabel);
// HRESULT IFileDialogCustomize::AddEditBox(...)
// HRESULT IFileDialogCustomize::EnableOpenDialog(...)
```

This works for dialogs **Glide creates itself**, not foreign dialogs owned by Notepad/Word/etc. For v0.6.0, this is a "nice to have" when Glide launches its own picker; for foreign dialogs, the global-hotkey + `WM_SETTEXT` approach is the practical one.

---

## 3. Explorer context menu integration

Right-click → "Open with Glide" in File Explorer requires a **shell extension**:

- Implement `IContextMenu` and `IShellExtInit` in a COM server (separate DLL).
- Register under `HKCR\*\shell\Glide` (all files) or `HKCR\Directory\shell\Glide` (folders).
- `regsvr32` to register the COM class.

**Why Glide avoids this for v0.6.0:**
- Breaks the "single binary, no separate DLL" rule.
- Shell extensions load inside `explorer.exe`; a crash there restarts the shell.
- NativeAOT COM server hosting is possible but adds complexity.

This is explicitly deferred to a later version (or a separate `Glide.Shell` package).

---

## 4. Global hotkey

Already implemented in Glide v0.5.0:

- `RegisterHotKey` on a hidden message-only window.
- Fallback to `Ctrl+Alt+Space` if the default (`Ctrl+Shift+Alt+/`) is taken by another app (e.g. Listary).
- Notice row painted in-window instead of a modal `MessageBox` (avoids blocking the message loop).

This works **inside file dialogs** too, because `RegisterHotKey` is session-wide and dialogs generally don't swallow `WM_HOTKEY`. The user presses Glide's hotkey, Glide shows its window, user picks a path, then Glide injects it into the active dialog via `WM_SETTEXT` / `SetFileName`.

---

## 5. Third-party file manager integration

Listary ships separate plugins for Total Commander, Directory Opus, XYplorer, xplorer2, etc. Each host exposes its own API:

| Host | Integration mechanism |
|---|---|
| Total Commander | `wdx` / `wdx2` plugin interface (C/C++) |
| Directory Opus | Scripting / toolbar button API (JScript/VBScript) |
| XYplorer | Scripting / custom columns |
| xplorer2 | Column / scripting API |
| Bandizip / WinRAR | Extract-dialog hooking |

Glide cannot support all of these without separate projects. v0.6.0 scope: **none**. Defer to v0.7+ or community plugins.

---

## 6. AutoCAD / WPS / OneCommander / Explzh / Files

These are individual applications with their own dialog patterns:

- **AutoCAD**: ObjectARX / .NET API plugin — separate project.
- **WPS / Office**: `IFileDialog` customization works where Office uses standard Windows dialogs; Office-specific ribbon integration needs VSTO/COM add-in.
- **OneCommander / Files**: Modern Win32/WPF apps using standard `IFileDialog` — covered by the file-dialog path above.

v0.6.0 scope: standard dialogs only.

---

## 7. Recommended v0.6.0 implementation

### Phase 1 — Global hotkey inside dialogs (done in v0.5.0)
Glide's hotkey already works when a file dialog has focus.

### Phase 2 — Path injection into active dialog (~200 lines COM/Win32)

1. Detect active foreground window: `GetForegroundWindow`.
2. Is it a file dialog? Check class `#32770` or try `IFileDialog` QI.
3. Find the filename edit control: `FindWindowEx` (legacy) or use `IFileDialog::SetFileName` (modern).
4. When user confirms a path in Glide: `WM_SETTEXT` or `SetFileName(path)`.

### Phase 3 — Glide-owned dialog with places-bar button (optional)
Use `IFileDialogCustomize` when Glide launches its own Open/Save picker.

---

## 8. Trade-off summary

| Approach | Single binary | Injection risk | Works in all apps | Effort |
|---|---|---|---|---|
| `RegisterHotKey` + `WM_SETTEXT` | ✅ | ✅ None | ✅ Standard dialogs | Low |
| `IFileDialog` / `IFileDialogCustomize` | ✅ | ✅ None | ✅ Modern dialogs | Medium |
| Shell extension (Explorer menu) | ❌ Separate DLL | ⚠️ Loads into explorer.exe | ✅ Explorer | High |
| CBT / keyboard hook | ⚠️ Possible but fragile | ⚠️ Injects into other processes | ⚠️ Most apps | High, AV-prone |
| Per-app plugin SDKs | ❌ Separate project each | ⚠️ Host-specific | ❌ Only that app | Very high |

**Verdict for Glide v0.6.0:** `IFileDialog` + `WM_SETTEXT` via global hotkey. Documented APIs, no injection, single binary, works in Notepad, VS Code, Office, browsers, and most "Open/Save" dialogs.

---

## 9. Evidence sources (public documentation)

All of the above is verifiable on learn.microsoft.com:

- `IFileDialog` interface — Windows Vista+
- `IFileDialogEvents` — `OnFileOk`, `OnFolderChanging`
- `IFileDialogCustomize` — `AddButton`, `AddEditBox`
- `RegisterHotKey` / `WM_HOTKEY` — session-wide hotkeys
- `IContextMenu` / `IShellExtInit` — shell extensions
- `FindWindowEx` / `WM_SETTEXT` — legacy dialog child controls

The mechanisms above come from public documentation. A separate metadata-only
analysis of the installed Listary (type names, settings enums, PE imports; no
code copied) is recorded in `docs/listary-analysis.md`.

---

*End of document.*