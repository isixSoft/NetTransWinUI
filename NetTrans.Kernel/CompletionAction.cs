namespace NetTrans.Kernel;

/// <summary>完成后动作种类：内核只触发注入的 runner，不真执行系统动作
/// （关机/休眠/通知的宿主语义由 KernelOptions.CompletionRunner 决定）。</summary>
public enum AfterCompletion
{
    None = 0,
    Notify,      // 系统通知
    Shutdown,    // 关机
    Hibernate,   // 休眠
    ExitApp,     // 退出程序
    RunCommand,  // 运行自定义命令（Command 模板，{path} {name} 占位符已展开）
}

/// <summary>完成后动作事件：任务完成或全部任务完成时交给注入的 runner（恰一次）。</summary>
/// <param name="TaskName">完成的任务名。</param>
/// <param name="SavePath">产物绝对路径（全完成事件 = 最后完成的那个任务的产物）。</param>
/// <param name="Action">动作种类（任务级优先于全局级，两层可并存）。</param>
/// <param name="Command">RunCommand 的命令模板，{path}/{name} 占位符已展开。</param>
/// <param name="FromAllCompleted">true = 「全部任务完成」收尾触发；false = 单任务级动作。</param>
public sealed record CompletionEvent(
    string TaskName,
    string? SavePath,
    AfterCompletion Action,
    string? Command,
    bool FromAllCompleted);
