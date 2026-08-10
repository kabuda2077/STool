using System.Diagnostics;
using System.Security.Principal;

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
