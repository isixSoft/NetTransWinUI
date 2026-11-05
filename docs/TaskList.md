# NetTrans 功能 TaskList（TDD 执行序）

> 来源：[FeatureSpec.md](FeatureSpec.md)（对标 9 款同类软件，2026-11-05 调研）。
> 执行纪律：每圈严格 **先写测试（红）→ 最小实现（绿）→ 11 套件全量回归 → 提交推送**。
> 约束延续：Kernel/测试零 NuGet、net8.0 BCL；不碰 WinUI 工程；中文注释英文代码；提交署名 isixSoft + Co-authored-by traeagent。
> 进度：圈 1–13（P0 全部 + P1 全部）已完成并推送（HEAD 24ce35d）。P2 依序提圈，见下表。

## TDD 圈（按依赖与可测性排序）

| 圈 | 条目 | 测试落点 | 关键测试先行内容 |
| --- | --- | --- | --- |
| 1 | P0-5 yt-dlp 解析适配器（`ISiteExtractor`，`-J` JSON → `SiteMedia`；无 yt-dlp 诚实降级 null） | NetworkTests/SiteExtractors.cs 增 partial 段 | fixture dump-json：formats→Streams 排序、字幕条目映射、缺二进制→null 回退内置提取器、坏 JSON→抛异常回退 |
| 2 | P0-4 完成后动作（任务级+全局 runner 注入；「队列全完成」判定） | NetworkTests/KernelLifecycle.cs 增段 | 注入 runner 收到（任务名/产物路径/动作）恰一次；多任务全完成才触发；异常动作不崩内核 |
| 3 | P0-3 队列分组与定时启停（可注入时钟；跨午夜窗口） | NetworkTests/KernelLifecycle.cs 增段 | 队列 A 定时到点批量启动、队列外不受影响；跨午夜窗口边界；停止窗口批量暂停 |
| 4 | P0-1 本地控制 API（HttpListener REST：add/pause/resume/remove/list；仅 localhost 403；端口回退） | NetworkTests/ControlApi.cs 新 partial | POST /tasks 建任务→GET 列表含之→DELETE 移除；非 loopback 来源 403；端口占用回退 |
| 5 | P0-2 浏览器接管入口（依赖圈4：`nettrans://` 解析 + 握手 token 校验） | NetworkTests/ControlApi.cs 增段 | `nettrans://add?url=…&sha256=…` 解析入队、参数完整；无效 token 拒绝；协议注册本体=UI 侧（文档化，不碰 WinUI） |
| 6 | P1-2 期望哈希校验（显式输入 + 边车文件；失败态不覆盖） | NetworkTests/ChecksumsAndPrivacy.cs 增段 | 不匹配→校验失败态+错误含两哈希；匹配→Completed 正常 |
| 7 | P1-1 Cookie 导入（Netscape 解析，按域注入） | NetworkTests/DetectionAndNaming.cs 增段 | `#HttpOnly_` 行、过期剔除；对 fixture 域的请求头携带正确 Cookie |
| 8 | P1-3 磁盘空间预检（DriveInfo；不足排队+明确错误；释放后自动续跑） | NetworkTests/ResilienceAndLimits.cs 增段 | 注入剩余空间探测；不足不进运行态；释放后恢复 |
| 9 | P1-5 HLS/DASH 字幕轨（外置 .vtt/.srt 优先） | NetworkTests/StreamingProtocols.cs 增段 | fixture m3u8 含 EXT-X-MEDIA 字幕 → 字幕轨列出且可下载；无字幕行为不变 |
| 10 | P1-6 BT per-file 透传（{files} 占位符展开） | ExternalEngineTests/Program.cs 增段 | 双文件种子勾选 1 → 命令含 --select-file 对应索引 |
| 11 | P1-7 批量导入（txt 每行一条，# 注释；剪贴板多条） | NetworkTests/KernelLifecycle.cs 增段 | 3 行文件→3 任务归指定队列；空行注释跳过 |
| 12 | P1-8 通用页面直链兜底（`<a>/<video>/<source>` 过滤） | NetworkTests/SiteExtractors.cs 增段 | fixture HTML：pdf 直链+m3u8 引用→两类候选、最优流可传输 |
| 13 | P1-4 系统通知（headless：通知服务接口 + 设置开关；AppNotification 接线归 UI 侧） | UiLogicTests 或 SetupTests（不碰 WinUI 工程，测服务契约） | 开关开→回调收到标题；关→无通知 |

## Backlog（P2，按需启动，不排圈）

P2-1 Metalink4 · P2-2 限速档位 · P2-3 文件名模板 · P2-4 Mark-of-the-Web（Windows-only 测试跳过）· P2-5 ZIP 部分下载 · P2-6 站点批量抓取 · P2-7 哈希展示比对 · P2-8 过期链接刷新 · P2-9 多语言基础设施。

## P2 TDD 圈（按依赖与可测性排序，延续同纪律）

| 圈 | 条目 | 测试落点 | 关键测试先行内容 |
| --- | --- | --- | --- |
| 14 | P2-1 Metalink4（`.meta4/.metalink` → file/size/hash/url；镜像探测期验尺寸；内嵌哈希作期望指纹） | NetworkTests/ChecksumsAndPrivacy.cs 增段 | fixture meta4：priority 定主源+镜像列表+Size/Name/Hash；全链路下载完成且「SHA-256 已校验」；尺寸不符镜像被剔除 |

## 明确不做

见 [FeatureSpec.md §⑤](FeatureSpec.md)：内置混流器、自研 BT 数据面、TLS 指纹、UPnP、整站爬取、网盘私有 API、NTLM 代理、远程控制、RTSP、应用内视频预览。

## 待验证（落地前确认）

- `HttpListener` URL ACL：圈 4 落地时确认 `netsh http` 预留需求，否则改 TcpListener 自实现（fixture 测试不受影响）。
- HLS fMP4 字幕拆 .vtt 可行性：圈 9 先做外置字幕。
- UI 侧任务记录持久化完整度：与圈 5 文档化一起核对（不碰 WinUI）。
