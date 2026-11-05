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

/// <summary>
/// 场景化端到端测试：以「用户的一天」为线索，把调度、断点、限速、
/// 校验、签名库、PT 引擎下发等特性串成完整使用流程，验证它们协同工作。
/// </summary>
internal static partial class Program
{
    private static int _passed, _failed;
    private static readonly List<string> Failures = new();
    private static readonly object EventLock = new();

    private static async Task<int> Main()
    {
        using var server = new TestServer();

        await Run("场景1. 新用户首日：并发上限/队列补位/分类落盘/签名库", () => FirstDayAsync(server));
        await Run("场景2. 网络抖动一天：断连→重试→续传→字节精确", () => FlakyNetworkAsync(server));
        await Run("场景3. PT 用户日常：magnet 引擎下发/脱敏/签名入库", () => PtUserAsync(server));
        await Run("场景4. 混跑 + 全局限速：跨任务共享一个总闸", () => MixedTrafficAsync(server));
        await Run("场景5. 坏文件处置：校验失败→换源重下→签名入库", () => BadFileRecoveryAsync(server));
        await Run("场景6. 进程重启：断点存活→新实例续传→零重复流量", () => RestartResumeAsync(server));
        await Run("场景7. 队列指挥室：插队/全停全续/下载中重下/移除任务", () => QueueControlAsync(server));
        await Run("场景8. 暂停续传：手动暂停落断点→恢复→零重复流量", () => PauseResumeAsync(server));
        await Run("场景9. 链接动物园：ed2k/magnet/torrent/坏链/引擎故障", () => LinkZooAsync(server));
        await Run("场景10. 命名与校验：防碰撞/响应头命名/多算法/镜像", () => NamingChecksumMirrorAsync(server));

        Console.WriteLine();
        Console.WriteLine(new string('=', 64));
        Console.WriteLine($"结果：{_passed} 通过，{_failed} 失败");
        foreach (var failure in Failures) Console.WriteLine($"  [FAIL] {failure}");
        return _failed == 0 ? 0 : 1;
    }
    private static byte[] TorrentBytes(string name, long length, params string[] announces)
    {
        static string Str(string s) => $"{Encoding.UTF8.GetByteCount(s)}:{s}";
        var info = $"d4:name{Str(name)}6:lengthi{length}ee";
        var list = "l" + string.Concat(announces.Select(a => $"l{Str(a)}e")) + "e";
        return Encoding.UTF8.GetBytes($"d4:info{info}8:announce{Str(announces[0])}13:announce-list{list}e");
    }

    // ── 基础设施 ──────────────────────────────────────────────────────────

    private static async Task Run(string name, Func<Task> body)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await body();
            Interlocked.Increment(ref _passed);
            Console.WriteLine($"[PASS] {name}（{watch.Elapsed.TotalSeconds:0.0}s）");
        }
        catch (Exception error)
        {
            Interlocked.Increment(ref _failed);
            Failures.Add($"{name} :: {error.Message}");
            Console.WriteLine($"[FAIL] {name}（{watch.Elapsed.TotalSeconds:0.0}s）");
        }
    }

    private static async Task WaitAsync(Func<bool> condition, string what, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"等待超时：{what}");
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nettrans-scenario-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>确定性载荷：同 size/seed 恒定，便于字节精确断言。</summary>
    private static byte[] Payload(int size, int seed = 1)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = (byte)(i * 31 + seed * 7);
        return data;
    }

    private static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{what}：期望 {expected}，实际 {actual}");
    }

    private static void That(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException(what);
    }
}
