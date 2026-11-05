namespace NetTrans.Kernel;

/// <summary>One row of the inspector's 日志 tab: timestamp + event text.</summary>
/// <param name="Time">Wall-clock stamp as shown ("09:41").</param>
/// <param name="Message">Event text.</param>
/// <param name="IsError">Renders in --red when true.</param>
public sealed record LogEntry(string Time, string Message, bool IsError = false);
