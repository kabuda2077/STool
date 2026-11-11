using System.IO;
using System.Net;
using System.Net.Sockets;
using STool.Core;
using STool.Modules.LanTransfer;
using Xunit;

namespace STool.Tests;

public class LanTransferControllerTests
{
    private static readonly NetworkEndpoint WifiEndpoint =
        new(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0"), "Wi-Fi");

    private static readonly NetworkEndpoint EthernetEndpoint =
        new(IPAddress.Parse("10.0.0.5"), IPAddress.Parse("255.255.255.0"), "Ethernet");

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(null, false)]
    public void NeedsRepair_OnlyWhenFirewallDoesNotCoverPort(bool? firewallOpen, bool expected)
    {
        Assert.Equal(expected, LanTransferController.NeedsRepair(firewallOpen));
    }

    [Theory]
    [InlineData(null, 17654, true)]
    [InlineData("", 17654, true)]
    [InlineData("*", 17654, true)]
    [InlineData("17654", 17654, true)]
    [InlineData("17654", 17655, false)]
    [InlineData("80, 17654", 17654, true)]
    [InlineData("17000-18000", 17654, true)]
    [InlineData("17000-17653", 17654, false)]
    [InlineData("RPC", 17654, false)]
    [InlineData("abc-def", 17654, false)]
    public void PortListCovers_ParsesFirewallPortExpressions(string? localPorts, int port, bool expected)
    {
        Assert.Equal(expected, LanTransferSetup.PortListCovers(localPorts, port));
    }

    [Theory]
    [InlineData(true, 1, 1, 6, true)]
    [InlineData(true, 1, 1, 256, true)]
    [InlineData(false, 1, 1, 6, false)]
    [InlineData(true, 2, 1, 6, false)]
    [InlineData(true, 1, 0, 6, false)]
    [InlineData(true, 1, 1, 17, false)]
    public void FirewallRuleCoversPort_RequiresEnabledInboundAllowTcp(
        bool enabled, int direction, int action, int protocol, bool expected)
    {
        Assert.Equal(expected, LanTransferSetup.FirewallRuleCoversPort(enabled, direction, action, protocol, "17654", 17654));
    }

    [Fact]
    public void FirewallRuleCoversPort_RejectsRuleForAnotherPort()
    {
        Assert.False(LanTransferSetup.FirewallRuleCoversPort(true, 1, 1, 6, "17654", 18000));
    }

    [Fact]
    public async Task Restart_FirewallOpenForPort_RunsWithoutRepair()
    {
        await using var harness = new Harness();

        await harness.Controller.RestartAsync();

        Assert.Equal(ServerLifecycleState.Running, harness.Controller.State);
        Assert.Equal(LanStatusKind.WaitingForPhone, harness.Controller.Status);
        Assert.False(harness.Controller.RepairVisible);
        Assert.Equal($"http://192.168.1.10:{harness.Port}/", harness.Controller.Address);
    }

    [Fact]
    public async Task Restart_FirewallClosedForPort_SuggestsRepair()
    {
        await using var harness = new Harness();
        harness.Environment.Firewall = _ => false;

        await harness.Controller.RestartAsync();

        Assert.Equal(ServerLifecycleState.Running, harness.Controller.State);
        Assert.Equal(LanStatusKind.FirewallRepairSuggested, harness.Controller.Status);
        Assert.True(harness.Controller.RepairVisible);
    }

    [Fact]
    public async Task Restart_FirewallUnknown_DoesNotNagForRepair()
    {
        await using var harness = new Harness();
        harness.Environment.Firewall = _ => null;

        await harness.Controller.RestartAsync();

        Assert.False(harness.Controller.RepairVisible);
        Assert.Equal(LanStatusKind.WaitingForPhone, harness.Controller.Status);
    }

    [Fact]
    public async Task Restart_AfterPortChange_ChecksFirewallForTheNewPort()
    {
        await using var harness = new Harness();
        var oldPort = harness.Port;
        var newPort = GetFreePort();
        harness.Environment.Firewall = port => port == oldPort;

        await harness.Controller.RestartAsync();
        Assert.False(harness.Controller.RepairVisible);

        harness.Config.Update(config => config.LanTransfer.Port = newPort);
        await harness.Controller.RestartAsync();

        Assert.Equal(ServerLifecycleState.Running, harness.Controller.State);
        Assert.True(harness.Controller.RepairVisible);
        Assert.Equal([oldPort, newPort], harness.Environment.FirewallQueries);
        Assert.Equal($"http://192.168.1.10:{newPort}/", harness.Controller.Address);
    }

    [Fact]
    public async Task Restart_FirewallProbeDoesNotDependOnExecutablePath()
    {
        await using var harness = new Harness();
        harness.Config.Update(config => config.LanTransfer.ConfiguredExecutablePath = @"C:\Somewhere\Else\STool.exe");

        await harness.Controller.RestartAsync();

        Assert.False(harness.Controller.RepairVisible);
        Assert.Equal(LanStatusKind.WaitingForPhone, harness.Controller.Status);
    }

    [Fact]
    public async Task Restart_ListenerAccessDenied_RequiresPermissionSetup()
    {
        await using var harness = new Harness();
        harness.Environment.CreateFailure = new HttpListenerException(5);

        await harness.Controller.RestartAsync();

        Assert.Equal(ServerLifecycleState.PermissionRequired, harness.Controller.State);
        Assert.Equal(LanStatusKind.WaitingForPermission, harness.Controller.Status);
        Assert.False(harness.Controller.RepairVisible);
    }

    [Fact]
    public async Task Restart_UnexpectedFailure_ReportsErrorAndOffersRepair()
    {
        await using var harness = new Harness();
        harness.Environment.CreateFailure = new InvalidOperationException("boom");

        await harness.Controller.RestartAsync();

        Assert.Equal(ServerLifecycleState.Failed, harness.Controller.State);
        Assert.Equal(LanAddressNotice.Error, harness.Controller.AddressNotice);
        Assert.Equal("boom", harness.Controller.ErrorMessage);
        Assert.True(harness.Controller.RepairVisible);
    }

    [Fact]
    public async Task Restart_WithoutNetwork_FailsAndRecoversWhenNetworkReturns()
    {
        await using var harness = new Harness();
        harness.Environment.Endpoint = null;

        await harness.Controller.RestartAsync();

        Assert.Equal(ServerLifecycleState.Failed, harness.Controller.State);
        Assert.Equal(LanStatusKind.NoNetwork, harness.Controller.Status);
        Assert.Equal(LanAddressNotice.NoNetwork, harness.Controller.AddressNotice);

        harness.Environment.Endpoint = WifiEndpoint;
        await harness.Controller.RefreshNetworkAsync();

        Assert.Equal(ServerLifecycleState.Running, harness.Controller.State);
        Assert.Equal(LanAddressNotice.None, harness.Controller.AddressNotice);
        Assert.Equal($"http://192.168.1.10:{harness.Port}/", harness.Controller.Address);
    }

    [Fact]
    public async Task RefreshNetwork_AddressChanged_UpdatesAddressWithoutRestartingServer()
    {
        await using var harness = new Harness();
        await harness.Controller.RestartAsync();
        var created = harness.Environment.CreatedServers;
        var qrBefore = harness.Controller.QrAddress;
        var changes = 0;
        harness.Controller.Changed += () => changes++;

        harness.Environment.Endpoint = EthernetEndpoint;
        await harness.Controller.RefreshNetworkAsync();

        Assert.Equal(created, harness.Environment.CreatedServers);
        Assert.Equal(ServerLifecycleState.Running, harness.Controller.State);
        Assert.True(harness.Controller.IsRunning);
        Assert.Equal($"http://10.0.0.5:{harness.Port}/", harness.Controller.Address);
        Assert.NotEqual(qrBefore, harness.Controller.QrAddress);
        Assert.StartsWith($"http://10.0.0.5:{harness.Port}/#token=", harness.Controller.QrAddress);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task RefreshNetwork_SameAddress_DoesNotNotify()
    {
        await using var harness = new Harness();
        await harness.Controller.RestartAsync();
        var changes = 0;
        harness.Controller.Changed += () => changes++;

        await harness.Controller.RefreshNetworkAsync();

        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task RefreshNetwork_NetworkLostWhileRunning_KeepsLastAddress()
    {
        await using var harness = new Harness();
        await harness.Controller.RestartAsync();
        var address = harness.Controller.Address;

        harness.Environment.Endpoint = null;
        await harness.Controller.RefreshNetworkAsync();

        Assert.Equal(ServerLifecycleState.Running, harness.Controller.State);
        Assert.Equal(address, harness.Controller.Address);
    }

    [Fact]
    public async Task NetworkAddressChangedEvent_RefreshesAddressAfterDebounce()
    {
        await using var harness = new Harness();
        await harness.Controller.RestartAsync();
        Assert.NotNull(harness.Environment.NetworkHandler);

        harness.Environment.Endpoint = EthernetEndpoint;
        // 连续触发多次只应处理最后一次结果。
        harness.Environment.NetworkHandler!();
        harness.Environment.NetworkHandler!();

        await WaitUntilAsync(() => harness.Controller.Address == $"http://10.0.0.5:{harness.Port}/");
        Assert.Equal(1, harness.Environment.CreatedServers);
    }

    [Fact]
    public async Task AddSharedPaths_MissingPath_ReportsNothingFound()
    {
        await using var harness = new Harness();
        await harness.Controller.RestartAsync();

        var outcome = await harness.Controller.AddSharedPathsAsync([Path.Combine(harness.Root, "missing.txt")]);

        Assert.Equal(AddSharedResult.NothingFound, outcome.Result);
    }

    [Fact]
    public async Task AddSharedPaths_ServerCannotStart_ReportsNotRunning()
    {
        await using var harness = new Harness();
        harness.Environment.CreateFailure = new InvalidOperationException("boom");
        var file = Path.Combine(harness.Root, "file.txt");
        File.WriteAllText(file, "content");

        var outcome = await harness.Controller.AddSharedPathsAsync([file]);

        Assert.Equal(AddSharedResult.ServerNotRunning, outcome.Result);
    }

    [Fact]
    public async Task AddSharedPaths_SingleFile_QueuesOutgoingTransfer()
    {
        await using var harness = new Harness();
        await harness.Controller.RestartAsync();
        var file = Path.Combine(harness.Root, "file.txt");
        File.WriteAllText(file, "content");

        var outcome = await harness.Controller.AddSharedPathsAsync([file]);

        Assert.Equal(AddSharedResult.Queued, outcome.Result);
    }

    [Fact]
    public async Task Shutdown_StopsServerAndIgnoresLaterRestarts()
    {
        await using var harness = new Harness();
        await harness.Controller.RestartAsync();

        await harness.Controller.ShutdownAsync();
        await harness.Controller.RestartAsync();

        Assert.Equal(ServerLifecycleState.Stopping, harness.Controller.State);
        Assert.False(harness.Controller.IsRunning);
        Assert.Equal(1, harness.Environment.CreatedServers);
    }

    [Fact]
    public async Task EnsureReceiveDirectory_FillsDefaultWhenMissing()
    {
        await using var harness = new Harness();
        harness.Config.Update(config => config.LanTransfer.ReceiveDirectory = "");

        var directory = harness.Controller.EnsureReceiveDirectory();

        Assert.Equal(LanTransferController.GetDefaultReceiveDirectory(), directory);
        Assert.Equal(directory, harness.Config.Get().LanTransfer.ReceiveDirectory);
    }

    [Fact]
    public void BuildHistoryEntry_ReceivedUsesReceiveDirectoryWhenNoDestination()
    {
        var snapshot = CreateSnapshot(TransferDirection.ToComputer, destination: null, sources: []);

        var entry = LanTransferController.BuildHistoryEntry(snapshot, @"D:\Receive");

        Assert.NotNull(entry);
        Assert.Equal(@"D:\Receive", entry.LocationPath);
        Assert.Equal(snapshot.Id, entry.Id);
    }

    [Fact]
    public void BuildHistoryEntry_SentUsesFirstSourcePath()
    {
        var snapshot = CreateSnapshot(TransferDirection.ToPhone, destination: null, sources: [@"D:\Files\a.txt"]);

        var entry = LanTransferController.BuildHistoryEntry(snapshot, @"D:\Receive");

        Assert.NotNull(entry);
        Assert.Equal(@"D:\Files\a.txt", entry.LocationPath);
    }

    [Fact]
    public void BuildHistoryEntry_WithoutAnyPath_ReturnsNull()
    {
        var snapshot = CreateSnapshot(TransferDirection.ToPhone, destination: null, sources: []);

        Assert.Null(LanTransferController.BuildHistoryEntry(snapshot, @"D:\Receive"));
    }

    private static TransferSessionSnapshot CreateSnapshot(
        TransferDirection direction, string? destination, IReadOnlyList<string> sources) =>
        new(
            "id-1", "name", direction, TransferState.Completed, 10, 10, 0, 1,
            sources, destination, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "等待条件超时。");
            await Task.Delay(20);
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class FakeEnvironment : ILanTransferEnvironment
    {
        private readonly object _gate = new();
        private readonly List<int> _firewallQueries = [];
        private int _createdServers;

        public NetworkEndpoint? Endpoint { get; set; } = WifiEndpoint;
        public Func<int, bool?> Firewall { get; set; } = _ => true;
        public Exception? CreateFailure { get; set; }
        public Action? NetworkHandler { get; private set; }
        public int CreatedServers => Volatile.Read(ref _createdServers);

        public IReadOnlyList<int> FirewallQueries
        {
            get
            {
                lock (_gate)
                    return _firewallQueries.ToArray();
            }
        }

        public NetworkEndpoint? GetPreferredEndpoint() => Endpoint;

        public bool? IsFirewallOpenForPort(int port)
        {
            lock (_gate)
                _firewallQueries.Add(port);
            return Firewall(port);
        }

        public Task<bool> RunElevatedSetupAsync(int port) => Task.FromResult(true);

        public LanTransferServer CreateServer(
            NetworkEndpoint endpoint,
            int port,
            string receiveDirectory,
            int maxConcurrentTransfers,
            SharedFileCatalog sharedFiles,
            DeviceTokenStore deviceTokens)
        {
            if (CreateFailure != null)
                throw CreateFailure;
            Interlocked.Increment(ref _createdServers);
            return new LanTransferServer(
                endpoint, port, receiveDirectory, maxConcurrentTransfers, sharedFiles, deviceTokens,
                listenerHost: "localhost");
        }

        public IDisposable SubscribeNetworkChanged(Action handler)
        {
            NetworkHandler = handler;
            return new NoopDisposable();
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness()
        {
            Root = Path.Combine(Path.GetTempPath(), "STool.Tests", Guid.NewGuid().ToString("N"));
            var receive = Path.Combine(Root, "Receive");
            Directory.CreateDirectory(receive);
            Port = GetFreePort();
            Config = new ConfigManager(
                Path.Combine(Root, "config.json"),
                decrypt: value => value,
                encrypt: value => value,
                isPortableEncrypted: _ => false);
            Config.Update(config =>
            {
                config.LanTransfer.Port = Port;
                config.LanTransfer.ReceiveDirectory = receive;
            });

            // 控制器会捕获当前同步上下文；测试里不要借用 xUnit 的上下文，回调直接内联执行。
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                Controller = new LanTransferController(
                    Config,
                    Environment,
                    new DeviceTokenStore(Path.Combine(Root, "devices.json")),
                    new TransferHistoryStore(Path.Combine(Root, "history.json")),
                    networkChangeDebounce: TimeSpan.FromMilliseconds(50));
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        public string Root { get; }
        public int Port { get; }
        public ConfigManager Config { get; }
        public FakeEnvironment Environment { get; } = new();
        public LanTransferController Controller { get; }

        public async ValueTask DisposeAsync()
        {
            await Controller.ShutdownAsync();
            Controller.Dispose();
            try { Directory.Delete(Root, true); }
            catch (IOException) { }
        }
    }
}
