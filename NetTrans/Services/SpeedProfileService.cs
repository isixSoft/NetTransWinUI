using NetTrans.Models;

namespace NetTrans.Services;

/// <summary>
/// 限速档位服务（P2-2）：把命名档位解析为全局限速字节值，一键切换时经回调
/// 即时下发内核（下一 tick 生效）。刻意用 <see cref="SpeedProfileService.Switch"/>
/// 的委托而非 IDownloadKernel，让本服务保持纯逻辑、可独立测试。
/// </summary>
public sealed class SpeedProfileService
{
    /// <summary>出厂默认档位：全速（不限）+ 浏览优先（1 MiB/s）。</summary>
    public static IReadOnlyList<SpeedProfile> Defaults { get; } = new SpeedProfile[]
    {
        new("全速", null),
        new("浏览优先", 1048576L),
    };

    private readonly AppSettings _settings;

    public SpeedProfileService(AppSettings settings) => _settings = settings;

    /// <summary>当前档位列表：用户自定义列表非空时优先，否则回退出厂默认。</summary>
    public IReadOnlyList<SpeedProfile> Profiles =>
        _settings.SpeedProfiles.Count > 0 ? _settings.SpeedProfiles : Defaults;

    /// <summary>档位名 → 字节/秒；未知档位与「全速」都返回 null。</summary>
    public long? Resolve(string name) =>
        Profiles.FirstOrDefault(p => p.Name == name)?.BytesPerSecond;

    /// <summary>
    /// 一键切换：把档位限速经 <paramref name="applyKernelLimit"/> 下发内核并记录为
    /// 当前档位。未知档位返回 false，内核与设置状态都不动。
    /// </summary>
    public bool Switch(string name, Action<long?> applyKernelLimit)
    {
        if (Profiles.FirstOrDefault(p => p.Name == name) is not { } profile) return false;

        applyKernelLimit(profile.BytesPerSecond);
        _settings.ActiveSpeedProfile = profile.Name;
        return true;
    }

    /// <summary>
    /// 启动时恢复上次档位（设置页接线）：非默认「全速」才经委托下发——「全速」
    /// 交由 KernelOptionsFactory 映射的旧全局限速值，避免启动时清掉精细限速。
    /// 记录的档位已不存在时走 <see cref="Switch"/> 的失败路径，内核与状态都不动。
    /// </summary>
    public bool Restore(Action<long?> applyKernelLimit) =>
        _settings.ActiveSpeedProfile is not ("" or "全速") &&
        Switch(_settings.ActiveSpeedProfile, applyKernelLimit);
}
