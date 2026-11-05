# NetTrans 功能 Spec 调研：同类下载器对标

> 调研日期：2026-11-05 · 分支：`trae/agent-qwW3Wi` · 只产出文档，不含代码变更

---

## ① 调研范围与日期

- **调研日期**：2026-11-05。
- **方法**：通过 WebSearch / WebFetch 抓取各对标项目在 GitHub 上的 README（raw 或仓库页）、官网功能页。gh CLI 的 API 凭据已失效，未使用 gh api。
- **对标对象（9 款）**：
  1. [yt-dlp](https://github.com/yt-dlp/yt-dlp) — 命令行音视频下载器
  2. [aria2](https://github.com/aria2/aria2) — 多协议命令行下载引擎
  3. [Motrix](https://github.com/agalwood/Motrix)（v2.2.0）— aria2 内核桌面下载器
  4. [Free Download Manager](https://www.freedownloadmanager.org/features.htm)（v6.35）— 闭源桌面下载器
  5. [Persepolis](https://github.com/persepolisdm/persepolis) — aria2/yt-dlp 前端
  6. [AB Download Manager](https://github.com/amir1376/ab-download-manager)（v1.11.4）— Compose 桌面下载器
  7. [Varia](https://github.com/giantpinkrobots/varia)（v2026.8.5）— aria2 + yt-dlp + libtorrent 前端
  8. [Gopeed](https://github.com/GopeedLab/gopeed)（2.0.0 beta）— Go 内核跨端下载器
  9. [Xtreme Download Manager](https://github.com/subhra74/xdm)（XDM 9）— Java 内核桌面下载器
- **标注约定**：✅ 支持 · ❌ 不支持 · 🔌 依赖插件/外部引擎 · ◐ 部分支持 · ❓ 待验证。
- 仓库内已有的 Ghost Downloader 对齐表见根目录 `README.md`，本文不重复。

---

## ② 对标软件功能矩阵

### 2.1 协议支持

| 功能 | yt-dlp | aria2 | Motrix | FDM | Persepolis | AB DM | Varia | Gopeed | XDM | NetTrans |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| HTTP/HTTPS 多连接分段 | ✅（fragment 并发） | ✅ | ✅ | ✅ | ✅（64 连接） | ✅ | 🔌 aria2 | ✅ | ✅ | ✅ |
| 断点续传（Range） | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | 🔌 aria2 | ✅ | ✅ | ✅ |
| FTP / FTPS | ❌ | ✅（含 SFTP） | ✅ | ❓ | 🔌 aria2 | ❌ | 🔌 aria2 | ❌ | ❌ | ✅ |
| HLS / m3u8（VOD） | ✅ | ❌ | ❌ | 🔌 add-on | 🔌 yt-dlp | ❓ | 🔌 yt-dlp | ❌ | ✅（含 MP4 混流） | ✅（TS 直拼，无混流） |
| MPEG-DASH | ✅ | ❌ | ❌ | 🔌 add-on | 🔌 yt-dlp | ❓ | 🔌 yt-dlp | ❌ | ✅ | ✅（template/list/base） |
| 直播 / 加密 HLS | ✅（--live-from-start） | ❌ | ❌ | ❌ | 🔌 yt-dlp | ❓ | 🔌 yt-dlp | ❌ | ❌ | ❌（诚实 Unsupported） |
| BitTorrent 数据面 | ❌ | ✅（DHT/PEX/MSE/webseed） | ✅ | ✅ | ❌ | ❌ | 🔌 libtorrent | ✅（DHT/uTP/webseed） | ❌ | 🔌 外部引擎槽 |
| Magnet / .torrent 元数据 | ❌ | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ | ✅ | ❌ | ✅（bencode + magnet 解析） |
| eD2k | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | 🔌 链接解析 + 外部引擎槽 |
| Metalink 3/4（含分块校验） | ❌ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| RTMP 等旧协议 | ✅（rtmpdump） | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| 镜像源 / 多源同下 | ❌ | ✅（多 URI） | ✅ | ✅（多镜像并发） | ❌ | ❌ | ❌ | ✅（GitHub 镜像） | ❌ | ✅（MirrorResolver + 大小/ETag 校验） |

### 2.2 站点解析与视频处理

| 功能 | yt-dlp | aria2 | Motrix | FDM | Persepolis | AB DM | Varia | Gopeed | XDM | NetTrans |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 站点提取器（一站点一文件） | ✅（1000+ 站） | ❌ | 🔌 URL Resolver 插件 | 🔌 add-on | 🔌 yt-dlp | ❌ | 🔌 yt-dlp | 🔌 JS 扩展 | ✅（内置视频抓取） | ✅（Bilibili / X / YouTube，3 站） |
| 页面 HTML 兜底嗅探直链 | ❌ | ❌ | 🔌 Page Scraper 插件 | ❌ | ❌ | ❌ | ❌ | ❌ | ✅（批量按类型抓链接） | ◐（HttpDriver m3u8/mpd body sniff） |
| 音视频混流（merge） | ✅（ffmpeg） | ❌ | ❌ | ✅（内置转换） | 🔌 ffmpeg | ❌ | 🔌 ffmpeg | ❌ | ✅（自研 mux，免 ffmpeg） | ❌（外部引擎槽可承载） |
| 字幕下载 / 字幕轨 | ✅（含自动字幕） | ❌ | ❌ | ❌ | 🔌 yt-dlp | ❌ | 🔌 yt-dlp | ❌ | ✅（.srt 导出） | ❌ |
| 章节缩略图 / 元数据 / SponsorBlock | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| 批量（列表页 / 主页批量） | ✅（playlist） | ◐（-i 文本文件） | ❌ | ❌ | ❌ | ❌ | ❌ | ◐（批量操作） | ✅（社媒主页一键批量） | ❌ |

### 2.3 调度、队列与速度控制

| 功能 | yt-dlp | aria2 | Motrix | FDM | Persepolis | AB DM | Varia | Gopeed | XDM | NetTrans |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 多任务并发槽 + 优先级 | ❌ | ◐（--max-concurrent-downloads） | ✅ | ✅ | ✅ | ✅（队列顺序执行） | ✅ | ✅ | ✅ | ✅（priority + MaxSimultaneous） |
| 队列分组（分组启停/顺序） | ❌ | ❌ | ◐ | ✅ | ✅ | ✅（Queue 实体） | ✅ | ◐（分类） | ✅ | ❌（只有全局槽位） |
| 定时开始 / 停止（调度器） | ❌ | ❌ | ❌ | ✅（含定时启停应用/断网） | ✅ | ✅（队列级 Scheduler） | ✅（定时下载） | ❌ | ✅ | ◐（仅「夜间不限速」时间窗，非任务启停） |
| 全局限速 | ✅（-r） | ✅ | ✅ | ✅ | 🔌 aria2 | ✅ | 🔌 aria2 | ✅ | ✅ | ✅（token bucket） |
| 单任务限速 | ✅ | ✅ | ✅ | ✅ | 🔌 aria2 | ❌ | 🔌 aria2 | ✅ | ❌ | ✅ |
| 限速多档位 profile | ❌ | ❌ | ✅（多 profile） | ✅（流量模式：低/中/高） | ❌ | ❌ | ❌ | ❌ | ❌ | ❌（有夜间窗口） |
| 完成后动作（关机/休眠/命令） | ❌ | ❌ | ❌（有系统通知） | ✅（定时启停应用） | ✅（完成后关机） | ❌ | ✅（完成后关机） | ✅（下载后脚本） | ✅（完成后关机） | ❌ |

### 2.4 浏览器集成与入口

| 功能 | yt-dlp | aria2 | Motrix | FDM | Persepolis | AB DM | Varia | Gopeed | XDM | NetTrans |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 浏览器扩展接管下载 | ❌ | ❌（RPC 生态） | ✅（Chrome/Edge/Firefox） | ✅ | ✅（add-on） | ✅（自动捕获） | ✅（Firefox/Chrome） | ✅ | ✅（Chrome/FF/Edge 等） | ❌（仅应用内嗅探 sheet） |
| 剪贴板监视 / 粘贴批量 | ❌ | ❌ | ❌ | ✅ | ✅ | ❌ | ❌ | ❌ | ✅（单链接或列表） | ◐（剪贴板 URL 监视，单条） |
| 本地 RPC / REST API | ❌ | ✅（JSON-RPC/WS） | ✅（MDXP，JSON-RPC 2.0） | ❌ | ❌ | ❌ | ❌ | ✅（REST + webhook + MCP） | ✅（无头 CLI） | ❌ |
| CLI / 无头模式 | ✅（本体即 CLI） | ✅（守护进程） | ✅（@motrix/cli + Docker server） | ❌ | ❌ | ❌ | ❌ | ✅ | ✅（--headless） | ❌ |
| 协议关联（magnet: 等 deep link） | ❌ | ❌ | ✅（magnet: / motrix:// / .torrent） | ✅（magnet:） | ❌ | ✅ | ✅（magnet:） | ✅ | ✅ | ❌ |
| 远程控制 | ❌ | ✅（RPC 远端） | ✅（无头 server + 设备配对） | ✅（Android 遥控） | ❌ | ❌ | ❌ | ✅（Web UI/移动端） | ❌ | ❌ |

### 2.5 校验、安全与任务管理 UX

| 功能 | yt-dlp | aria2 | Motrix | FDM | Persepolis | AB DM | Varia | Gopeed | XDM | NetTrans |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 哈希校验（下载完成） | ❌ | ✅（Metalink 分块校验） | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ✅（完成窗内校验） | ✅（MD5/SHA 全套 + 本地签名库） |
| 期望哈希输入（用户/边车文件比对） | ❌ | ✅（Metalink） | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ◐ | ❌（待验证：仅签名库同源比对） |
| Cookie 导入（Netscape 文件） | ✅（含 cookies-from-browser） | ✅（导入/导出） | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌（仅手工 header） |
| 自定义 UA / headers | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ✅ | ❌ | ✅（KernelOptions） |
| 代理（HTTP/SOCKS） | ✅ | ✅（含 FTP-over-HTTP 代理） | ✅ | ✅ | 🔌 aria2 | ❓ | 🔌 aria2 | ✅ | ✅（含 NTLM） | ✅（http/socks5 + RFC1929） |
| 磁盘空间预检 / 满盘处理 | ❌ | ◐（磁盘缓存） | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ✅（满盘停止且保留进度） | ◐（预分配写，异常即停；无显式预检） |
| Mark-of-the-Web（来源标记） | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ |
| 分类文件夹 | ✅（输出模板目录） | ❌ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅（分类） | ✅（规则分类） | ✅（FoldersByCategory） |
| 批量导入（文本/剪贴板多条） | ◐ | ✅（-i） | ❌ | ❌ | ❌ | ❌ | ❌ | ✅（批量操作） | ✅ | ◐（Batch sheet；文本文件导入待验证） |
| 重命名 / 文件名模板 | ✅（输出模板） | ✅（--out） | 🔌 Filename Template 插件 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ◐（重命名有；模板无） |
| 崩溃/重启后会话恢复 | ❌ | ✅（--save-session） | ✅（SQLite 会话） | ✅ | ✅ | ✅ | ✅ | ✅（任务恢复） | ✅ | ✅（checkpoint，重启待验证 UI 侧恢复） |
| 系统级通知 | ❌ | ❌ | ✅（通知中心） | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ◐（应用内 toast lane） |
| 小文件直写/免临时文件 | ❌ | ✅（磁盘缓存） | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ✅（直写最终文件） | ✅（4MB 阈值内存缓冲，一次性落盘） |
| 多语言 UI | ❌ | ✅ | ✅ | ✅（24 语言） | ✅ | ✅ | ✅ | ✅（20+） | ✅ | ❌（中文为主） |

### 2.6 BT / PT 能力

| 功能 | aria2 | Motrix | FDM | Varia | Gopeed | NetTrans |
| --- | --- | --- | --- | --- | --- | --- |
| BT 数据面（上传/做种） | ✅ | ✅ | ✅ | 🔌 libtorrent | ✅（ratio/时长做种配额） | 🔌 外部引擎槽 |
| 多文件种子选择性下载 | ✅ | ✅（per-file） | ✅ | ✅ | ✅ | ❌（元数据可解析 file list，未透传给引擎） |
| Tracker 列表管理与自动更新 | ❌ | ✅（自动更新 + 健康检查） | ❌ | ❌ | ✅（tracker 管理） | ❌ |
| UPnP / NAT-PMP 端口映射 | ❌ | ✅ | ❌ | ❌ | ❌ | ❌ |
| 种子文件解析 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅（bencode；PT 解析脱敏） |
| 私有种子（PT）安全处理 | ❌ | ◐（privacy 保留） | ❌ | ❌ | ❌ | ✅（SensitiveUrls 脱敏） |

---

## ③ NetTrans 已有能力（对照基准）

依据根目录 `README.md` 与 `NetTrans.Kernel/` 代码（`KernelOptions.cs`、`Protocol/`、`Transfer/`、`Sites/`）：

- **协议驱动**：HTTP/HTTPS 多连接分段（超分 4× 连接、上限 64）+ Range 断点续传（`.nettrans.json` checkpoint，大小/ETag 校验后才信任恢复）；FTP/FTPS（`EnableSsl`）；HLS VOD（master→最高码率、EXT-X-MAP，TS 直拼）；DASH（SegmentTemplate/Timeline、SegmentList、SegmentBase）；直播与加密 HLS 诚实返回 Unsupported；BT/ed2k 元数据（bencode + magnet 解析，PT 种子解析脱敏），数据面走 `ExternalEngine` 槽（`EngineRunner` 委托或 `{url}/{path}/{name}` 命令模板，未配置则显式失败）。
- **调度与速度**：优先级 + `MaxSimultaneous` 并发槽；全局/单任务双级限速（token bucket，运行中可改）；「夜间不限速」时间窗（`UncappedAtNight`，跨午夜）。
- **网络策略**：http(s)/socks5 代理（RFC1929 用户名密码、userinfo 凭据）；镜像源（`MirrorResolver`，候选经大小 + ETag 校验后入源集，失败降级）；自定义 UA/headers/cookies（`DefaultHeaders`）；TLS handler 工厂槽（浏览器指纹需外部栈）；小文件（默认 ≤4MB）全内存缓冲一次性落盘。
- **校验与安全**：MD5/SHA 全套（`Checksums`）、本地签名库（`HashDatabase`，URL→指纹历史自动比对）、敏感 URL 脱敏（`SensitiveUrls`）。
- **站点提取器框架**：一站点一文件（`ISiteExtractor`，Bilibili / X / YouTube 已实现），`SiteApiBases` 可覆写 API 端点；直链交给协议驱动再探测。
- **UI/入口**（WinUI 3 壳）：新建/批量/种子/嗅探/设置 sheet；剪贴板 URL 监视；检查器（分块/连接/日志）；分类文件夹；重命名/打开/显示/校验经 toast 反馈；托盘、贴边隐藏、老板键；`NewVersionInfo` 新版本探测。

---

## ④ 缺失 / 需增强功能 spec

> **优先级判据**：P0 = 绝大多数对标软件的必备项且是 NetTrans 当前的体验断点，net8.0 BCL 可实现；P1 = 显著对齐对标项、有清晰 BCL 路径；P2 = 锦上添花或小众场景。所有条目均满足「零 NuGet（外部引擎调用除外）、不做混流器等重活」。验收标准均写成可转 headless 测试用例（沿用 `tests/` 的 Check/That 断言风格）。

### P0

| # | 功能 | 验收标准（一句话） | 对标 |
| --- | --- | --- | --- |
| P0-1 | **本地控制 API（RPC 端口）**：`HttpListener`（BCL）提供最小 REST/JSON 接口：`add`（含 headers/cookies/优先级）、`pause/resume/remove`、`list`、事件轮询或 SSE。 | 启动内核后对 `http://127.0.0.1:<port>/tasks` 发 POST 建任务、GET 列任务、DELETE 删任务；未授权来源（非 localhost）返回 403；端口占用时可配置回退端口。 | aria2、Motrix（MDXP）、Gopeed、XDM |
| P0-2 | **浏览器接管入口**（依赖 P0-1）：注册 `nettrans://` 协议关联 + magnet: 关联 + .torrent 关联，并提供官方浏览器扩展对接本地 API 的最小契约（扩展本体独立仓库交付，本仓库只保证 API 契约与握手 token）。 | shell 内执行 `nettrans://add?url=…&sha256=…` 后内核 `TaskAdded` 事件收到该 URL 且参数完整；无效 token 的注册请求被拒绝。 | Motrix、AB DM、XDM、FDM、Varia、Gopeed（8/9 款有浏览器入口） |
| P0-3 | **队列分组与定时启停**：任务可归入命名队列；每条队列可设 `HH:mm` 开始/停止时间（可跨午夜），到点自动批量启停该队列内任务；全局并发槽语义不变。 | 给定队列 A（3 个暂停任务）设 1 分钟后开始的调度，时钟到点后 A 内任务按优先级进入运行态、队列外任务不受影响；测试用可注入时钟验证启停边界（含跨午夜窗口）。 | AB DM、FDM、XDM、Persepolis、Varia |
| P0-4 | **完成后动作**：任务级 + 全局级「完成后执行」配置：系统通知 / 关机 / 休眠 / 退出程序 / 运行自定义命令（命令模板经用户显式开启，防止注入）。 | 内核 `TaskCompleted` 触发后，配置的动作 runner 收到（任务路径、退出码）并被调用恰好一次；「队列全部完成再关机」以队列为单位判定；测试以注入 runner 断言，不真关机。 | XDM、Varia、FDM、Gopeed（下载后脚本） |
| P0-5 | **yt-dlp 解析适配器（外部引擎）**：新增一个 `ISiteExtractor` 实现，把 yt-dlp（`-J` dump-json）作为外部引擎调用，将其 JSON 结果映射为 `SiteMedia`（流/字幕/标题），直链仍由 NetTrans 协议驱动传输；未安装 yt-dlp 时诚实降级回自研提取器。 | 对固定 fixture URL，适配器返回的 `SiteMedia.Streams` 与 fixture JSON 的 formats 一一对应（画质标签排序正确）；yt-dlp 二进制不存在时 `ExtractAsync` 返回 null 且框架回退到内置提取器，不抛未处理异常。 | Varia、Persepolis、Motrix（URL Resolver 插件）；对齐 yt-dlp 的站点覆盖 |

### P1

| # | 功能 | 验收标准（一句话） | 对标 |
| --- | --- | --- | --- |
| P1-1 | **Cookie 导入（Netscape 格式）**：任务级/全局加载 Netscape cookie 文件，按域匹配注入 Cookie 头。 | 解析样例 cookie 文件后，对 `example.com` 的探测请求携带正确 `Cookie` 头；`#HttpOnly_` 行与过期条目被正确处理/剔除。 | aria2、yt-dlp |
| P1-2 | **期望哈希校验**：新建任务可填期望 MD5/SHA-1/SHA-256（或自动读取同名 `.md5`/`.sha256` 边车文件），完成时自动比对，不符则任务置为「校验失败」状态并保留文件不覆盖。 | 下载内容哈希与期望值不一致时任务状态为校验失败且错误信息含两种哈希值；一致时 `TaskCompleted` 正常触发。 | aria2（Metalink）、XDM；NetTrans 签名库的显式输入面 |
| P1-3 | **磁盘空间预检**：建任务与续传前用 `DriveInfo` 检查目标盘剩余空间，不足则任务排队等待并给出明确错误，恢复有空间后自动续跑。 | 目标盘可用空间 < 预分配大小时任务不进入运行态且错误信息含「需要 X / 剩余 Y」；模拟释放空间后任务自动恢复。 | XDM（满盘停止且保留进度） |
| P1-4 | **系统级 toast 通知**：完成/失败经 Windows App SDK AppNotification 通知中心（应用已有该 SDK 依赖，不新增 NuGet），设置页可关。 | 完成一个任务后通知回调收到标题=文件名；设置关闭后无系统通知仅保留应用内 toast。 | Motrix、Gopeed、XDM 等（8/9 款） |
| P1-5 | **HLS/DASH 字幕轨下载**：解析 `EXT-X-MEDIA:TYPE=SUBTITLES` 与 DASH subtitle adaptation set，字幕作为可选流下载（TS/fMP4 内封装轨拆出 vtt/txt 待验证；纯外置 .vtt/.srt 先做）。 | fixture m3u8 含字幕 rendition 时，`SiteMedia`/计划中列出字幕轨且能完整下载落盘；无字幕轨的播放列表行为不变。 | XDM（.srt）、yt-dlp |
| P1-6 | **BT per-file 选择透传**：`TransferPlan.ExternalEngine` 增加可选文件列表字段（种子/磁力元数据已可解析 file list），引擎命令模板支持 `{files}` 占位符展开为引擎参数（如 aria2 `--select-file`）。 | 双文件种子里只勾选第 1 个时，生成的命令中 `{files}` 展开为对应索引且未勾选文件不出现在落盘目录（fixture 引擎验证参数展开）。 | Motrix、Gopeed、aria2 |
| P1-7 | **批量导入增强**：剪贴板多条 URL 一次入队；从 .txt（每行一条，支持 `#` 注释）导入；导入时可选统一保存目录与队列。 | 3 行 URL 的文本文件导入后产生 3 个任务且归属指定队列；空行/注释行被跳过。 | aria2（-i）、XDM（剪贴板列表）、Gopeed |
| P1-8 | **站点解析兜底增强**：把 HttpDriver 的 body sniff 升级为通用页面直链提取（`<a href>` 按 FileKind 过滤、`<video>/<source>`、内联 m3u8/mpd 引用），作为站点提取器框架的兜底层。 | fixture HTML 含一个 pdf 直链和一个 m3u8 引用时，URL 提交后自动列出两类候选且最优流可正常完成传输。 | XDM（批量按类型抓链接）、Motrix（Page Scraper 插件） |

### P2

| # | 功能 | 验收标准（一句话） | 对标 |
| --- | --- | --- | --- |
| P2-1 | **Metalink 4 解析**：解析 `.meta4`/`.metalink` 的多 URL + 分块哈希，复用镜像源集与分块校验。 | fixture meta4 生成含 2 个镜像源与 piece 哈希的传输计划，分块校验失败可定位到分段号。 | aria2 |
| P2-2 | **限速档位 profile**：全局限速保存多个命名档位（如「浏览优先 1MB/s / 全速 / 夜间全速」），一键切换并即时生效。 | 切换档位后 1 个 tick 内活动任务的实际限速值变为档位值（SpeedLimiter 重调已支持，仅需 UI/存储层）。 | Motrix（多 profile）、FDM（流量模式） |
| P2-3 | **文件名模板**：完成前按模板重命名（如 `{站点}/{标题}.{ext}`），冲突策略沿用现有碰撞命名。 | 模板 `{站点}-{标题}.mp4` 应用于 fixture 任务后落盘文件名与模板渲染结果一致，非法字符被替换。 | Motrix（Filename Template 插件）、yt-dlp（输出模板） |
| P2-4 | **Mark-of-the-Web**：Windows 上完成时写 `Zone.Identifier` ADS（或等效 API），来源标记可关闭。 | 完成文件读出 `ZoneId=3`；开关关闭后无 ADS。（Windows-only，测试在 CI 跳过） | XDM |
| P2-5 | **ZIP 部分下载**：HTTP Range 读取 ZIP central directory，列出成员按需下载（依赖服务器支持 Range）。 | fixture ZIP（10 成员）只下载指定 2 个成员时，字节数远小于整包且两个文件可解出正确内容。 | FDM（Partial ZIP） |
| P2-6 | **站点批量抓取**：在站点提取器框架上新增「列表页/主页」类提取（一次返回多任务草稿），仅限已适配站点。 | fixture 主页 URL 解析出 N 条媒体草稿，用户勾选后一次入队 N 个任务。 | XDM（社媒批量）、yt-dlp（playlist） |
| P2-7 | **哈希值展示与手动比对**：完成窗/检查器展示 MD5/SHA-1/SHA-256 并支持粘贴期望值即时比对。 | 粘贴正确哈希显示「匹配」，错误哈希显示「不匹配」且两个值都展示。 | XDM（校验窗口） |
| P2-8 | **过期链接刷新重试**：HTTP 410/403 过期类响应时按可配置策略重新走一遍探测（HLS/DASH 已有 playlist 指纹机制，补 HTTP 链接路径）。 | fixture 服务器先返回 403 后换新 URL 成功，任务最终完成且旧 URL 的已下载分段（若有 Range 支持的新 URL）被复用。 | XDM（refresh expired links） |
| P2-9 | **多语言 UI 基础设施**：WinUI 资源字符串抽取 + `en/zh-CN` 两语言起步。 | 切换语言后主界面 7 个工具栏图标 tooltip 与设置页文案全部变化（资源存在性测试）。 | 全部对标（普遍 20+ 语言） |

---

## ⑤ 明确不建议做的功能及理由

| 功能 | 理由 |
| --- | --- |
| **内置音视频混流器 / 格式转换**（对齐 XDM 自研 mux、FDM 转换） | ffmpeg 领域的重活，任务约束明确「不做混流器这类重活除非用外部引擎」；P0-5 的 yt-dlp 适配器与 ExternalEngine 槽（可下发 ffmpeg 命令）已覆盖同一需求面。 |
| **自研 BT/eD2k 数据面**（DHT/uTP/PEX/做种） | libtorrent 级工程量与风险，且外部引擎槽已把该边界显式化；NetTrans 的差异化（调度/校验/站点解析）不在此。 |
| **浏览器 TLS 指纹模拟内置** | .NET SslStream 无法模仿浏览器握手（README 已明确）；`HttpHandlerFactory` 槽保留外部栈注入点即可。 |
| **UPnP / NAT-PMP 内置端口映射** | 只在 BT 数据面有意义，而 BT 数据面已外包给外部引擎；独立实现的收益不抵 NAT 边界设备的长尾兼容成本。 |
| **整站爬取（HTML Spider）/ Site Explorer 目录树下载** | 爬虫与下载器职责混淆，滥用与版权风险高；站点提取器框架 + P1-8 兜底层覆盖目标场景。 |
| **网盘私有 API / 云账号体系**（百度网盘、OneDrive 私有接口等） | 各家 API 与风控持续变动，维护成本高；公开页面统一走站点提取器路线。待验证的项目（如 AB DM 的 HLS 支持）也不在列。 |
| **NTLM 代理 / 服务器认证** | 企业场景小众，BCL 无现成高层 API，P/Invoke 实现复杂度与收益不成比；http/socks5 + Basic/Digest（经 userinfo）已覆盖绝大多数场景。 |
| **远程控制 / 移动端遥控**（对齐 FDM Android 遥控、Motrix headless server、Gopeed Web UI） | 需要常驻服务、鉴权与跨设备配对的完整安全面，超出当前单机桌面定位；P0-1 本地 API 是其前置，留待后续评估。 |
| **RTP/RTSP 等长尾流协议** | BCL 无栈，yt-dlp 也仅靠外部 rtmpdump；出现需求时经 ExternalEngine 槽下发。 |
| **应用内视频预览**（FDM 特色） | 需要解码器栈，属重活；Windows 上用「打开方式」交给系统播放器零成本。 |

---

## 附：不确定项（待验证清单）

- AB Download Manager 是否已支持 HLS/m3u8 任务类型（官网未列，社区版有讨论）→ 标 ❓。
- NetTrans 应用内核对 UI 侧「重启后任务列表恢复」的完整程度（checkpoint 在文件层已验证，任务记录持久化待验证）。
- HLS fMP4 字幕轨拆出为 .vtt 的可行性（P1-5 先做外置字幕）。
- `HttpListener` 在目标 Windows 环境的 URL ACL 要求（P0-1 落地时确认 `netsh http` 预留或改用 `TcpListener` 自实现）。
