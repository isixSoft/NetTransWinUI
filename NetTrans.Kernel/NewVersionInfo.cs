namespace NetTrans.Kernel;

/// <summary>
/// The handoff's `newer` field: a newer build of the same file exists on the
/// server. Drives the blue `.newver` notice in the inspector and the blue
/// 新版本 tag on the list row. In a real build this comes from a server-side
/// version query surfaced by the kernel, not from the task itself.
/// </summary>
/// <param name="Version">File name of the newer build.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="Published">Human-readable publish time ("2 天前").</param>
public sealed record NewVersionInfo(string Version, long Size, string Published);
