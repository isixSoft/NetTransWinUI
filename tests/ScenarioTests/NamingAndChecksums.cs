using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EndToEndTests;
using NetTrans.Kernel;
using NetTrans.Kernel.Protocol;
using NetTrans.Kernel.Protocol.Drivers;
using NetTrans.Kernel.Transfer;
namespace ScenarioTests;

internal static partial class Program
{
    // ── 场景 10：命名与校验的日常 ────────────────────────────────────────
    // 同名文件不覆盖（(1) 改写）、显式文件目标、Content-Disposition 与
    // filename* 命名、MD5/SHA-1/SHA-256 多算法校验、签名库「签名变化」
    // 提示、损坏/外来断点兜底、镜像源探测与降级、请求头合并、自动分类。

    private static async Task NamingChecksumMirrorAsync(TestServer server)
    {
        var root = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions
        {
            ConnectionsPerTask = 2,
            MaxSimultaneous = 2,
            DefaultSavePath = root,
            FoldersByCategory = true,
            HashDatabasePath = Path.Combine(root, "hashes.json"),
            DefaultHeaders = new Dictionary<string, string> { ["Referer"] = server.Base },
            MirrorResolver = (url, ct) => Task.FromResult<IReadOnlyList<string>>(url.Contains("solo.bin")
                ? new List<string>()
                : new List<string>
                {
                    server.Base + "day10/mirror.bin",
                    server.Base + "day10/mirror-bad.bin",
                    server.Base + "day10/mirror-etag.bin",
                    "http://127.0.0.1:1/mirror-dead.bin",
                }),
        });

        // ① 防碰撞：同名再下一遍 → same.bin 之外改写为 same (1).bin
        var same1 = Payload(64 * 1024, 0x11);
        var same2 = Payload(64 * 1024, 0x22);
        server.Map("day10/one/same.bin", server.RangeFile(same1, etag: "\"same1\""));
        server.Map("day10/two/same.bin", server.RangeFile(same2, etag: "\"same2\""));
        var sameDir = TempDir();
        var first = kernel.Add(new NewDownloadRequest(server.Base + "day10/one/same.bin", sameDir, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => first.Status == DownloadStatus.Completed, "首遍下载应完成", 60_000);
        var second = kernel.Add(new NewDownloadRequest(server.Base + "day10/two/same.bin", sameDir, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => second.Status == DownloadStatus.Completed, "二遍下载应完成", 60_000);
        That(File.ReadAllBytes(Path.Combine(first.SavePath, "same.bin")).SequenceEqual(same1), "先到者用原名");
        That(File.ReadAllBytes(Path.Combine(second.SavePath, "same (1).bin")).SequenceEqual(same2), "后到者改写为 (1)");

        // ② 显式文件目标（SaveTo 自带扩展名）→ 原样落盘
        var explicitTarget = Path.Combine(TempDir(), "explicit.bin");
        server.Map("day10/explicit-src.bin", server.RangeFile(Payload(32 * 1024, 0x33), etag: "\"explicit\""));
        var explicitTask = kernel.Add(new NewDownloadRequest(server.Base + "day10/explicit-src.bin", explicitTarget, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => explicitTask.Status == DownloadStatus.Completed, "显式目标应完成", 60_000);
        That(File.Exists(explicitTarget), "显式文件目标应原样使用");

        // ③ Content-Disposition：quoted filename（含空格）与 filename*（UTF-8 百分号编码）
        var cdData = Payload(48 * 1024, 0x44);
        server.Map("day10/cd.bin", ctx =>
        {
            ctx.Response.Headers["Content-Disposition"] = "attachment; filename=\"setup 1.0.exe\"";
            if (ctx.Request.HttpMethod == "HEAD") { TestServer.ServeHead(ctx, cdData, "\"cd\""); return; }
            TestServer.ServeRange(ctx, cdData, etag: "\"cd\"");
        });
        var cdTask = kernel.Add(new NewDownloadRequest(server.Base + "day10/cd.bin", "", "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => cdTask.Status == DownloadStatus.Completed, "Content-Disposition 任务应完成", 60_000);
        Equal("setup 1.0.exe", cdTask.Name, "quoted filename 应成为任务名");
        That(File.Exists(Path.Combine(root, "doc", "setup 1.0.exe")), "应按新名字落盘（分类在添加时按 URL 名判定 → doc）");

        var starData = Payload(48 * 1024, 0x55);
        server.Map("day10/star.bin", ctx =>
        {
            ctx.Response.Headers["Content-Disposition"] = "attachment; filename*=UTF-8''%E6%8A%A5%E5%91%8A.pdf";
            if (ctx.Request.HttpMethod == "HEAD") { TestServer.ServeHead(ctx, starData, "\"star\""); return; }
            TestServer.ServeRange(ctx, starData, etag: "\"star\"");
        });
        var starTask = kernel.Add(new NewDownloadRequest(server.Base + "day10/star.bin", "", "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => starTask.Status == DownloadStatus.Completed, "filename* 任务应完成", 60_000);
        Equal("报告.pdf", starTask.Name, "filename* 百分号编码应解码");
        That(File.Exists(Path.Combine(root, "doc", "报告.pdf")), "应按解码名落盘（pdf → doc 分类）");

        // ④ MD5 / SHA-1 校验（长度判别算法）
        var md5Data = Payload(24 * 1024, 0x66);
        server.Map("day10/md5.bin", server.RangeFile(md5Data, etag: "\"md5\""));
        var md5Task = kernel.Add(new NewDownloadRequest(server.Base + "day10/md5.bin", TempDir(), "", 2, TaskPriority.Normal,
            StartNow: true, ExpectedHash: Convert.ToHexString(MD5.HashData(md5Data))));
        await WaitAsync(() => md5Task.Status == DownloadStatus.Completed, "MD5 任务应完成", 60_000);
        await WaitAsync(() => md5Task.Checksum == "MD5 已校验", "MD5 校验应通过", 15_000);

        var sha1Data = Payload(24 * 1024, 0x88);
        server.Map("day10/sha1.bin", server.RangeFile(sha1Data, etag: "\"sha1\""));
        var sha1Task = kernel.Add(new NewDownloadRequest(server.Base + "day10/sha1.bin", TempDir(), "", 2, TaskPriority.Normal,
            StartNow: true, ExpectedHash: Convert.ToHexString(SHA1.HashData(sha1Data))));
        await WaitAsync(() => sha1Task.Status == DownloadStatus.Completed, "SHA-1 任务应完成", 60_000);
        await WaitAsync(() => sha1Task.Checksum == "SHA-1 已校验", "SHA-1 校验应通过", 15_000);

        // ⑤ 签名库「签名变化」：同 URL 内容变了 → 完成但显著标注
        var v1 = Payload(64 * 1024, 0x99);
        var v2 = Payload(64 * 1024, 0xAA);
        server.Map("day10/evolve.bin", server.RangeFile(v1, etag: "\"evolve1\""));
        var evolveDir = TempDir();
        var evolve1 = kernel.Add(new NewDownloadRequest(server.Base + "day10/evolve.bin", evolveDir, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => evolve1.Status == DownloadStatus.Completed, "首版应完成", 60_000);
        await WaitAsync(() => evolve1.Checksum == "SHA-256 已校验", "首版应入库", 15_000);
        server.Map("day10/evolve.bin", server.RangeFile(v2, etag: "\"evolve2\""));
        var evolve2 = kernel.Add(new NewDownloadRequest(server.Base + "day10/evolve.bin", TempDir(), "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => evolve2.Status == DownloadStatus.Completed, "改版应完成", 60_000);
        Equal("SHA-256 已校验（签名变化）", evolve2.Checksum, "内容变化应显著标注");
        That(evolve2.Log.Any(l => l.IsError && l.Message.Contains("不一致")), "签名变化应写错误日志");

        // ⑥ 损坏断点：垃圾 JSON 兜底为「无断点」，下载照常完成
        var corruptDir = TempDir();
        var corruptSrc = Payload(48 * 1024, 0xBB);
        server.Map("day10/corrupt.bin", server.RangeFile(corruptSrc, etag: "\"corrupt\""));
        await File.WriteAllTextAsync(Path.Combine(corruptDir, "corrupt.bin.nettrans.json"), "{ not json");
        var corruptTask = kernel.Add(new NewDownloadRequest(server.Base + "day10/corrupt.bin", corruptDir, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => corruptTask.Status == DownloadStatus.Completed, "损坏断点不应阻碍下载", 60_000);
        That(File.ReadAllBytes(Path.Combine(corruptDir, "corrupt.bin")).SequenceEqual(corruptSrc), "损坏断点下字节仍精确");

        // ⑦ 外来断点：同目录遗留别人的断点 → 不得误续传
        var foreignDir = TempDir();
        var foreignSrc = Payload(48 * 1024, 0xCC);
        server.Map("day10/foreign.bin", server.RangeFile(foreignSrc, etag: "\"foreign\""));
        var foreignCp = Path.Combine(foreignDir, "foreign.bin.nettrans.json");
        await File.WriteAllTextAsync(foreignCp, JsonSerializer.Serialize(new TransferCheckpoint
        {
            Kind = "ranges",
            Url = "http://other.example/x",
            Size = 99,
            Start = new long[] { 0 },
            End = new long[] { 98 },
            Done = new long[] { 40 },
        }));
        var foreignTask = kernel.Add(new NewDownloadRequest(server.Base + "day10/foreign.bin", foreignDir, "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => foreignTask.Status == DownloadStatus.Completed, "外来断点不应误续传", 60_000);
        That(File.ReadAllBytes(Path.Combine(foreignDir, "foreign.bin")).SequenceEqual(foreignSrc), "外来断点下字节仍精确");

        // ⑧ 镜像源：主源 + 好镜像 + 坏尺寸 + 坏指纹 + 死镜像 → 只收好镜像；
        //    「solo」任务的解析器返回空表 → 无镜像照样下载。
        var primary = Payload(600 * 1024, 0xDD);
        server.Map("day10/primary.bin", server.RangeFile(primary, etag: "\"mirror-set\""));
        server.Map("day10/mirror.bin", server.RangeFile(primary, etag: "\"mirror-set\""));
        server.Map("day10/mirror-bad.bin", server.RangeFile(Payload(64 * 1024, 0xEE), etag: "\"mirror-bad\""));
        server.Map("day10/mirror-etag.bin", server.RangeFile(primary, etag: "\"etag-mismatch\""));
        var mirrorTask = kernel.Add(new NewDownloadRequest(server.Base + "day10/primary.bin", TempDir(), "", 3, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => mirrorTask.Status == DownloadStatus.Completed, "镜像任务应完成", 90_000);
        That(mirrorTask.Log.Any(l => l.Message.Contains("镜像探测：1/4")), $"应只收 1 个可用镜像（{string.Join(" | ", mirrorTask.Log.Select(l => l.Message))}）");
        That(mirrorTask.Log.Any(l => l.Message.Contains("个源")), "计划描述应报告镜像数量");
        That(File.ReadAllBytes(Path.Combine(mirrorTask.SavePath, "primary.bin")).SequenceEqual(primary), "镜像下载字节精确");

        var soloSrc = Payload(48 * 1024, 0xDF);
        server.Map("day10/solo.bin", server.RangeFile(soloSrc, etag: "\"solo\""));
        var soloTask = kernel.Add(new NewDownloadRequest(server.Base + "day10/solo.bin", TempDir(), "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => soloTask.Status == DownloadStatus.Completed, "空镜像列表任务应完成", 60_000);
        That(soloTask.Log.All(l => !l.Message.Contains("镜像探测")), "空镜像列表不应触发镜像探测");
        That(File.ReadAllBytes(Path.Combine(soloTask.SavePath, "solo.bin")).SequenceEqual(soloSrc), "无镜像下载字节精确");

        // ⑨ 请求头合并：默认 Referer + 任务 Cookie 同时到达服务器
        var observedCookies = new List<string>();
        var headerData = Payload(32 * 1024, 0x0F);
        server.Map("day10/auth.bin", server.RangeFile(headerData, etag: "\"auth\"", onGet: (n, ctx) =>
        {
            lock (EventLock) observedCookies.Add(ctx.Request.Headers["Cookie"] ?? "");
            return false;
        }));
        var authTask = kernel.Add(new NewDownloadRequest(server.Base + "day10/auth.bin", TempDir(), "", 1, TaskPriority.Normal,
            StartNow: true, Headers: new Dictionary<string, string> { ["Cookie"] = "session=abc" }));
        await WaitAsync(() => authTask.Status == DownloadStatus.Completed, "带头任务应完成", 60_000);
        lock (EventLock)
            That(observedCookies.Any(c => c.Contains("session=abc")), $"Cookie 应随请求到达（实际 {string.Join(",", observedCookies)}）");

        // ⑩ 自动分类：无分类任务按扩展名落 music/doc/soft 目录；8 连接的小任务
        server.Map("day10/song.flac", server.RangeFile(Payload(16 * 1024, 0x10), etag: "\"song\""));
        server.Map("day10/song.mp3", server.RangeFile(Payload(16 * 1024, 0x30), etag: "\"song-mp3\""));
        server.Map("day10/pic.jpg", server.RangeFile(Payload(16 * 1024, 0x40), etag: "\"pic\""));
        server.Map("day10/note.txt", server.RangeFile(Payload(16 * 1024, 0x50), etag: "\"note\""));
        server.Map("day10/tool.zip", server.RangeFile(Payload(16 * 1024, 0x20), etag: "\"tool\""));
        var song = kernel.Add(new NewDownloadRequest(server.Base + "day10/song.flac", "", "", 1, TaskPriority.Normal, StartNow: true));
        var songMp3 = kernel.Add(new NewDownloadRequest(server.Base + "day10/song.mp3", "", "", 8, TaskPriority.Normal, StartNow: true));
        var pic = kernel.Add(new NewDownloadRequest(server.Base + "day10/pic.jpg", "", "", 8, TaskPriority.Normal, StartNow: true));
        var note = kernel.Add(new NewDownloadRequest(server.Base + "day10/note.txt", "", "", 1, TaskPriority.Normal, StartNow: true));
        var tool = kernel.Add(new NewDownloadRequest(server.Base + "day10/tool.zip", "", "", 1, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => song.Status == DownloadStatus.Completed && tool.Status == DownloadStatus.Completed &&
                             songMp3.Status == DownloadStatus.Completed && pic.Status == DownloadStatus.Completed &&
                             note.Status == DownloadStatus.Completed,
            "自动分类任务应完成", 60_000);
        That(File.Exists(Path.Combine(root, "music", "song.flac")), "flac 应落 music 目录");
        That(File.Exists(Path.Combine(root, "music", "song.mp3")), "mp3 应落 music 目录");
        That(File.Exists(Path.Combine(root, "doc", "pic.jpg")), "jpg 应落 doc 目录");
        That(File.Exists(Path.Combine(root, "doc", "note.txt")), "txt 应落 doc 目录");
        That(File.Exists(Path.Combine(root, "soft", "tool.zip")), "zip 应落 soft 目录");
        Equal("#FF2D55", song.Tint, "音乐类型应有专属配色");

        // ⑩b 整块边界：恰好 1MB、单连接 → DirectSink 写合并整块边界
        server.Map("day10/boundary.bin", server.RangeFile(Payload(1024 * 1024, 0x60), etag: "\"boundary\""));
        var boundary = kernel.Add(new NewDownloadRequest(server.Base + "day10/boundary.bin", TempDir(), "", 1, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => boundary.Status == DownloadStatus.Completed, "整块边界任务应完成", 60_000);
        That(File.ReadAllBytes(Path.Combine(boundary.SavePath, "boundary.bin")).SequenceEqual(Payload(1024 * 1024, 0x60)),
            "整块边界任务字节精确");

        // ⑪ 校验清单与指纹判别：GNU/BSD 两种行格式 + 注释/空行/坏行/归一化
        var md5Hex = Convert.ToHexString(MD5.HashData(md5Data));
        var sha1Hex = Convert.ToHexString(SHA1.HashData(sha1Data));
        var sha256Hex = Convert.ToHexString(SHA256.HashData(primary));
        var manifest = Checksums.ParseChecksumFile(
            "# 注释行应跳过\n" +
            "\n" +
            $"{md5Hex} *ubuntu.iso\n" +
            $"{sha1Hex}  ./dir/report.pdf\n" +
            $"MD5 (tool\\bin\\setup.exe) = {md5Hex.ToLowerInvariant()}\n" +
            $"SHA256 (big.iso) = {sha256Hex.ToLowerInvariant()}\n" +
            $"SHA1 (nested.iso) = {sha1Hex.ToLowerInvariant()}\n" +
            "deadbeef badline.bin\n" +
            $"MD5 (././dotted.iso) = {md5Hex.ToLowerInvariant()}\n");
        Equal(6, manifest.Count, "6 条有效条目应入库，坏行跳过");
        var ubuntu = manifest.Single(e => e.FileName == "ubuntu.iso");
        Equal("MD5", ubuntu.Algorithm, "GNU 行 + 二进制标记应识别");
        Equal(md5Hex, ubuntu.Hash, "哈希字段不受 * 前缀影响");
        Equal("SHA-1", manifest.Single(e => e.FileName == "dir/report.pdf").Algorithm, "./ 前缀应归一");
        Equal("MD5", manifest.Single(e => e.FileName == "tool/bin/setup.exe").Algorithm, "BSD 反斜杠路径应归一");
        Equal("SHA-256", manifest.Single(e => e.FileName == "big.iso").Algorithm, "BSD SHA256 条目应识别");
        Equal("SHA-1", manifest.Single(e => e.FileName == "nested.iso").Algorithm, "BSD SHA1 条目应识别");
        That(manifest.All(e => e.Hash.All(char.IsAsciiHexDigit)), "无指纹的坏行不得入库");

        That(Checksums.DetectAlgorithm(null) is null, "null 应判别失败");
        That(Checksums.DetectAlgorithm(" ") is null, "空白应判别失败");
        That(Checksums.DetectAlgorithm("zzzz") is null, "非 hex 短串应判别失败");
        That(Checksums.DetectAlgorithm(new string('g', 32)) is null, "32 位非 hex 应判别失败");
        That(Checksums.DetectAlgorithm(new string('a', 48)) is null, "48 位长度应判别失败");
        Equal("MD5", Checksums.DetectAlgorithm(md5Hex), "32 位 hex → MD5");
        Equal("SHA-1", Checksums.DetectAlgorithm(sha1Hex), "40 位 hex → SHA-1");
        Equal("SHA-256", Checksums.DetectAlgorithm(sha256Hex), "64 位 hex → SHA-256");

        var probeFile = Path.Combine(TempDir(), "probe.bin");
        await File.WriteAllBytesAsync(probeFile, md5Data);
        var md5DataSha256 = Convert.ToHexString(SHA256.HashData(md5Data));
        Equal(md5Hex, Checksums.Compute("md5", probeFile), "md5 算法名（小写）");
        Equal(Convert.ToHexString(SHA1.HashData(md5Data)), Checksums.Compute("sha-1", probeFile), "sha-1 算法名");
        Equal(md5DataSha256, Checksums.Compute("sha-256", probeFile), "sha-256 算法名");
        Equal(md5DataSha256, Checksums.Compute("weird", probeFile), "未知算法按 SHA-256 兜底");

        // ⑫ 脱敏与兜底工具：边界输入一律返回安全值
        Equal("", SensitiveUrls.Mask(null), "null 链接应脱敏为空");
        Equal("", SensitiveUrls.Mask(""), "空链接应脱敏为空");
        Equal("magnet:", SensitiveUrls.Mask("magnet:"), "无查询串的 magnet 原样返回");
        Equal("magnet:?xt", SensitiveUrls.Mask("magnet:?xt"), "无值的参数不应崩溃");
        Equal("http://host/a?a=1", SensitiveUrls.Mask("http://host/a?a=1"), "无 % 的参数值原样返回");
        Equal("未命名下载", DriverHelpers.GuessName("#frag-only"), "仅片段名应退回默认名");
        Equal("a", DriverHelpers.GuessName("http://host/a#frag"), "片段应被剥掉");
        Equal("host", DriverHelpers.GuessName("http://host/"), "裸主机名应作为默认名");
        Equal("download", DriverHelpers.BaseName("http://host/.bin"), "纯扩展名应退回 download");
        Equal("http://abs.example/x", DriverHelpers.ResolveUrl("http://abs.example/x", "http://host/a/"), "绝对引用原样返回");
        Equal("http://host/a/rel.bin", DriverHelpers.ResolveUrl("rel.bin", "http://host/a/"), "相对引用应拼接");
        Equal("rel.bin", DriverHelpers.ResolveUrl("rel.bin", "::bad"), "坏基址应原样返回");
        Equal(DownloadProtocol.Http, ProtocolDetector.Detect("https://a/x"), "https 归入 http 驱动");
        Equal(DownloadProtocol.Http, ProtocolDetector.Detect(""), "空链接归入 http 驱动兜底");
        Equal(DownloadProtocol.Dash, ProtocolDetector.Detect("http://a/x.mpd"), "mpd 扩展名归入 DASH 驱动");
        Equal(DownloadProtocol.BitTorrent, ProtocolDetector.Detect("magnet:?x"), "magnet 归入 BT 驱动");
        Equal(DownloadProtocol.Ed2k, ProtocolDetector.Detect("ed2k://|file|x|1|h|/"), "ed2k 归入 eD2k 驱动");
        Equal(DownloadProtocol.Ftp, ProtocolDetector.Detect("ftp://host/x.iso"), "ftp 归入 FTP 驱动");
    }

    /// <summary>手工 bencode：{info:{name,length}, announce, announce-list}。</summary>
}
