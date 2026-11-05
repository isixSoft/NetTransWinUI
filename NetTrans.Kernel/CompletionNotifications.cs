namespace NetTrans.Kernel;

/// <summary>
/// 系统通知槽位（P1-4）：内核在任务完成等时机发出通知；headless 下由测试/宿主
/// 注入实现，UI 侧由 AppNotification 接线为真实系统通知。实现需自行保证线程安全，
/// 可能在任意工作线程上被调用。
/// </summary>
public interface INotificationSink
{
    void Notify(string title, string body);
}

/// <summary>
/// 任务完成通知（P1-4）：设置开关（KernelOptions.NotifyOnComplete）开且注入了
/// NotificationSink 才发出；sink 抛异常只吞掉——通知是旁路能力，绝不影响下载内核。
/// </summary>
public static class CompletionNotifications
{
    public const string CompletedTitle = "下载完成";

    public static void Emit(KernelOptions options, DownloadTask task)
    {
        if (!options.NotifyOnComplete) return;
        var sink = options.NotificationSink;
        if (sink is null) return;

        try
        {
            sink.Notify(CompletedTitle, $"{task.Name} 已完成");
        }
        catch
        {
            // 旁路能力：通知失败不影响下载
        }
    }
}
