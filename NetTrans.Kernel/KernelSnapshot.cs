namespace NetTrans.Kernel;

/// <summary>
/// Aggregate readouts the kernel reports with every tick; an immutable copy,
/// safe to hand across threads.
/// </summary>
/// <param name="TotalSpeed">Sum of every task's live speed, bytes/s.</param>
/// <param name="UploadSpeed">Sum of BT upload speeds, bytes/s.</param>
/// <param name="IsRunning">True while at least one task is downloading.</param>
/// <param name="SpeedHistory">The island's 26-bar sparkline, oldest sample first.</param>
public sealed record KernelSnapshot(
    double TotalSpeed,
    double UploadSpeed,
    bool IsRunning,
    double[] SpeedHistory);
