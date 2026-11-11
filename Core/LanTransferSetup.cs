using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using Serilog;

namespace STool.Core;

internal sealed record LanTransferSetupResult(bool Success, string Message);

internal static class LanTransferSetup
{
    internal const int DefaultPort = 17654;
    private const string FirewallRuleName = "STool LAN Transfer";

    public static async Task<bool> RunElevatedAsync(int port)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            return false;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--configure-lan-transfer {port}",
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas"
            });
            if (process == null)
                return false;

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检查当前防火墙里是否已有覆盖该端口的 STool 入站放行规则。
    /// 返回 null 表示无法判断（例如读取防火墙策略失败），调用方不应据此提示修复。
    /// </summary>
    public static bool? IsFirewallOpenForPort(int port)
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type == null)
                return null;
            dynamic? policy = Activator.CreateInstance(type);
            if (policy == null)
                return null;

            foreach (dynamic rule in policy.Rules)
            {
                if (!string.Equals((string?)rule.Name, FirewallRuleName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (FirewallRuleCoversPort(
                        (bool)rule.Enabled,
                        (int)rule.Direction,
                        (int)rule.Action,
                        (int)rule.Protocol,
                        (string?)rule.LocalPorts,
                        port))
                    return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to read Windows Firewall rules for LAN transfer");
            return null;
        }
    }

    /// <summary>规则属性取自 INetFwRule：Direction 1=入站，Action 1=允许，Protocol 6=TCP / 256=任意。</summary>
    internal static bool FirewallRuleCoversPort(
        bool enabled, int direction, int action, int protocol, string? localPorts, int port) =>
        enabled &&
        direction == 1 &&
        action == 1 &&
        protocol is 6 or 256 &&
        PortListCovers(localPorts, port);

    /// <summary>解析防火墙的本地端口表达式（"*"、"17654"、"17000-18000"、逗号分隔组合）。</summary>
    internal static bool PortListCovers(string? localPorts, int port)
    {
        if (string.IsNullOrWhiteSpace(localPorts))
            return true;

        foreach (var part in localPorts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part == "*")
                return true;

            var dash = part.IndexOf('-');
            if (dash > 0)
            {
                if (int.TryParse(part.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out var low) &&
                    int.TryParse(part.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var high) &&
                    port >= low && port <= high)
                    return true;
                continue;
            }

            if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var single) && single == port)
                return true;
        }

        return false;
    }

    public static LanTransferSetupResult ConfigureCurrentUser(int port)
    {
        var exePath = Environment.ProcessPath;
        var userName = WindowsIdentity.GetCurrent().Name;
        if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(userName))
            return new LanTransferSetupResult(false, "无法确定当前程序路径或 Windows 用户。 ");

        if (port is < 1024 or > 65535)
            return new LanTransferSetupResult(false, "监听端口必须位于 1024 到 65535 之间。 ");

        var url = $"http://+:{port}/";
        RunNetsh(["http", "delete", "urlacl", $"url={url}"]);
        var urlResult = RunNetsh(["http", "add", "urlacl", $"url={url}", $"user={userName}"]);
        if (urlResult.ExitCode != 0)
            return new LanTransferSetupResult(false, $"URL 监听权限配置失败：{urlResult.Output}");

        RunNetsh(["advfirewall", "firewall", "delete", "rule", $"name={FirewallRuleName}"]);
        var firewallResult = RunNetsh([
            "advfirewall", "firewall", "add", "rule",
            $"name={FirewallRuleName}",
            "dir=in", "action=allow", "protocol=TCP",
            $"localport={port}",
            "profile=any", "remoteip=localsubnet", "enable=yes"
        ]);

        if (firewallResult.ExitCode != 0)
            return new LanTransferSetupResult(false, $"防火墙规则配置失败：{firewallResult.Output}");

        return new LanTransferSetupResult(true, "连接权限和局域网防火墙规则已配置完成。 ");
    }

    private static (int ExitCode, string Output) RunNetsh(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo("netsh.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        if (process == null)
            return (-1, "无法启动 netsh.exe");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
    }
}
