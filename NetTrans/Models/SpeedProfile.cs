namespace NetTrans.Models;

/// <summary>
/// 命名限速档位（P2-2）：「全速 / 浏览优先 / ...」一键切换的全局限速预设。
/// <see cref="BytesPerSecond"/> 为字节/秒，null = 不限。
/// </summary>
public sealed record SpeedProfile(string Name, long? BytesPerSecond);
