# NetTrans

A WinUI 3 download manager built to the **FlashGet Mini v2** design handoff
(`FlashgetMini.zip` → `design_handoff_flashget_mini_v2/`): an iOS-idiom desktop
shell made of two independent 536 × 680 frames that snap together Winamp-style,
with a Dynamic-Island-like speed capsule floating above them.

Targets `net8.0-windows10.0.19041.0` and `net10.0-windows10.0.19041.0` side by
side, unpackaged (no MSIX required to run).

## What the design asks for, and where it lives

| Design piece | Implementation |
| --- | --- |
| `.frame.win2` — task frame, 536 × 680, 16px corners | `Views/MainShell.xaml` in a borderless window built by `Shell/ShellHost.cs` |
| `.frame.sidewin` — inspector frame, same size | `Views/InspectorShell.xaml`, its own window |
| Magnetic docking, 18px threshold, blue guide, .24s settle | `Services/DockManager.cs` + `Shell/SnapGuideWindow.cs` |
| `.bond-r/-l/-t/-b` — corners squared on the bonded edge | `Interop/WindowChrome.ApplyCorners` (a Win32 window region) |
| `.nav` drag-to-move | `WindowChrome.BeginDrag` → `WM_NCLBUTTONDOWN` / `HTCAPTION` |
| `.seg` — sliding segmented control | `Views/Controls/SegmentedControl.xaml` |
| `.row` + `.swipe` + `.card.dense` | `Views/Controls/TaskRow.xaml` |
| `.morerow` — five-row fold | `ShellViewModel.VisibleTasks` + the fold button in `MainShell.xaml` |
| `.bar` — the seven toolbar icons | `MainShell.xaml`, bottom row |
| Inspector 概览 / 分块 / 连接 / 日志 | `Views/InspectorShell.xaml` with `RingProgress`, `BlockGrid`, `SessionBars`, `ConnectionList` |
| `.newver` — 服务器上有更新版本 | `NewVersionInfo` on the model, notice at the top of the inspector body |
| `.sheet` — iOS half sheets | `Views/Controls/SheetHost.xaml` + `Views/Sheets/*` |
| `.ctx` — 244px popovers (context / 新建 / 显示与排序 / 托盘) | `Views/Controls/PopoverControl.xaml` |
| `.island` — the 灵动岛 | `Views/Controls/IslandControl.xaml`, its own top-most window |
| `.toast`, `.banner`, `.drop` | overlay layer in `MainShell.xaml` |
| 贴边隐藏, 老板键 Ctrl+Alt+H | `Shell/ShellHost.cs` (`RegisterHotKey`, edge slide) |
| Design tokens, icon set | `Resources/Tokens.xaml`, `Resources/Icons.xaml` |

## Layering

The network side is split in two, and the solution mirrors it:

- **`NetTrans.Kernel`** owns the tasks. It is a plain `net8.0` library whose
  csproj carries no Windows/WinUI references, so nothing inside it can reach a
  dispatcher, a XAML control or a view model. It exposes exactly one contract,
  `IDownloadKernel`: commands (`Add`, `Toggle`, `Remove`, `ToggleAll`,
  `MoveToFront/Back`, `Redownload`, `SetPriority`, `SetSpeedLimit`) and events
  (`TaskAdded/Removed/Updated/Completed/Moved`, `Ticked`), with the threading
  contract spelled out on the interface. The current driver,
  `SimulatedDownloadKernel`, ticks on the handoff's 900ms cadence; a real
  multi-segment HTTP/BT engine drops in behind the same interface.
- **`NetTrans` (the app)** owns the presentation. `Services/TaskManager` is the
  only consumer of that contract: it marshals kernel events onto the UI thread
  via `DispatcherQueue`, mirrors the kernel queue into the
  `ObservableCollection` the shell binds, and forwards user intent back down as
  kernel commands. View models and views never touch the kernel directly.

## Project layout

```
NetTrans.Kernel/            Download kernel -- plain net8.0, zero UI references
  IDownloadKernel.cs        The boundary: commands + events
  SimulatedDownloadKernel.cs Timer-driven simulation on the handoff's SEED
  DownloadTask.cs           The kernel's task record (was Models/DownloadItem)
NetTrans/
  App.xaml(.cs)             Entry point; builds kernel → TaskManager → ShellHost
  Shell/
    ShellHost.cs            The three frames, docking, island follow, edge-hide, boss key
    SnapGuideWindow.cs      The 3px blue snap guide
  Interop/
    NativeMethods.cs        Win32 surface (subclassing, regions, hotkeys, DWM)
    WindowChrome.cs         One frameless, 16px-rounded, drag-anywhere window
  Views/
    MainShell.xaml(.cs)     Task frame
    InspectorShell.xaml(.cs) Inspector frame
    Controls/               StrokeIcon, SegmentedControl, TaskRow, IosSwitch, FormRow,
                            CheckRow, RingProgress, BlockGrid, SessionBars,
                            ConnectionList, SheetHost, PopoverControl, IslandControl
    Sheets/                 Add / Batch / Torrent / Sniff / Settings
  ViewModels/               ShellViewModel, DownloadItemViewModel
  Models/                   AppSettings
  Services/                 TaskManager (the kernel↔UI seam), DockManager, Easing,
                            FormatHelpers, ThemeBrushes, clipboard + settings stores
  Resources/                Tokens.xaml (iOS tokens as ThemeDictionaries), Icons.xaml,
                            Styles/ (Text, Surfaces, Buttons, Inputs, Shadows)
  Converters/               BindingHelpers (x:Bind function bindings)
```

## Building

Requires Visual Studio 2022/2026 with the **Windows App SDK** / **WinUI
application development** workload (or the .NET 8/10 SDK plus Windows App SDK
tooling on the command line).

```
dotnet build -f net8.0-windows10.0.19041.0 -r win-x64
```

This rewrite, like the original scaffold, was authored without access to a
Windows toolchain, so **give it a first build pass on Windows before relying on
it**. Expect typo-level XAML/C# fixes rather than architectural ones. The two
places most worth checking first are the `CommunityToolkit.WinUI.Media` shadow
API in `Resources/Styles/Shadows.xaml` and the Win32 interop in `Interop/`.

## Deliberate departures from the handoff

Everything else is pixel-for-pixel; these could not be, and each is commented at
the site:

- **Snap-guide glow.** The design puts a 12px blue glow around the `.snapline`.
  A WinUI 3 window cannot be partially transparent, so the guide is the solid
  3px bar only (`Shell/SnapGuideWindow.cs`).
- **Island shadow.** `.island`'s `0 10px 30px` shadow would be clipped by its own
  window region; it uses the OS window shadow instead.
- **Tray pill.** The prototype floats a separate tray pill on the desktop layer
  showing the same total speed as the island. Rather than duplicate it, the
  island carries the tray menu — click it to get 显示主窗口 / 贴边隐藏 / 老板键 / 退出.
- **Green traffic light.** The frame is a fixed 536 × 680, so there is nothing to
  zoom to; the green dot opens and closes the inspector frame, the only thing
  that changes the shell's footprint.
- **Typeface.** Windows has no SF Pro. The handoff's own fallback chain is
  Inter Tight → system-ui, and Segoe UI Variable is the closest system face, so
  it leads the stack in `Tokens.xaml`.
- **Dark theme.** The handoff only specifies a light palette. `Tokens.xaml` adds
  the iOS system-dark counterpart of every token so the app can follow the
  Windows theme without inventing new semantics.
- **Tabular figures.** WinUI has no font-feature API on `TextBlock`; Segoe UI
  Variable's lining figures are already fixed-width, so numbers still align.

## What's real vs. stubbed

- UI is wired end to end (MVVM, CommunityToolkit.Mvvm) — no mock view models.
- `SimulatedDownloadKernel` is seeded with the handoff's own `SEED` array and
  ticks on the same 900ms cadence and growth curve, behind the
  `IDownloadKernel` contract a real multi-segment HTTP/BT engine would
  implement. The kernel library builds anywhere .NET 8 does
  (`dotnet build NetTrans.Kernel`), so it can be developed and tested without a
  Windows toolchain.
- Settings persist **portably**, next to the executable
  (`NetTrans.settings.json`), falling back to `%LOCALAPPDATA%\NetTrans` when that
  directory is read-only — matching the 设置 sheet's promise of no registry writes.
- Clipboard URL detection is live (`Clipboard.ContentChanged`).
- 打开文件 / 在文件夹中显示 / 重命名 / 校验 SHA-256 report through the toast lane; they
  need the real engine to do anything.
