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
  contract spelled out on the interface. Two implementations live behind it:
  `SimulatedDownloadKernel` (the handoff's timer-driven demo) and
  `RealDownloadKernel` — the multi-protocol transfer engine described below.
  `App.xaml.cs` picks one with a single `UseRealKernel` switch; nothing else in
  the app changes.
- **`NetTrans` (the app)** owns the presentation. The mirroring engine,
  `NetTrans.Kernel.TaskManager<TItem>`, is generic over the host's per-task
  view wrapper and lives in the kernel so it stays UI-free and testable
  headlessly: it marshals kernel events onto the UI thread, mirrors the kernel
  queue into the `ObservableCollection` the shell binds, and forwards user
  intent back down as kernel commands. The app's `Services/TaskManager` is a
  thin subclass that binds it to `DownloadItemViewModel` — view models and
  views never touch the kernel directly.

## Network protocol architecture (Ghost Downloader alignment)

The real kernel's protocol surface was dimensioned against
[Ghost Downloader](https://github.com/XiaoYouChR/Ghost-Downloader-3):
everything its network stack supports, NetTrans supports; where Ghost reaches
for an external engine, NetTrans models that as an explicit slot instead of
pretending.

```
NetTrans.Kernel/
  KernelOptions.cs            UA / connections / concurrency / retry / speed /
                              proxy / headers / mirror resolver / TLS handler factory /
                              external engine (runner delegate + command template)
  TaskManager.cs              UI-agnostic kernel↔UI seam, generic over the view wrapper
  RealDownloadKernel.cs       priority scheduling, MaxSimultaneous slots, tick,
                              SHA-256 verify, checkpoint lifecycle
  Protocol/
    DownloadProtocol.cs       Http | Ftp | BitTorrent | Hls | Dash | Ed2k
    ProtocolDetector.cs       URL scheme + extension → protocol
    ProtocolDriverRegistry.cs detect → probe → one-hop handoff (body sniff)
    IProtocolDriver.cs        ResourceProbe + TransferPlan contract
    Drivers/HttpDriver.cs     HEAD→GET+Range:0-0 probe, Content-Disposition naming,
                              mirror validation, m3u8/mpd body sniff
    Drivers/FtpDriver.cs      FTPS (EnableSsl), size probe, proxy
    Drivers/HlsDriver.cs      master→highest-bandwidth variant, EXT-X-MAP,
                              live/encrypted → honest Unsupported
    Drivers/DashDriver.cs     SegmentTemplate (duration / SegmentTimeline),
                              SegmentList, SegmentBase → range fallback
    Drivers/BitTorrentDriver.cs  bencode + magnet metadata
    Drivers/Ed2kDriver.cs     ed2k:// link metadata
    Bencode.cs                minimal bencode decoder
  Transfer/
    SegmentTable.cs           over-split (4× connections, cap 64) work queue
    ChunkedFileWriter.cs      offset writes into a preallocated file — no merge pass
    TransferCheckpoint.cs     resumable state, stored next to the target file
    TransferSession.cs        one run: workers, retry, mirror failover, pause/resume
    SpeedLimiter.cs           token bucket; global = min(global, per-task), live retune
    MirrorSourceSet.cs        round-robin + failure demotion
  Net/Socks5Connector.cs      RFC1928 CONNECT + RFC1929 username/password auth
  Net/ProxyInfo.cs            socks5/http(s) proxy URL parsing (userinfo = credentials)
```

A probe resolves any URL into a `TransferPlan` — `ByteRanges` (multi-segment,
mirror-backed), `FtpFile`, `SegmentFiles` (HLS/DASH), `ExternalEngine`
(BT/eD2k data plane) or `Unsupported` (with the reason, in Chinese, shown to
the user). Resume state lives in a `.nettrans.json` checkpoint beside the
target file: range tables for HTTP, committed-prefix counters plus a playlist
fingerprint for HLS/DASH, validated against size/ETag before a resume is
trusted.

The `ExternalEngine` slot is executable: hosts supply either an
`KernelOptions.EngineRunner` delegate (typed plan → produce the file) or an
`ExternalEngineCommand` shell template with `{url}` `{path}` `{name}`
placeholders; a non-zero exit or a missing file fails the task with the
engine's output logged. With neither configured the task fails honestly —
no silent fallback.

### Capability matrix vs. Ghost Downloader

| Capability | Ghost Downloader | NetTrans |
| --- | --- | --- |
| HTTP/HTTPS multi-segment + Range resume | Yes | Yes |
| FTP / FTPS | Yes (aioftp) | Yes (`FtpWebRequest`, EnableSsl) |
| HLS / m3u8 (VOD) | Yes (N_m3u8DL-RE) | Yes (concatenated TS, no remux) |
| MPEG-DASH | Yes (N_m3u8DL-RE) | Yes (template/list/base) |
| Live HLS / encrypted HLS | via N_m3u8DL-RE | Honest `Unsupported` |
| Magnet / .torrent metadata | Yes (libtorrent) | Yes (bencode + magnet parse) |
| BitTorrent data plane | Yes (libtorrent ≥ 2.0.13) | External-engine slot (`EngineRunner` / command template) — clear error when unconfigured |
| eD2k | Yes (Kelpie) | Link metadata + external-engine slot |
| yt-dlp (YouTube, Bilibili…) | Yes | Not built in — same slot can host it |
| aria2 RPC control | Yes | Not implemented |
| Browser-extension sniffing | Yes | Not implemented (app ships a sniff sheet) |
| HTTP(S) / SOCKS5 proxy | Yes | Yes (`WebProxy` / `Socks5Connector`, incl. SOCKS5 RFC1929 username/password and userinfo credentials) |
| Browser TLS fingerprint | Yes (wreq) | Slot only (`HttpHandlerFactory`); .NET SslStream can't imitate a browser handshake |
| Custom UA / headers / cookies | Yes | Yes (`KernelOptions`) |
| Mirror acceleration (GitHub / HuggingFace) | Yes (curated mirror lists) | Generalized: any `MirrorResolver`, validated by size + ETag |
| Live edits of URL/headers/proxy mid-download | Yes | Yes — checkpoint survives, resume keeps bytes |
| Global + per-task speed limit, live retune | Yes | Yes (token bucket) |
| Priority queue + max-simultaneous slots | Yes | Yes |
| SHA-256 verification | No | Yes |
| Category folders, newer-version probe, clipboard watch, scheduled downloads | No / partial | Yes |

The two rows where NetTrans intentionally says "no" (BT/eD2k data plane) are
the same boundary Ghost draws with native engines — the difference is that
NetTrans surfaces it as a typed plan (`ExternalEngine`) instead of a silent
fallback.

## Project layout

```
NetTrans.Kernel/            Download kernel -- plain net8.0, zero UI references
  IDownloadKernel.cs        The boundary: commands + events
  TaskManager.cs            The UI-agnostic kernel↔UI seam (generic core)
  SimulatedDownloadKernel.cs Timer-driven simulation on the handoff's SEED
  RealDownloadKernel.cs     The real engine -- see "Network protocol architecture" above
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
  Services/                 TaskManager (thin subclass binding the kernel's generic
                            manager to DownloadItemViewModel), KernelOptionsFactory
                            (AppSettings → KernelOptions), DockManager, Easing,
                            FormatHelpers, ThemeBrushes, clipboard + settings stores
  Resources/                Tokens.xaml (iOS tokens as ThemeDictionaries), Icons.xaml,
                            Styles/ (Text, Surfaces, Buttons, Inputs, Shadows)
  Converters/               BindingHelpers (x:Bind function bindings)
tests/                      Headless console suites -- net8.0, zero NuGet packages
  UiLogicTests/             TaskManager mirroring + view-model + settings logic
  NetworkTests/             Protocol detection / drivers / plans over a local HTTP server
  FileManagementTests/      Save-path collision naming, checkpoint lifecycle, atomic writes
  EndToEndTests/            Full pipeline over a local server: multi-segment bytes,
                            SHA-256, 96-block grid, same-kernel resume, HLS/DASH
                            concatenation, no-Range fallback, priority scheduling
  SetupTests/               AppSettings → KernelOptions mapping + settings-store round trip
  ProxyTests/               SOCKS5 RFC1929 handshake mocks + ProxyInfo.Parse table
  ExternalEngineTests/      EngineRunner delegate + command-template execution
```

Every suite is a plain `dotnet run` (Linux-clean); assertion style is a shared
`Check`/`That`/`Equal` helper with a one-line summary and a non-zero exit code
on failure.

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
- **Dark theme.** The handoff only specifies a light palette, so the app pins
  `RequestedTheme="Light"` to stay 1:1 with the prototype on any Windows
  theme. `Tokens.xaml` still carries the iOS system-dark counterpart of every
  token for the day a dark design exists — flip the attribute then.
- **Tabular figures.** WinUI has no font-feature API on `TextBlock`; Segoe UI
  Variable's lining figures are already fixed-width, so numbers still align.

## What's real vs. stubbed

- UI is wired end to end (MVVM, CommunityToolkit.Mvvm) — no mock view models.
- `SimulatedDownloadKernel` is seeded with the handoff's own `SEED` array and
  ticks on the same 900ms cadence and growth curve — it remains one switch
  away for demoing the UI without a network. The default is
  `RealDownloadKernel` (multi-segment HTTP, FTP, HLS, DASH, BT/eD2k metadata,
  resume, mirrors, rate limiting); both sit behind the same `IDownloadKernel`
  contract and either can be picked in `App.xaml.cs` (`UseRealKernel`). The
  kernel library builds anywhere .NET 8 does (`dotnet build NetTrans.Kernel`),
  and its transfer paths are covered by the headless suites under `tests/`
  (188 checks across 7 projects — UI mirroring, protocol plans, file/checkpoint
  management, end-to-end transfers, settings mapping, proxy auth, external
  engine — all passing on Linux).
- Settings persist **portably**, next to the executable
  (`NetTrans.settings.json`), falling back to `%LOCALAPPDATA%\NetTrans` when that
  directory is read-only — matching the 设置 sheet's promise of no registry writes.
- Clipboard URL detection is live (`Clipboard.ContentChanged`).
- 打开文件 / 在文件夹中显示 / 重命名 / 校验 SHA-256 report through the toast lane; they
  need the real engine to do anything.
