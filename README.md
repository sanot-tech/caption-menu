# CaptionMenu 🪟🔝

**Right-click any window title bar → "Always on Top" — just like KDE Plasma.** ⚡

A tiny Windows 10/11 tray utility that intercepts right-clicks on window caption bars and shows
its own menu instead of the Windows system menu. The main feature: **"Always on Top"** with a
check mark that toggles on and off with the same right-click — exactly how KWin behaves in Plasma. 🎯

```
  ┌──────────────────────────── Notepad ─────────────── □ ✕ ─┐   ← right-click here
  │                                                          │   🎯 CaptionMenu intercepts it
  │   Always on Top                  ✔                     │   🚫 blocks the system menu
  │   ─────────────────────────────────────────────────────  │
  │   Move                                              ↕️  │
  │   Resize                                            ↔️  │
  │   Center                                            🎯  │
  │   ─────────────────────────────────────────────────────  │
  │   Minimize                                          🔽  │
  │   Maximize                                          🔼  │
  │   Close                                            ❌  │
  └──────────────────────────────────────────────────────────┘
```

## Features 🎁

| Menu item                | What it does                                                     | Plasma equivalent |
|--------------------------|------------------------------------------------------------------|-------------------|
| **Always on Top** ✔      | Toggles `WS_EX_TOPMOST`. The window jumps above everything        | Always on Top     |
| Move                     | Window follows the mouse (native `SC_MOVE`)                       | Move              |
| Resize                   | Resize frame, drag with mouse                                     | Resize            |
| Center                   | Centers on the work area of the window's monitor (no taskbar)     | Center            |
| Minimize / Maximize      | Native window commands                                            | Minimize/Maximize |
| Close                    | Window asks "save changes?" itself, as usual                      | Close             |

**Always on Top** is the **default item**: shown in bold, pressing `Enter` toggles it immediately. ⚡

The check mark is read from the window **every time the menu opens**, not cached — so the state is
always truthful, even if something else pinned the window programmatically. ✔️

## Build 🛠️

```powershell
dotnet publish src\CaptionMenu\CaptionMenu.csproj -c Release -r win-x64 -o out
```

Output: `out\CaptionMenu.exe` — a single file, **no dependencies** (self-contained, single-file).
About 48 MB because the .NET runtime travels with it; runtime speed is unaffected. 🎒

## Usage 🎮

1. Run `CaptionMenu.exe` — an icon appears in the tray (next to the clock). 🔔
2. Right-click the title bar of **any** window → "Always on Top". 🔝
3. Right-click that title bar again → uncheck it → the window returns to normal Z-order. 🔁

### Tray menu 🎛️

- **Intercept: on/off** — global enable/disable (really removes and re-installs the hook). 🔛✅
- **Autostart with Windows** — writes `HKCU\...\Run` (visible in Task Manager → Startup). 🚀
- **How to use** / **Exit**. ❓🚪

## How it works 🧠

```
 Right-click anywhere
        │
        ▼
 ┌───────────────────────────┐
 │ MouseHook (WH_MOUSE_LL)   │  Handles ONLY WM_RBUTTONDOWN; everything else is forwarded instantly
 │  ⚡ must return in <1 ms   │
 └───────────┬───────────────┘
             │ caption hit?
             ▼
 ┌───────────────────────────┐
 │ CaptionHitTester          │  WM_NCHITTEST with a 60 ms timeout + filters (shell/utility/cloaked)
 │  ✓ HTCAPTION + y ≤ 64 px  │
 └───────────┬───────────────┘
             │ PostMessage (never blocks the hook!)
             ▼
 ┌───────────────────────────┐
 │ MessageWindow             │  hidden 1×1 window, handler runs on the UI thread
 └───────────┬───────────────┘
             │ (safe to block now)
             ▼
 ┌───────────────────────────┐
 │ CaptionMenuPresenter      │  native CreatePopupMenu → TrackPopupMenuEx
 │  RBUTTONDOWN is swallowed │  returning 1 = "don't pass to the window"
 └───────────┬───────────────┘
             │ returned id
             ▼
 ┌───────────────────────────┐
 │ WindowOps                 │  WS_EX_TOPMOST via SetWindowPos(HWND_TOPMOST/NOTOPMOST)
 └───────────────────────────┘
```

### Why it is built this way 🧠

**1. The menu cannot be shown from inside the hook.** ⛔
A low-level mouse hook runs on the thread that installed it and must return in milliseconds.
Blocking on `TrackPopupMenu` (which blocks until an item is chosen) gets the hook killed by the
`LowLevelHooksTimeout` watchdog, and the tool silently dies. So the hook posts a `PostMessage` to
a hidden window, whose handler shows the menu on the UI thread. ✅

**2. The right-click must be swallowed.** 🚫
The hook returns `1` instead of `CallNextHookEx`, so the event never reaches the window and the
stock Windows system menu never appears. Otherwise the user would see two menus on top of each other.

**3. The button-release must be swallowed too.** 🚫
A real click is `DOWN` + `UP`. Swallowing only `DOWN` lets `UP` travel to whatever window is under
the cursor — which is now our own menu. The menu reads it as "click outside / cancel", closes
immediately, and returns `MenuId.None`. This was the reason for the "menu flashes and vanishes"
symptom during development. 🐛

**4. Foreground stealing, the hard part.** 🎖️
`TrackPopupMenuEx` only displays a menu if the **calling thread owns the foreground**. We intercepted
the click from a foreign window, so foreground is not ours. The fix is two-step and both parts are
required:

- Send `WM_NULL` to the current foreground window **synchronously** — this is the documented trick
  that grants a process the right to take foreground. Note: it must be the *current foreground*
  window, not the clicked one, otherwise the right is not granted.
- `AttachThreadInput` our thread to the foreground thread, then `BringWindowToTop` +
  `SetForegroundWindow` on our hidden window. The thread inputs are always detached in `finally`.

A message-only window cannot be made foreground, which is why the sink is a `WS_POPUP` +
`WS_EX_TOOLWINDOW` 1×1 window parked off-screen at `(-32000, -32000)`: invisible to the shell, but
still a valid foreground owner. 🔍

**5. Native menu instead of WinForms.** 🍽️
`CreatePopupMenu` + `TrackPopupMenuEx` already handle the Windows 11 dark theme, High-DPI scaling
and system fonts. Drawing our own menu would be 300 lines and a fight with themes.

**6. `WS_EX_TOPMOST` + `SetWindowPos`.** 🔝
"Always on top" is bit `0x8` in the window's extended style. Toggling it means `SetWindowPos` with
`HWND_TOPMOST`/`HWND_NOTOPMOST` and "don't move, don't resize" flags. Using `HWND_NOTOPMOST`
(instead of clearing the bit by hand) is what makes the check mark disappear even if something else
pinned the window first.

**7. Single instance.** 🔒
A `Local\CaptionMenu.SingleInstance` mutex. Two hooks means two intercepts of the same click, which
means two menus. If one is already running, it shows a message box and exits instead of failing silently.

## What it does NOT intercept (and why) 🚫

- **Desktop, taskbar, Start menu, Action Center** — classes `Progman` / `WorkerW` / `Shell_TrayWnd` /
  `Windows.UI.Core.CoreWindow`. Their "caption" is the whole panel; intercepting there is pointless. 🖥️
- **Tool windows** (`WS_EX_TOOLWINDOW`) — floating panels, tooltips, previews. 🧰
- **Minimized and cloaked windows** (`DWMWA_CLOAKED=1`) — their caption is not on screen. 👻
- **Fullscreen games** — no caption in the `WM_NCHITTEST` sense, native behaviour is preserved. 🎮

False-positive guard: even if an app answers `WM_NCHITTEST` with "I'm all caption" (Electron and
UWP apps do), clicks are additionally rejected by geometry — only the top 64 logical pixels of the
window count as a caption. 📏

## Project layout 🗂️

```
win-caption-menu/
├── src/CaptionMenu/
│   ├── Program.cs               🎬 Entry point, initialization order, message loop
│   ├── MouseHook.cs             🪝 Global WH_MOUSE_LL hook (the riskiest code)
│   ├── MessageWindow.cs         📬 Hidden 1×1 window — the "mailman" for the UI thread
│   ├── CaptionHitTester.cs      🔍 "Is this a caption under the cursor?" hit test + filters
│   ├── CaptionMenuPresenter.cs  🍽️ Menu building, display, dispatching the choice
│   ├── WindowOps.cs             ⚡ Window actions (topmost / center / move / resize / …)
│   ├── TrayIcon.cs              🎛️ Tray icon, enable/disable, autostart
│   ├── AutoStart.cs             🚀 HKCU\...\Run
│   ├── DebugLog.cs              🧪 Opt-in file logging via CAPTIONMENU_DEBUG=1
│   └── Native.cs                🌐 All P/Invoke and constants in one place
├── Test-CaptionMenu.ps1         🧪 End-to-end test with synthetic mouse input
├── assets/ICON-PROMPT.md        🎨 Leonardo AI prompt for the app icon
└── out/CaptionMenu.exe          🎒 The built single file
```

## Tests 🧪

```powershell
pwsh -File .\Test-CaptionMenu.ps1
```

The test opens Notepad, places it on topmost at a known position, **synthesizes a real right-click**
on its caption via `SendInput`, and asserts that:

1. the global hook receives the click, ✅
2. the caption hit test accepts it, ✅
3. the menu is shown (found in the log), ✅
4. clicking the first item flips `WS_EX_TOPMOST` **on**, ✅
5. doing it again flips it **off** — proving the toggle works both ways. ✅

Exit code `0` = pass, `1` = fail. 🔍

> ⚠️ Two PowerShell 7 pitfalls discovered and worked around in the test (both worth knowing):
> `Set-StrictMode -Version Latest` breaks type resolution for `Add-Type` types, and PowerShell only
> resolves `[Type]` literals for **public** classes, not `internal` ones.

## Diagnostics 🧪

```powershell
$env:CAPTIONMENU_DEBUG = "1"
.\out\CaptionMenu.exe
# log: $env:TEMP\CaptionMenu.log
```

Logging is **opt-in** — without the environment variable nothing is written, otherwise the tool would
touch the disk on every right-click in the system.

| Symptom                                  | Cause                                    | Fix                          |
|------------------------------------------|------------------------------------------|------------------------------|
| Tray says "intercept: off"               | Hook refused (rights / antivirus)        | Run as administrator         |
| No menu on a specific window             | App draws its own caption (Electron, XAML, games) | By design         |
| No menu on the taskbar / Start           | Shell classes are on the blocklist       | By design                    |

## Keyboard-free operation ♿

`Always on Top` is the default menu item, so `Enter` right after the right-click toggles it.
`Esc` closes without changing anything.

## License 📄

MIT — do whatever you want with it. 🌍
