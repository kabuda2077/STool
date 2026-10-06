using System.Collections.Generic;

namespace STool.Core;

/// <summary>设置界面需要回调到应用外壳（托盘、全局热键、后台服务）的操作。</summary>
public interface IAppShell
{
    IReadOnlyList<HotkeyRegistrationResult> ReloadHotkeys(bool notifyFailures = false);

    /// <summary>快捷键录入框获得焦点时临时注销全局热键，失焦后调用 ReloadHotkeys 恢复。</summary>
    void SuspendHotkeys();

    void ReloadTrayIconVisibility();

    /// <summary>剪贴板设置变化后立即生效（开关监听、清理策略）。</summary>
    void ApplyClipboardSettings();

    /// <summary>诊断日志开关变化后立即生效。</summary>
    void ApplyDiagnosticsSettings();
}
