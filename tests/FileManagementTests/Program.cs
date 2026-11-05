// File-management behaviour tests for NetTrans.Kernel (net8.0 console):
//   1. collision-free naming (pure function + live kernel)
//   2. nested save directories are created
//   3. cross-instance checkpoint resume (暂停 → Dispose → 新内核恢复)
//   4. a checkpoint whose URL differs is ignored — fresh download
//   5. corrupt checkpoints degrade to "no checkpoint", never throw
//   6. the completion SHA-256 log line appears
// Zero package dependencies: local TestServer serves a deterministic payload.

using System.Security.Cryptography;
using FileManagementTests;
using NetTrans.Kernel;

const int MiB = 1024 * 1024;
var failures = new List<string>();
int checks = 0;

using var server = new TestServer();

// ── 1. collision-free naming ──────────────────────────────────────────────
Console.WriteLine("== 1. 防碰撞命名 ==");
CollisionNamingPure();
await CollisionNamingLive(server);

// ── 2. nested directories ─────────────────────────────────────────────────
Console.WriteLine("== 2. 目录创建 ==");
await NestedDirectory(server);

// ── 3./4./6. interrupt once, then resume vs. different URL ───────────────
Console.WriteLine("== 3./4./6. 跨实例断点续传 / URL 不同 / SHA-256 日志 ==");
var root = NewRoot();
var sharedDir = Path.Combine(root, "shared");
Directory.CreateDirectory(sharedDir);
var sharedUrl = server.BaseUrl + "/files/big.bin";
string checkpointPath;

{
    var kernelA = new RealDownloadKernel();
    var taskA = kernelA.Add(new NewDownloadRequest(sharedUrl, sharedDir, "soft", 8, TaskPriority.Normal, StartNow: false));
    kernelA.SetSpeedLimit(taskA.Id, "512 KB/s");
    kernelA.Toggle(taskA.Id); // start downloading under the cap

    await WaitUntilAsync(() => taskA.Done >= 1.5 * MiB, TimeSpan.FromSeconds(60),
        "限速下载到中段", failures, taskA);
    kernelA.Toggle(taskA.Id); // pause

    checkpointPath = Path.Combine(sharedDir, "big.bin.nettrans.json");
    await WaitUntilAsync(() => File.Exists(checkpointPath), TimeSpan.FromSeconds(15),
        "checkpoint 落盘", failures, taskA);
    Check(File.Exists(Path.Combine(sharedDir, "big.bin")), "暂停后目标文件（半截）在盘", ref checks, failures);
    kernelA.Dispose();

    // 4. a different URL must ignore the leftover checkpoint and start fresh
    var kernelX = new RealDownloadKernel();
    var taskX = kernelX.Add(new NewDownloadRequest(server.BaseUrl + "/mirror/big.bin", sharedDir,
        "soft", 8, TaskPriority.Normal, StartNow: true));
    await WaitCompletedAsync(kernelX, taskX, failures, "URL 不同的任务");
    Check(File.ReadAllBytes(Path.Combine(sharedDir, "big (1).bin")).AsSpan().SequenceEqual(TestServer.Bytes),
        "URL 不同：落到 big (1).bin 且内容完整", ref checks, failures);
    Check(!taskX.Log.Any(l => l.Message.Contains("已从断点恢复")),
        "URL 不同：日志无「已从断点恢复」", ref checks, failures);
    Check(File.Exists(checkpointPath), "URL 不同：旧 checkpoint 仍未被新任务使用", ref checks, failures);
    kernelX.Dispose();

    // 3. the same URL on a brand-new kernel resumes from the checkpoint
    var kernelB = new RealDownloadKernel();
    var taskB = kernelB.Add(new NewDownloadRequest(sharedUrl, sharedDir, "soft", 8, TaskPriority.Normal, StartNow: true));
    await WaitCompletedAsync(kernelB, taskB, failures, "跨实例恢复任务");

    Check(taskB.Log.Any(l => l.Message.Contains("已从断点恢复")),
        "跨实例：日志含「已从断点恢复」", ref checks, failures);
    Check(File.ReadAllBytes(Path.Combine(sharedDir, "big.bin")).AsSpan().SequenceEqual(TestServer.Bytes),
        "跨实例：文件字节与 TestServer.Bytes 完全一致", ref checks, failures);
    Check(!File.Exists(checkpointPath), "跨实例：完成后 checkpoint 已清除", ref checks, failures);

    // 6. SHA-256 verification line for the finished download
    var expected16 = Convert.ToHexString(SHA256.HashData(TestServer.Bytes))[..16].ToLowerInvariant();
    await WaitUntilAsync(
        () => taskB.Log.Any(l => l.Message.StartsWith("SHA-256 ") || l.Message.StartsWith("校验失败")),
        TimeSpan.FromSeconds(15), "SHA-256 校验日志出现", failures, taskB);
    Check(taskB.Log.Any(l => l.Message.StartsWith($"SHA-256 {expected16}…")),
        $"完成校验日志出现（期望 SHA-256 {expected16}…）", ref checks, failures);
    Check(!taskB.Log.Any(l => l.Message.StartsWith("校验失败")),
        "无「校验失败」日志", ref checks, failures);
    kernelB.Dispose();
}
Directory.Delete(root, recursive: true);

// ── 5. corrupt checkpoints ────────────────────────────────────────────────
Console.WriteLine("== 5. 损坏 checkpoint 容错 ==");
await CorruptCheckpoint(server, "非法 JSON", "{ this is not json !!!");
await CorruptCheckpoint(server, "缺字段空对象", "{}");
await CorruptCheckpoint(server, "JSON null", "null");
await CorruptCheckpoint(server, "半截 JSON", "{\"Kind\":\"ranges\",\"Url\":\"http://");

Console.WriteLine();
Console.WriteLine($"共 {checks} 项断言");
if (failures.Count > 0)
{
    Console.WriteLine($"FAIL：{failures.Count} 项失败");
    foreach (var failure in failures) Console.WriteLine($"  - {failure}");
    return 1;
}
Console.WriteLine("ALL PASS");
return 0;

// ── test bodies ───────────────────────────────────────────────────────────

void CollisionNamingPure()
{
    // pure function over a fake existence set — no file system involved
    static bool Taken(HashSet<string> taken, string candidate) => taken.Contains(candidate.Replace('\\', '/'));

    var taken = new HashSet<string>();
    Check(RealDownloadKernel.NextFreePath("/dl/big.bin", p => Taken(taken, p)) == "/dl/big.bin",
        "无碰撞时原名保留", ref checks, failures);

    taken = new HashSet<string> { "/dl/big.bin" };
    Check(RealDownloadKernel.NextFreePath("/dl/big.bin", p => Taken(taken, p)) == "/dl/big (1).bin",
        "big.bin 已存在 → big (1).bin", ref checks, failures);

    taken = new HashSet<string> { "/dl/big.bin", "/dl/big (1).bin" };
    Check(RealDownloadKernel.NextFreePath("/dl/big.bin", p => Taken(taken, p)) == "/dl/big (2).bin",
        "(1) 也被占 → big (2).bin", ref checks, failures);

    taken = new HashSet<string> { "/dl/big.bin", "/dl/big (1).bin", "/dl/big (2).bin" };
    Check(RealDownloadKernel.NextFreePath("/dl/big.bin", p => Taken(taken, p)) == "/dl/big (3).bin",
        "(2) 也被占 → big (3).bin（连续递增）", ref checks, failures);

    taken = new HashSet<string> { "/dl/report" };
    Check(RealDownloadKernel.NextFreePath("/dl/report", p => Taken(taken, p)) == "/dl/report (1)",
        "无扩展名 → report (1)", ref checks, failures);

    taken = new HashSet<string> { "/dl/a.tar.gz" };
    Check(RealDownloadKernel.NextFreePath("/dl/a.tar.gz", p => Taken(taken, p)) == "/dl/a.tar (1).gz",
        "多点扩展名 → a.tar (1).gz", ref checks, failures);

    taken = new HashSet<string> { "/dl/big.bin" };
    taken.Add("/dir-big"); // simulate the target being a directory
    Check(RealDownloadKernel.NextFreePath("/dl/big.bin", p => Taken(taken, p)) == "/dl/big (1).bin",
        "同名目录也算碰撞", ref checks, failures);
}

async Task CollisionNamingLive(TestServer testServer)
{
    var root = NewRoot();
    var dir = Path.Combine(root, "dl");
    Directory.CreateDirectory(dir);
    await File.WriteAllBytesAsync(Path.Combine(dir, "big.bin"), "stale original"u8.ToArray());

    using var kernel = new RealDownloadKernel();
    var first = kernel.Add(new NewDownloadRequest(testServer.BaseUrl + "/files/big.bin", dir,
        "soft", 8, TaskPriority.Normal, StartNow: true));
    await WaitCompletedAsync(kernel, first, failures, "碰撞任务 1");

    var second = kernel.Add(new NewDownloadRequest(testServer.BaseUrl + "/files/big.bin", dir,
        "soft", 8, TaskPriority.Normal, StartNow: true));
    var third = kernel.Add(new NewDownloadRequest(testServer.BaseUrl + "/files/big.bin", dir,
        "soft", 8, TaskPriority.Normal, StartNow: true));
    await WaitCompletedAsync(kernel, second, failures, "碰撞任务 2");
    await WaitCompletedAsync(kernel, third, failures, "碰撞任务 3");
    kernel.Dispose();

    Check(File.Exists(Path.Combine(dir, "big (1).bin")), "已有 big.bin → 新任务落 big (1).bin（文件存在）", ref checks, failures);
    Check(File.ReadAllBytes(Path.Combine(dir, "big (1).bin")).AsSpan().SequenceEqual(TestServer.Bytes),
        "已有 big.bin → 新任务落 big (1).bin", ref checks, failures);
    var untouched = await File.ReadAllBytesAsync(Path.Combine(dir, "big.bin"));
    Check("stale original"u8.ToArray().AsSpan().SequenceEqual(untouched),
        "原 big.bin 内容未被覆盖", ref checks, failures);
    Check(File.ReadAllBytes(Path.Combine(dir, "big (2).bin")).AsSpan().SequenceEqual(TestServer.Bytes) &&
          File.ReadAllBytes(Path.Combine(dir, "big (3).bin")).AsSpan().SequenceEqual(TestServer.Bytes),
        "连续任务落 big (2).bin、big (3).bin（共 (1)(2)(3)）", ref checks, failures);
    Directory.Delete(root, recursive: true);
}

async Task NestedDirectory(TestServer testServer)
{
    var root = NewRoot();
    var deep = Path.Combine(root, "deep", "nested", "dir");

    using var kernel = new RealDownloadKernel();
    var task = kernel.Add(new NewDownloadRequest(testServer.BaseUrl + "/files/big.bin", deep,
        "soft", 8, TaskPriority.Normal, StartNow: true));
    await WaitCompletedAsync(kernel, task, failures, "嵌套目录任务");
    kernel.Dispose();

    Check(File.Exists(Path.Combine(deep, "big.bin")), "嵌套目录被创建且文件在位", ref checks, failures);
    Check(File.ReadAllBytes(Path.Combine(deep, "big.bin")).AsSpan().SequenceEqual(TestServer.Bytes),
        "嵌套目录文件内容完整", ref checks, failures);
    Directory.Delete(root, recursive: true);
}

async Task CorruptCheckpoint(TestServer testServer, string label, string garbage)
{
    var root = NewRoot();
    var dir = Path.Combine(root, "garbage");
    Directory.CreateDirectory(dir);
    var checkpointPath = Path.Combine(dir, "big.bin.nettrans.json");
    await File.WriteAllTextAsync(checkpointPath, garbage);

    using var kernel = new RealDownloadKernel();
    var task = kernel.Add(new NewDownloadRequest(testServer.BaseUrl + "/files/big.bin", dir,
        "soft", 8, TaskPriority.Normal, StartNow: true));
    await WaitCompletedAsync(kernel, task, failures, $"损坏 checkpoint（{label}）");
    kernel.Dispose();

    Check(File.ReadAllBytes(Path.Combine(dir, "big.bin")).AsSpan().SequenceEqual(TestServer.Bytes),
        $"损坏 checkpoint（{label}）：从零下载完成", ref checks, failures);
    Check(!File.Exists(checkpointPath), $"损坏 checkpoint（{label}）：完成后已清除", ref checks, failures);
    Directory.Delete(root, recursive: true);
}

// ── helpers ───────────────────────────────────────────────────────────────

static string NewRoot() =>
    Path.Combine(Path.GetTempPath(), "nettrans-fmt-" + Guid.NewGuid().ToString("N")[..10]);

static void Check(bool ok, string name, ref int checks, List<string> failures, string detail = "")
{
    checks++;
    Console.WriteLine(ok ? $"  PASS {name}" : $"  FAIL {name}{(detail.Length > 0 ? $"（{detail}）" : "")}");
    if (!ok) failures.Add(name);
}

static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what,
    List<string> failures, DownloadTask? dump = null)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (condition()) return;
        await Task.Delay(100);
    }

    failures.Add($"超时：{what}");
    Console.WriteLine($"  FAIL 超时：{what}");
    if (dump is not null) DumpLog(dump);
}

static async Task WaitCompletedAsync(RealDownloadKernel kernel, DownloadTask task,
    List<string> failures, string label)
{
    var fresh = () => kernel.Tasks.First(t => t.Id == task.Id).Status == DownloadStatus.Completed;
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
    while (DateTime.UtcNow < deadline && !fresh()) await Task.Delay(100);

    var current = kernel.Tasks.First(t => t.Id == task.Id);
    if (current.Status != DownloadStatus.Completed)
    {
        failures.Add($"超时：{label} 未完成（{current.Status}）");
        Console.WriteLine($"  FAIL 超时：{label} 未完成（{current.Status}）");
        DumpLog(current);
        return;
    }
    Console.WriteLine($"  ·    {label} 已完成");
}

static void DumpLog(DownloadTask task)
{
    Console.WriteLine($"  ---- task #{task.Id} 日志 ----");
    foreach (var entry in task.Log) Console.WriteLine($"  [{entry.Time}] {entry.Message}");
}
