using System.Net.NetworkInformation;
using STool.Core;

namespace STool.Modules.LanTransfer;

/// <summary>
/// 控制器与系统之间的边界：网络地址、防火墙、提权配置、服务创建。
/// 生产环境用 <see cref="SystemLanTransferEnvironment"/>，测试替换为假实现。
/// </summary>
internal interface ILanTransferEnvironment
{
    NetworkEndpoint? GetPreferredEndpoint();

    /// <summary>防火墙是否已有覆盖该端口的放行规则；null 表示无法判断。</summary>
    bool? IsFirewallOpenForPort(int port);

    /// <summary>以管理员权限配置 URL 监听权限与防火墙规则，返回是否成功。</summary>
    Task<bool> RunElevatedSetupAsync(int port);

    LanTransferServer CreateServer(
        NetworkEndpoint endpoint,
        int port,
        string receiveDirectory,
        int maxConcurrentTransfers,
        SharedFileCatalog sharedFiles,
        DeviceTokenStore deviceTokens);

    /// <summary>订阅系统网络地址变化；回调可能在任意线程触发。</summary>
    IDisposable SubscribeNetworkChanged(Action handler);
}

internal sealed class SystemLanTransferEnvironment : ILanTransferEnvironment
{
    public NetworkEndpoint? GetPreferredEndpoint() => NetworkEndpointSelector.GetPreferred();

    public bool? IsFirewallOpenForPort(int port) => LanTransferSetup.IsFirewallOpenForPort(port);

    public Task<bool> RunElevatedSetupAsync(int port) => LanTransferSetup.RunElevatedAsync(port);

    public LanTransferServer CreateServer(
        NetworkEndpoint endpoint,
        int port,
        string receiveDirectory,
        int maxConcurrentTransfers,
        SharedFileCatalog sharedFiles,
        DeviceTokenStore deviceTokens) =>
        new(endpoint, port, receiveDirectory, maxConcurrentTransfers, sharedFiles, deviceTokens);

    public IDisposable SubscribeNetworkChanged(Action handler)
    {
        void OnChanged(object? sender, EventArgs e) => handler();
        NetworkChange.NetworkAddressChanged += OnChanged;
        return new Subscription(() => NetworkChange.NetworkAddressChanged -= OnChanged);
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
