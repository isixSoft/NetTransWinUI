using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using NetTrans.Kernel;
using NetTrans.Kernel.Net;
using NetTrans.Kernel.Protocol;
using NetTrans.Kernel.Protocol.Drivers;
using NetTrans.Kernel.Transfer;
namespace NetworkTests;

internal sealed partial class Program
{
    // ── 16. 哈希校验（Ubuntu SHA256SUMS 模式） ─────────────────────────────

    private static async Task ChecksumsUnitAsync()
    {
        // 算法识别：长度 + 十六进制字符集
        Equal("MD5", Checksums.DetectAlgorithm("d41d8cd98f00b204e9800998ecf8427E"), "32 位十六进制 → MD5");
        Equal("SHA-1", Checksums.DetectAlgorithm("DEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEF"), "40 位十六进制 → SHA-1");
        Equal("SHA-256", Checksums.DetectAlgorithm(new string('a', 64)), "64 位十六进制 → SHA-256");
        Equal((string?)null, Checksums.DetectAlgorithm(new string('g', 32)), "非十六进制 → null");
        Equal((string?)null, Checksums.DetectAlgorithm(new string('a', 33)), "33 位 → null");
        Equal((string?)null, Checksums.DetectAlgorithm(""), "空串 → null");
        Equal((string?)null, Checksums.DetectAlgorithm(null), "null → null");

        // 校验清单解析：GNU 行（含 * 二进制标记、./ 前缀）+ BSD 行 + 注释 + 坏行
        var manifest = """
            # SHA256SUMS for noble (注释与空行应忽略)

            d41d8cd98f00b204e9800998ecf8427e  ubuntu.iso
            e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855 *./pool/test.deb
            MD5 (file.bin) = d41d8cd98f00b204e9800998ecf8427e
            SHA256 (x.iso) = E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855
            这是一行无法识别的乱码
            nothex  filename.zip
            """;
        var entries = Checksums.ParseChecksumFile(manifest);
        Equal(4, entries.Count, $"应解析出 4 条有效指纹（实际 {entries.Count}）");
        That(entries.Any(e => e.FileName == "ubuntu.iso" && e.Algorithm == "MD5"),
            "GNU 行应解析出 ubuntu.iso/MD5");
        That(entries.Any(e => e.FileName == "pool/test.deb" && e.Algorithm == "SHA-256" && e.Hash.StartsWith("E3B0")),
            "./ 前缀应剥离、* 标记应忽略（pool/test.deb/SHA-256）");
        That(entries.Any(e => e.FileName == "file.bin" && e.Algorithm == "MD5"), "BSD 行 MD5 应解析");
        That(entries.Any(e => e.FileName == "x.iso" && e.Hash == e.Hash.ToUpperInvariant()), "BSD 行 SHA-256 大写哈希应统一大写");

        // 同文件名后行覆盖前行（镜像里常见多算法清单同名场景按最后一次为准）
        var overrideEntries = Checksums.ParseChecksumFile(
            "0123456789abcdef0123456789abcdef  a.bin\n" +
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  a.bin");
        Equal(1, overrideEntries.Count, "同名指纹应去重");
        Equal("SHA-256", overrideEntries[0].Algorithm, "同名后行覆盖前行");

        await Task.CompletedTask;
    }

    private static async Task ExpectedHashAsync(TestServer server)
    {
        var sha256Data = Payload(256 * 1024);
        var md5Data = Payload(128 * 1024);
        var plain = Payload(64 * 1024);
        server.Map("hash/sha.bin", server.RangeFile(sha256Data, etag: "\"hash-sha\""));
        server.Map("hash/md5.bin", server.RangeFile(md5Data, etag: "\"hash-md5\""));
        server.Map("hash/bad.bin", server.RangeFile(plain, etag: "\"hash-bad\""));

        // SHA-256 期望指纹：匹配 → Completed + Checksum「已校验」
        var shaHex = Convert.ToHexString(SHA256.HashData(sha256Data)).ToLowerInvariant();
        using (var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 2, MaxSimultaneous = 1 }))
        {
            var task = kernel.Add(new NewDownloadRequest(server.Base + "hash/sha.bin", TempDir(), "", 2,
                TaskPriority.Normal, StartNow: true, ExpectedHash: shaHex));
            await WaitAsync(() => task.Status == DownloadStatus.Completed, "SHA-256 匹配任务应完成", 30_000);
            Equal("SHA-256 已校验", task.Checksum, "匹配时 Checksum 应为「SHA-256 已校验」");
            That(task.Log.Any(l => l.Message.Contains("已记录签名")), "首次下载应记录签名");
        }

        // MD5 期望指纹：32 位自动判别为 MD5
        var md5Hex = Convert.ToHexString(MD5.HashData(md5Data));
        using (var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1 }))
        {
            var task = kernel.Add(new NewDownloadRequest(server.Base + "hash/md5.bin", TempDir(), "", 1,
                TaskPriority.Normal, StartNow: true, ExpectedHash: md5Hex));
            await WaitAsync(() => task.Status == DownloadStatus.Completed, "MD5 匹配任务应完成", 30_000);
            Equal("MD5 已校验", task.Checksum, "32 位期望指纹应按 MD5 校验");
        }

        // 期望指纹不匹配：任务必须转 Error，错误信息含期望/实际指纹
        using (var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1 }))
        {
            var wrong = new string('0', 64);
            var task = kernel.Add(new NewDownloadRequest(server.Base + "hash/bad.bin", TempDir(), "", 1,
                TaskPriority.Normal, StartNow: true, ExpectedHash: wrong));
            await WaitAsync(() => task.Status == DownloadStatus.Error, "指纹不匹配应转 Error", 30_000);
            That(task.ErrorMessage!.Contains("SHA-256 校验失败") && task.ErrorMessage!.Contains("期望") && task.ErrorMessage!.Contains("实际"),
                $"错误信息应说明期望/实际指纹（实际「{task.ErrorMessage}」）");
            Equal("SHA-256 校验失败", task.Checksum, "Checksum 应标注校验失败");
            That(task.Log.Any(l => l.IsError), "日志应有失败行");
        }
    }

    private static async Task HashDatabaseAsync(TestServer server)
    {
        var data = Payload(128 * 1024);
        server.Map("db/file.bin", server.RangeFile(data, etag: "\"db-1\""));
        var dbPath = Path.Combine(TempDir(), "signatures", ".nettrans-checksums.json");

        // 第一次下载：记录签名 + 落盘
        using (var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1, HashDatabasePath = dbPath }))
        {
            var task = kernel.Add(new NewDownloadRequest(server.Base + "db/file.bin", TempDir(), "", 1, TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status == DownloadStatus.Completed, "首次下载应完成", 30_000);
            That(task.Log.Any(l => l.Message.Contains("已记录签名")), "首次下载日志应「已记录签名」");
        }
        That(File.Exists(dbPath), "签名库文件应落盘");
        var raw = File.ReadAllText(dbPath);
        That(raw.Contains(server.Base + "db/file.bin") && raw.Contains("SHA-256"), "签名库 JSON 应含 URL 与算法");

        // 第二次下载同 URL：与历史签名一致
        using (var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1, HashDatabasePath = dbPath }))
        {
            var task = kernel.Add(new NewDownloadRequest(server.Base + "db/file.bin", TempDir(), "", 1, TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status == DownloadStatus.Completed, "二次下载应完成", 30_000);
            That(task.Log.Any(l => l.Message.Contains("与历史签名一致")),
                $"二次下载应「与历史签名一致」，实际日志：{string.Join(" | ", task.Log.Select(l => l.Message))}");
            Equal("SHA-256 已校验", task.Checksum, "一致时 Checksum 不应带「签名变化」");
        }

        // 换内容再下（同 URL，同长度异字节）：签名变化要显著标注（完成但日志记警告）
        var tampered = data.Select(b => (byte)(b ^ 0x5A)).ToArray();
        server.Map("db/file.bin", server.RangeFile(tampered, etag: "\"db-2\""));
        using (var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1, HashDatabasePath = dbPath }))
        {
            var task = kernel.Add(new NewDownloadRequest(server.Base + "db/file.bin", TempDir(), "", 1, TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status == DownloadStatus.Completed, "内容变化下载仍应完成（换源属正常业务）", 30_000);
            That(task.Checksum!.Contains("签名变化"), $"Checksum 应标注签名变化（实际「{task.Checksum}」）");
            That(task.Log.Any(l => l.IsError && l.Message.Contains("与历史签名不一致")), "日志应有「与历史签名不一致」警告行");
        }

        // 损坏签名库：降级为空库重建，不影响下载
        File.WriteAllText(dbPath, "{这不是 JSON");
        using (var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1, HashDatabasePath = dbPath }))
        {
            var task = kernel.Add(new NewDownloadRequest(server.Base + "db/file.bin", TempDir(), "", 1, TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status == DownloadStatus.Completed, "损坏签名库下下载应照常完成", 30_000);
            That(task.Log.Any(l => l.Message.Contains("已记录签名")), "损坏库应降级重建（重新记录签名）");
            That(!File.ReadAllText(dbPath).StartsWith("{这不是 JSON"), "损坏库应被重建为合法 JSON");
        }
    }

    // ── 17d/17e. 边车哈希（P1-2）：同名 .md5/.sha256 自动读取 ─────────────

    private static async Task SidecarRemoteAsync(TestServer server)
    {
        var okSha = Payload(96 * 1024);
        var okMd5 = Payload(64 * 1024);
        var bad = Payload(48 * 1024);
        var bare = Payload(16 * 1024);
        server.Map("side/oksha.bin", server.RangeFile(okSha, etag: "\"side-sha\""));
        server.Map("side/okmd5.bin", server.RangeFile(okMd5, etag: "\"side-md5\""));
        server.Map("side/bad.bin", server.RangeFile(bad, etag: "\"side-bad\""));
        server.Map("side/bare.bin", server.RangeFile(bare, etag: "\"side-bare\""));

        var shaHex = Convert.ToHexString(SHA256.HashData(okSha)).ToLowerInvariant();
        var md5Hex = Convert.ToHexString(MD5.HashData(okMd5)).ToLowerInvariant();
        // GNU coreutils 清单行（sha256sum 输出样式）
        server.Map("side/oksha.bin.sha256", ctx =>
            TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes($"{shaHex}  oksha.bin\n"), "text/plain"));
        // 纯哈希（极简发布页样式）
        server.Map("side/okmd5.bin.md5", ctx =>
            TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(md5Hex), "text/plain"));
        // 错哈希边车（发布页抄错指纹的样式）
        server.Map("side/bad.bin.sha256", ctx =>
            TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(new string('0', 64)), "text/plain"));

        // 远端 .sha256 边车：自动读取 → 匹配 → Completed + TaskCompleted 恰一次
        int completed = 0;
        using (var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 2, MaxSimultaneous = 1 }))
        {
            kernel.TaskCompleted += _ => Interlocked.Increment(ref completed);
            var task = kernel.Add(new NewDownloadRequest(server.Base + "side/oksha.bin", TempDir(), "", 2,
                TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status == DownloadStatus.Completed, "边车匹配任务应完成", 30_000);
            Equal(shaHex, task.ExpectedHash, "边车指纹应回填到任务");
            Equal("SHA-256 已校验", task.Checksum, "边车校验应标注「SHA-256 已校验」");
            That(task.Log.Any(l => l.Message.Contains("边车")),
                $"日志应说明指纹来自边车（实际：{string.Join(" | ", task.Log.Select(l => l.Message))}）");
            await WaitAsync(() => Volatile.Read(ref completed) == 1, "TaskCompleted 应触发恰一次", 5_000);
        }

        // 远端 .md5 边车（纯哈希格式）：按 MD5 校验
        using (var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1 }))
        {
            var task = kernel.Add(new NewDownloadRequest(server.Base + "side/okmd5.bin", TempDir(), "", 1,
                TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status == DownloadStatus.Completed, "MD5 边车任务应完成", 30_000);
            Equal(md5Hex, task.ExpectedHash, ".md5 边车指纹应回填");
            Equal("MD5 已校验", task.Checksum, "32 位边车指纹应按 MD5 校验");
        }

        // 边车不匹配：Error + 错误含期望/实际两哈希 + 已下载文件保留不覆盖
        using (var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1 }))
        {
            var dir = TempDir();
            var task = kernel.Add(new NewDownloadRequest(server.Base + "side/bad.bin", dir, "", 1,
                TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status == DownloadStatus.Error, "边车不匹配应转 Error", 30_000);
            That(task.ErrorMessage!.Contains("校验失败") && task.ErrorMessage!.Contains("期望") && task.ErrorMessage!.Contains("实际"),
                $"错误信息应含期望/实际哈希（实际「{task.ErrorMessage}」）");
            var kept = Path.Combine(dir, task.Name);
            That(File.Exists(kept) && new FileInfo(kept).Length == bad.Length,
                "失败态必须保留已下载文件（失败态不覆盖）");
            That(task.Log.Any(l => l.IsError), "日志应有失败行");
        }

        // 无边车任务完全不受影响：不伪造期望指纹、照常完成
        using (var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1 }))
        {
            var task = kernel.Add(new NewDownloadRequest(server.Base + "side/bare.bin", TempDir(), "", 1,
                TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status == DownloadStatus.Completed, "无边车任务应照常完成", 30_000);
            Equal(null, (object?)task.ExpectedHash, "无边车时不应伪造期望指纹");
        }
    }

    private static async Task SidecarLocalUnitAsync(TestServer server)
    {
        // ParseSidecar 单元：纯哈希 / GNU 行 / BSD 行 / 规范化 / 拒绝路径
        var hex = "a1b2c3d4" + new string('e', 56);
        Equal(hex, Checksums.ParseSidecar(hex), "纯哈希直接返回");
        Equal(hex, Checksums.ParseSidecar(hex.ToUpperInvariant() + "\n"), "大写与尾换行规范化为小写");
        Equal(hex, Checksums.ParseSidecar($"{hex}  setup.iso\r\n"), "GNU 行取首字段");
        Equal(hex, Checksums.ParseSidecar($"SHA256 (setup.iso) = {hex}\n"), "BSD 行取等号后字段");
        Equal(null, Checksums.ParseSidecar(null), "null → null");
        Equal(null, Checksums.ParseSidecar("   \n\n"), "空白 → null");
        Equal(null, Checksums.ParseSidecar("# generated\nreadme.txt  安装说明\n"), "无哈希行 → null");

        var data = Payload(80 * 1024);
        var wrongSha = new string('f', 64);
        server.Map("side/local.bin", server.RangeFile(data, etag: "\"side-local\""));
        server.Map("side/both.bin", server.RangeFile(data, etag: "\"side-both\""));
        // 远端边车是错哈希：本地同名边车必须赢（本地优先）
        server.Map("side/both.bin.sha256", ctx =>
            TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(wrongSha), "text/plain"));
        var shaHex = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        // 本地保存目录同名 .sha256（GNU 行）→ 自动读取并校验
        using (var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1 }))
        {
            var dir = TempDir();
            File.WriteAllText(Path.Combine(dir, "local.bin.sha256"), $"{shaHex}  local.bin\n");
            var task = kernel.Add(new NewDownloadRequest(server.Base + "side/local.bin", dir, "", 1,
                TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status == DownloadStatus.Completed, "本地边车任务应完成", 30_000);
            Equal(shaHex, task.ExpectedHash, "本地边车指纹应回填");
            Equal("SHA-256 已校验", task.Checksum, "本地边车应参与校验");
        }

        // 本地边车优先于远端：远端错哈希不得把任务拖进 Error
        using (var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1 }))
        {
            var dir = TempDir();
            File.WriteAllText(Path.Combine(dir, "both.bin.sha256"), $"{shaHex}  both.bin\n");
            var task = kernel.Add(new NewDownloadRequest(server.Base + "side/both.bin", dir, "", 1,
                TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status == DownloadStatus.Completed, "本地边车应优先于远端", 30_000);
            Equal(shaHex, task.ExpectedHash, "应以本地边车指纹为准");
        }
    }

    // ── 18. PT 支持（脱敏 / tracker / 做种参数） ──────────────────────────

    private static async Task SensitiveUrlsUnitAsync()
    {
        Equal("http://pt.site/download?id=1&passkey=***&uid=***&name=big.iso",
            SensitiveUrls.Mask("http://pt.site/download?id=1&passkey=SECRET&uid=42&name=big.iso"),
            "http 查询串敏感参数遮蔽、普通参数保留");
        Equal("http://pt.site/dl?PassKey=X&TOKEN=Y&authkeyid=9".Replace("X", "***").Replace("Y", "***").Replace("9", "***"),
            SensitiveUrls.Mask("http://pt.site/dl?PassKey=X&TOKEN=Y&authkeyid=9"),
            "参数名大小写不敏感");
        Equal("http://pt.site/file.iso", SensitiveUrls.Mask("http://pt.site/file.iso"), "无查询串原样返回");

        // magnet：tr= 是 announce URL，解码后双层遮蔽，展示可读
        var magnet = "magnet:?xt=urn:btih:0123456789abcdef&dn=big.iso" +
                     "&tr=https%3a%2f%2fpt.site%2fannounce%3fpasskey%3dTOPSECRET" +
                     "&tr=udp%3a%2f%2ftracker.public%3a1337%2fannounce";
        var masked = SensitiveUrls.Mask(magnet);
        That(masked.StartsWith("magnet:?"), "magnet 头保留");
        That(!masked.Contains("TOPSECRET"), "嵌套 passkey 必须遮蔽");
        That(masked.Contains("passkey=***"), "遮蔽后的 announce 可读");
        That(masked.Contains("udp://tracker.public:1337/announce"), "公开 tracker 解码展示");

        Equal("", SensitiveUrls.Mask(null), "null → 空串");
        Equal("", SensitiveUrls.Mask("  "), "空白 → 空串");
        await Task.CompletedTask;
    }

    private static async Task PtMagnetAsync(TestServer server)
    {
        const string secret = "TOPSECRET-passkey";
        var magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=pt.show.iso" +
                     $"&tr=https%3a%2f%2fpt.example%2fannounce%3fpasskey%3d{Uri.EscapeDataString(secret)}" +
                     "&tr=udp%3a%2f%2ftracker.public%3a1337%2fannounce";

        // 未接引擎插槽：BT 任务走 Failed → Error，但脱敏/tracker 字段必须就位
        using var kernel = new RealDownloadKernel(new KernelOptions { MaxSimultaneous = 1 });
        var task = kernel.Add(new NewDownloadRequest(magnet, TempDir(), "bt", 1, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => task.Status == DownloadStatus.Error, "未接引擎的 magnet 任务应失败", 30_000);

        Equal(magnet, task.Url, "原始 magnet（含 passkey）必须原样保留给引擎下发");
        That(task.DisplayUrl is not null && task.DisplayUrl.Contains("***") && !task.DisplayUrl.Contains(secret),
            $"展示链接必须脱敏（实际「{task.DisplayUrl}」）");
        That(task.Trackers is { Count: 2 }, $"tracker 列表应 2 条（实际 {task.Trackers?.Count ?? 0}）");
        That(task.Trackers![0].Contains("***") && !task.Trackers[0].Contains(secret), "私有 tracker 应脱敏");
        Equal("udp://tracker.public:1337/announce", task.Trackers![1], "公开 tracker 原样");
        Equal("pt.show.iso", task.Name, "dn= 应成为任务名");
    }

    private static async Task PtTorrentSeedAsync(TestServer server)
    {
        // 本地 .torrent（announce-list 两级 tracker）→ tracker 脱敏
        var torrentPath = Path.Combine(TempDir(), "pt.torrent");
        File.WriteAllBytes(torrentPath, BuildTorrent(
            "https://pt.example/announce?authkey=ABCDEF0123456789", "udp://tracker.public:1337/announce"));

        using (var kernel = new RealDownloadKernel(new KernelOptions { MaxSimultaneous = 1 }))
        {
            var task = kernel.Add(new NewDownloadRequest(torrentPath, TempDir(), "bt", 1, TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status == DownloadStatus.Error, "未接引擎的种子任务应失败", 30_000);
            That(task.Trackers is { Count: 2 }, $"种子 tracker 应 2 条（实际 {task.Trackers?.Count ?? 0}）");
            That(task.Trackers![0].Contains("authkey=***") && !task.Trackers[0].Contains("ABCDEF"),
                $"种子内 authkey 应脱敏（实际「{task.Trackers[0]}」）");
            Equal("udp://tracker.public:1337/announce", task.Trackers![1], "announce-list 第二条原样");
            Equal("pt.torrent.iso", task.Name, "info.name 应成为任务名");
        }

        // 做种占位符：{seed-ratio} {seed-time} 由任务参数渲染；
        // PT 底线：{url} 把真实 passkey 交给引擎（数据面），但日志必须脱敏（展示面）
        const string secret = "TOPSECRET-passkey";
        var magnet = "magnet:?xt=urn:btih:fedcba9876543210fedcba9876543210fedcba98&dn=seed.iso" +
                     $"&tr=https%3a%2f%2fpt.example%2fannounce%3fpasskey%3d{Uri.EscapeDataString(secret)}";
        var saveDir = TempDir();
        var outPath = Path.Combine(saveDir, "seed.iso");
        using (var kernel = new RealDownloadKernel(new KernelOptions
               {
                   MaxSimultaneous = 1,
                   ExternalEngineCommand = "printf '%s' '{url}' > '{path}'",
               }))
        {
            var task = kernel.Add(new NewDownloadRequest(magnet, saveDir, "bt", 1, TaskPriority.Normal,
                StartNow: true, SeedRatio: 1.5, SeedHours: 48));
            await WaitAsync(() => task.Status is DownloadStatus.Completed or DownloadStatus.Error,
                "引擎命令任务应有终态", 30_000);
            Equal(DownloadStatus.Completed, task.Status, $"引擎命令成功应完成（实际 {task.Status}：{task.ErrorMessage}）");
            var handed = File.ReadAllText(outPath);
            That(handed.Contains("urn:btih:fedcba98") && handed.Contains(secret),
                "引擎（{url}）必须拿到含真实 passkey 的完整 magnet");
            var cmdLog = task.Log.FirstOrDefault(l => l.Message.Contains("执行外部命令")).Message;
            That(cmdLog.Contains("***") && !cmdLog.Contains(secret), $"命令日志必须脱敏（实际「{cmdLog}」）");
        }
    }

}
