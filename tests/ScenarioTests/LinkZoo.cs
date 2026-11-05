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
    // ── 场景 9：链接动物园 ───────────────────────────────────────────────
    // 用户从论坛/发布页抄来五花八门的链接：ed2k、magnet（贫富不齐）、
    // .torrent、纯乱码、ftp 死链、扩展名伪装。内核要认得出每一种、
    // 报错报得明白、脱敏一步不漏、引擎故障如实上报。

    private static async Task LinkZooAsync(TestServer server)
    {
        // ① ed2k + 未接引擎 → 明确报错而不是装死
        using (var kernel = new RealDownloadKernel(new KernelOptions { MaxSimultaneous = 1 }))
        {
            var link = "ed2k://|file|ubuntu-24.04.iso|3145728|0123456789ABCDEF0123456789ABCDEF|/";
            var task = kernel.Add(new NewDownloadRequest(link, TempDir(), "", 1, TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status == DownloadStatus.Error, "无引擎 ed2k 应报错", 30_000);
            That(task.ErrorMessage!.Contains("外部引擎"), $"错误应指向引擎插槽（{task.ErrorMessage}）");
            That(task.DisplayUrl is not null, "ed2k 展示链接应走脱敏通道");
            That(task.Name == "ubuntu-24.04.iso", $"eD2k 链接应解析出文件名（实际 {task.Name}）");
            Equal(3145728L, task.Size, "eD2k 链接应解析出大小");
        }

        // ② 畸形 ed2k → Unsupported 计划，报错来自驱动
        using (var kernel = new RealDownloadKernel(new KernelOptions { MaxSimultaneous = 1 }))
        {
            var task = kernel.Add(new NewDownloadRequest("ed2k://|junk", TempDir(), "", 1, TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status == DownloadStatus.Error, "畸形 ed2k 应报错", 30_000);
            That(task.ErrorMessage!.Contains("eD2k"), $"错误应来自 eD2k 驱动（{task.ErrorMessage}）");
            kernel.ToggleAll(); // 失败任务批量重试：Error → 重新排队
            await WaitAsync(() => task.Status == DownloadStatus.Error, "重试后应再次报错", 30_000);
        }

        // ③ 引擎在位：magnet（贫/富/残缺）、.torrent 全家桶
        var payload = Payload(96 * 1024, 0x77);
        var root = TempDir();
        var torrent = TorrentBytes("debian.iso", 524288,
            "http://tracker1.example/announce", "udp://tracker2.example:1337/announce");
        server.Map("day9/debian.torrent", server.RangeFile(torrent, contentType: "application/octet-stream"));

        using (var kernel = new RealDownloadKernel(new KernelOptions
        {
            MaxSimultaneous = 3,
            DefaultSavePath = root,
            FoldersByCategory = true,
            HashDatabasePath = Path.Combine(TempDir(), "hashes.json"),
            EngineRunner = (plan, path, ct) => { File.WriteAllBytes(path, payload); return Task.FromResult(true); },
        }))
        {
            // 贫 magnet：只有 xt + xl → 名字退回 bt-<hash8>，大小来自 xl
            var poor = kernel.Add(new NewDownloadRequest(
                $"magnet:?xt=urn:btih:{new string('a', 40)}&xl=98304", "", "", 1, TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => poor.Status is DownloadStatus.Completed or DownloadStatus.Error, "贫 magnet 应有终态", 30_000);
            Equal(DownloadStatus.Completed, poor.Status, $"贫 magnet 应完成（{poor.Status}：{poor.ErrorMessage}）");
            Equal("bt-aaaaaaaa", poor.Name, "无 dn 的 magnet 应以 hash 前缀命名");
            Equal(98304L, poor.Size, "xl 参数应成为任务大小");
            Equal("SHA-256 已校验", poor.Checksum, "引擎产出应过 SHA-256 自检");
            That(File.Exists(Path.Combine(root, "bt", "bt-aaaaaaaa")), "产出应落在 bt 分类目录");

            // 富 magnet：dn + xl + 两种 tracker（含 passkey）→ 脱敏必须到位
            const string secret = "DAY9-SECRET-passkey";
            var rich = kernel.Add(new NewDownloadRequest(
                $"magnet:?xt=urn:btih:{new string('b', 40)}&dn=pt.day9.bin&xl=98304" +
                $"&tr=udp%3a%2f%2ftracker.example%3a1337%2fannounce" +
                $"&tr=https%3a%2f%2fpt.example%2fannounce%3fpasskey%3d{Uri.EscapeDataString(secret)}",
                "", "", 1, TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => rich.Status == DownloadStatus.Completed, "富 magnet 应完成", 30_000);
            Equal("pt.day9.bin", rich.Name, "dn 参数应成为任务名");
            That(rich.Trackers is { Count: 2 }, $"应解析出 2 个 tracker（实际 {rich.Trackers?.Count}）");
            That(rich.Trackers!.Any(t => t.Contains("***")), "含 passkey 的 tracker 应脱敏");
            That(rich.Trackers.All(t => !t.Contains(secret)), "tracker 脱敏后不得残留 passkey");
            That(rich.Trackers.Any(t => t.Contains("udp://tracker.example:1337/announce")), "无凭据 tracker 应原样展示");
            That(rich.DisplayUrl is not null && !rich.DisplayUrl.Contains(secret), "展示链接应脱敏");

            // .torrent：HTTP 取回元数据 → 解析出名字/大小/tracker → 引擎产出
            var torrentTask = kernel.Add(new NewDownloadRequest(
                server.Base + "day9/debian.torrent", "", "", 1, TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => torrentTask.Status is DownloadStatus.Completed or DownloadStatus.Error,
                ".torrent 任务应有终态", 30_000);
            Equal(DownloadStatus.Completed, torrentTask.Status,
                $".torrent 任务应完成（{torrentTask.Status}：{torrentTask.ErrorMessage}）");
            Equal("debian.iso", torrentTask.Name, "种子元数据应更正任务名");
            Equal(524288L, torrentTask.Size, "种子元数据应给出大小");
            That(torrentTask.Trackers is { Count: 2 }, "种子 tracker 应入库");
            That(torrentTask.Log.Any(l => l.Message.Contains("BT 元数据")), "应记录 BT 元数据日志");
            That(File.Exists(Path.Combine(root, "bt", "debian.iso")), "产出应落在 bt 分类目录");

            // 残缺 magnet：无 xt → dn 兜底命名，仍可完成
            var broken = kernel.Add(new NewDownloadRequest("magnet:?dn=only", "", "", 1, TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => broken.Status == DownloadStatus.Completed, "残缺 magnet 应完成", 30_000);
            Equal("only", broken.Name, "无 xt 的 magnet 应以 dn 参数兜底命名");

            // 引擎故障三连：运行失败 / 未产出文件 / 运行异常
            using (var failKernel = new RealDownloadKernel(new KernelOptions
            {
                MaxSimultaneous = 3,
                DefaultSavePath = TempDir(),
                EngineRunner = (plan, path, ct) => Task.FromResult(false),
            }))
            {
                var fail = failKernel.Add(new NewDownloadRequest(
                    $"magnet:?xt=urn:btih:{new string('c', 40)}", "", "", 1, TaskPriority.Normal, StartNow: true));
                await WaitAsync(() => fail.Status == DownloadStatus.Error, "引擎失败应报错", 30_000);
                That(fail.ErrorMessage!.Contains("外部引擎运行失败"), $"应如实上报（{fail.ErrorMessage}）");
            }

            using (var noFileKernel = new RealDownloadKernel(new KernelOptions
            {
                MaxSimultaneous = 3,
                DefaultSavePath = TempDir(),
                EngineRunner = (plan, path, ct) => Task.FromResult(true),
            }))
            {
                var noFile = noFileKernel.Add(new NewDownloadRequest(
                    $"magnet:?xt=urn:btih:{new string('d', 40)}", "", "", 1, TaskPriority.Normal, StartNow: true));
                await WaitAsync(() => noFile.Status == DownloadStatus.Error, "引擎未产出文件应报错", 30_000);
                That(noFile.ErrorMessage!.Contains("未产出文件"), $"应如实上报（{noFile.ErrorMessage}）");
            }

            using (var throwKernel = new RealDownloadKernel(new KernelOptions
            {
                MaxSimultaneous = 3,
                DefaultSavePath = TempDir(),
                EngineRunner = (plan, path, ct) => throw new InvalidOperationException("boom"),
            }))
            {
                var thrown = throwKernel.Add(new NewDownloadRequest(
                    $"magnet:?xt=urn:btih:{new string('e', 40)}", "", "", 1, TaskPriority.Normal, StartNow: true));
                await WaitAsync(() => thrown.Status == DownloadStatus.Error, "引擎异常应报错", 30_000);
                That(thrown.ErrorMessage!.Contains("boom"), $"异常消息应透传（{thrown.ErrorMessage}）");
            }
        }

        // ④ 乱码链接：HTTP 探测失败 → 明确 Error + 未知来源
        using (var kernel = new RealDownloadKernel(new KernelOptions { MaxSimultaneous = 1 }))
        {
            var task = kernel.Add(new NewDownloadRequest("not a url", TempDir(), "", 1, TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status == DownloadStatus.Error, "乱码链接应报错", 30_000);
            Equal("未知来源", task.Host, "无法解析的主机应标注未知来源");
        }

        // ⑤ ftp 死链：端口 1 立即拒绝 → Error（不悬挂）
        using (var kernel = new RealDownloadKernel(new KernelOptions { MaxSimultaneous = 1 }))
        {
            var task = kernel.Add(new NewDownloadRequest("ftp://127.0.0.1:1/pub/ubuntu.iso", TempDir(), "", 1,
                TaskPriority.Normal, StartNow: true));
            await WaitAsync(() => task.Status == DownloadStatus.Error, "ftp 死链应报错", 30_000);
            That(task.ErrorMessage is not null && task.ErrorMessage.Length > 0, "应有具体错误消息");
        }

        // ⑥ 扩展名伪装：.m3u8 / .mpd 指向 404 → 走对应驱动后报错；坏种子同理
        server.Map("day9/broken.torrent", server.RangeFile(Encoding.UTF8.GetBytes("this is not bencode"), contentType: "application/octet-stream"));
        using (var kernel = new RealDownloadKernel(new KernelOptions { MaxSimultaneous = 2 }))
        {
            var m3u8 = kernel.Add(new NewDownloadRequest(server.Base + "day9/missing.m3u8", TempDir(), "", 1,
                TaskPriority.Normal, StartNow: true));
            var mpd = kernel.Add(new NewDownloadRequest(server.Base + "day9/missing.mpd", TempDir(), "", 1,
                TaskPriority.Normal, StartNow: true));
            var brokenTorrent = kernel.Add(new NewDownloadRequest(server.Base + "day9/broken.torrent", TempDir(), "", 1,
                TaskPriority.Normal, StartNow: true, ExpectedHash: "   "));
            await WaitAsync(() => m3u8.Status == DownloadStatus.Error && mpd.Status == DownloadStatus.Error &&
                                 brokenTorrent.Status == DownloadStatus.Error,
                "伪装流媒体/坏种子链接应报错", 30_000);
        }
    }

}
