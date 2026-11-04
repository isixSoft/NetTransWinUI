using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using NetTrans.Kernel.Net;
using NetTrans.Kernel.Protocol;

namespace NetTrans.Kernel.Transfer;

/// <summary>
/// External-engine mode (BT / eD2k data plane): an in-process engine runner
/// slot plus the shell-command fallback ({url} {path} {name} template).
/// </summary>
public sealed partial class TransferSession
{
    // ── external-engine mode (BT / eD2k data plane) ───────────────────────
    /// <summary>
    /// The deliberate engine slot: moving bytes for BT / eD2k needs a
    /// host-side engine, injected either as a runner delegate or as a shell
    /// command template ({url} {path} {name} {seed-ratio} {seed-time}). Success means
    /// the engine produced the file at the save path — reported exactly like a finished
    /// byte transfer; with neither slot configured the plan still fails honestly.
    /// </summary>
    private async Task<TransferOutcome> RunExternalEngineAsync(TransferPlan.ExternalEngine plan, CancellationToken cancellation)
    {
        Log($"外部引擎接管：{plan.Engine}");

        // PT 做种目标：任务参数优先，种子/驱动默认值兜底
        var seededPlan = plan with
        {
            SeedRatio = _request.SeedRatio ?? plan.SeedRatio,
            SeedHours = _request.SeedHours ?? plan.SeedHours,
        };

        if (_request.EngineRunner is { } runner)
        {
            // 引擎产物目录必须存在：命令路径（见 RunExternalEngineCommandAsync）
            // 会建目录，runner 插槽保持同一约定，不把建目录的义务甩给引擎。
            var outputDir = Path.GetDirectoryName(_request.SavePath);
            if (outputDir is { Length: > 0 }) Directory.CreateDirectory(outputDir);
            try
            {
                bool produced = await runner(seededPlan, _request.SavePath, cancellation);
                if (produced && File.Exists(_request.SavePath)) return EngineFileOutcome();
                Log(produced ? "外部引擎报告完成，但未产出文件" : "外部引擎运行失败", isError: true);
                return new TransferOutcome(TransferOutcomeKind.Failed,
                    produced ? "外部引擎未产出文件" : "外部引擎运行失败");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                Log($"外部引擎运行异常（{Brief(error.Message)}）", isError: true);
                return new TransferOutcome(TransferOutcomeKind.Failed, Brief(error.Message));
            }
        }

        return await RunExternalEngineCommandAsync(seededPlan, cancellation);
    }

    private async Task<TransferOutcome> RunExternalEngineCommandAsync(
        TransferPlan.ExternalEngine plan, CancellationToken cancellation)
    {
        // PT 做种占位符：{seed-ratio} 分享率、{seed-time} 小时数；
        // 未设置时替换为 0（aria2 语义：不限做种），模板自行决定是否携带。
        string command = (_request.ExternalEngineCommand ?? string.Empty)
            .Replace("{url}", _request.TaskUrl ?? string.Empty)
            .Replace("{path}", _request.SavePath)
            .Replace("{name}", _request.TaskName ?? string.Empty)
            .Replace("{seed-ratio}", (plan.SeedRatio ?? 0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{seed-time}", (plan.SeedHours ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
        // PT 底线：magnet/tracker 里的 passkey 不进日志
        Log($"执行外部命令：{SensitiveUrls.Mask(command)}");

        // 引擎输出目录必须先存在（HTTP/FTP 驱动自写文件时会建目录，
        // 引擎接管时内核是唯一能保证目录就位的一方）
        var outputDir = Path.GetDirectoryName(_request.SavePath);
        if (outputDir is { Length: > 0 }) Directory.CreateDirectory(outputDir);

        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        start.ArgumentList.Add(command);

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception error)
        {
            Log($"外部引擎启动失败（{Brief(error.Message)}）", isError: true);
            return new TransferOutcome(TransferOutcomeKind.Failed, Brief(error.Message));
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellation);
        }
        catch (OperationCanceledException)
        {
            // pause/remove must not leave the engine process behind
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { await Task.WhenAll(stdout, stderr); } catch { /* pipes are closing */ }
            throw;
        }

        string errorText = await stderr;
        _ = await stdout; // captured for completeness; only stderr feeds the failure log

        if (process.ExitCode == 0)
        {
            if (File.Exists(_request.SavePath)) return EngineFileOutcome();
            Log("外部引擎已退出，但未产出文件", isError: true);
            return new TransferOutcome(TransferOutcomeKind.Failed, "外部引擎未产出文件");
        }

        var briefError = Brief(FirstLine(errorText));
        Log(briefError.Length > 0
            ? $"外部引擎退出码 {process.ExitCode}：{briefError}"
            : $"外部引擎退出码 {process.ExitCode}", isError: true);
        return new TransferOutcome(TransferOutcomeKind.Failed, $"外部引擎退出码 {process.ExitCode}");
    }

    /// <summary>The engine produced the file: surface it like a finished byte transfer.</summary>
    private TransferOutcome EngineFileOutcome()
    {
        long length = new FileInfo(_request.SavePath).Length;
        Interlocked.Exchange(ref _total, length);
        Interlocked.Exchange(ref _done, length);
        Log($"外部引擎完成：{FormatSize(length)}");
        return new TransferOutcome(TransferOutcomeKind.Completed, null);
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].TrimEnd('\r').Trim();

}
