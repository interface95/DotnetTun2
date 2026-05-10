using System.Net;
using DotnetTun.Abstractions;
using DotnetTun.Abstractions.Routing;
using DotnetTun.Core;
using DotnetTun.Core.Dns;
using DotnetTun.Core.Routing;
using DotnetTun.Core.Sessions;
using DotnetTun.Outbounds.Socks5;
using DotnetTun.Platforms.MacOS.Networking;

namespace DotnetTun.Demo.Cli;

public interface ITunDemoRawTunProxy : IAsyncDisposable
{
    Task RunOpenAsync(int fileDescriptor, CancellationToken cancellationToken = default);
}

public sealed class TunDemoRuntime
{
    private readonly Func<ITunDevice> _createTunDevice;
    private readonly Func<MacTunConfigurator> _createConfigurator;
    private readonly Func<ITunDevice, FakeIpPool, IOutbound, int, TimeSpan?, ITunDemoRawTunProxy> _createRawTunProxy;
    private readonly Func<CancellationToken, ValueTask<IPAddress?>> _getDefaultGatewayAsync;

    public TunDemoRuntime(
        Func<ITunDevice> CreateTunDevice,
        Func<MacTunConfigurator> CreateConfigurator,
        Func<ITunDevice, FakeIpPool, IOutbound, int, TimeSpan?, ITunDemoRawTunProxy> CreateRawTunProxy)
        : this(CreateTunDevice, CreateConfigurator, CreateRawTunProxy, GetDefaultGatewayAsync: null)
    {
    }

    public TunDemoRuntime(
        Func<ITunDevice> CreateTunDevice,
        Func<MacTunConfigurator> CreateConfigurator,
        Func<ITunDevice, FakeIpPool, IOutbound, int, TimeSpan?, ITunDemoRawTunProxy> CreateRawTunProxy,
        Func<CancellationToken, ValueTask<IPAddress?>>? GetDefaultGatewayAsync = null)
    {
        _createTunDevice = CreateTunDevice ?? throw new ArgumentNullException(nameof(CreateTunDevice));
        _createConfigurator = CreateConfigurator ?? throw new ArgumentNullException(nameof(CreateConfigurator));
        _createRawTunProxy = CreateRawTunProxy ?? throw new ArgumentNullException(nameof(CreateRawTunProxy));
        _getDefaultGatewayAsync = GetDefaultGatewayAsync ?? (cancellationToken => new MacDefaultGatewayResolver().GetDefaultGatewayAsync(cancellationToken));
    }

    public static TunDemoRuntime Default { get; } = new(
        CreateTunDevice: () => new MacUtunDevice(new MacUtunNativeApi()),
        CreateConfigurator: () => new MacTunConfigurator(new MacRouteCommandBuilder(), new MacShellCommandRunner()),
        CreateRawTunProxy: (tunDevice, pool, outbound, mtu, responseReadTimeout)
            => new RawTunProxyAdapter(RawTunProxy.Create(
                tunDevice,
                pool,
                outbound,
                serverInitialSequence: 9_000,
                mtu,
                responseReadTimeout)));

    public ITunDevice CreateTunDevice() => _createTunDevice.Invoke();

    public MacTunConfigurator CreateConfigurator() => _createConfigurator.Invoke();

    public ITunDemoRawTunProxy CreateRawTunProxy(ITunDevice tunDevice, FakeIpPool pool, IOutbound outbound, int mtu, TimeSpan? responseReadTimeout)
        => _createRawTunProxy.Invoke(tunDevice, pool, outbound, mtu, responseReadTimeout);

    public ValueTask<IPAddress?> GetDefaultGatewayAsync(CancellationToken cancellationToken = default)
        => _getDefaultGatewayAsync.Invoke(cancellationToken);

    private sealed class RawTunProxyAdapter(RawTunProxy proxy) : ITunDemoRawTunProxy
    {
        public Task RunOpenAsync(int fileDescriptor, CancellationToken cancellationToken = default)
            => proxy.RunOpenAsync(fileDescriptor, cancellationToken);

        public ValueTask DisposeAsync() => proxy.DisposeAsync();
    }
}

public abstract class DotnetTunDemoCommand
{
    public static DotnetTunDemoCommand Parse(string[] args)
        => Parse(args, TunDemoRuntime.Default);

    public static DotnetTunDemoCommand Parse(string[] args, TunDemoRuntime tunRuntime)
    {
        ArgumentNullException.ThrowIfNull(tunRuntime);

        if (args is ["dns", .. var dnsArgs])
        {
            return DnsDemoCommand.Create(dnsArgs);
        }

        if (args is ["bridge", .. var bridgeArgs])
        {
            return BridgeDemoCommand.Create(bridgeArgs);
        }

        if (args is ["tun", .. var tunArgs])
        {
            return TunDemoCommand.Create(tunArgs, tunRuntime);
        }

        return DryRunDemoCommand.Create(args);
    }

    public abstract Task<int> RunAsync(CancellationToken cancellationToken = default);

    public virtual ValueTask<DotnetTunDemoCommandHandle> StartAsync(CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Only the dns and bridge commands can be started as long-running commands.");

    private sealed class DryRunDemoCommand(DotnetTunOptions options, string socks5Endpoint) : DotnetTunDemoCommand
    {
        public static DryRunDemoCommand Create(string[] args)
        {
            DotnetTunOptions options = ParseOptions(args);
            string socks5Endpoint = ParseSocks5Endpoint(args);
            return new DryRunDemoCommand(options, socks5Endpoint);
        }

        public override Task<int> RunAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var engine = new DotnetTunEngine();
            DotnetTunDryRunPlan plan = engine.CreateDryRun(options);
            Socks5OutboundOptions socks5 = ParseSocks5(socks5Endpoint);

            var macOptions = new MacTunOptions(
                InterfaceName: "utun-auto",
                Address: IPAddress.Parse("10.88.0.2"),
                Gateway: IPAddress.Parse("10.88.0.1"),
                Mtu: 1420,
                FakeIpCidr: options.FakeIpCidr,
                ExcludedIps: options.ExcludedIps);

            var commandBuilder = new MacRouteCommandBuilder();

            Console.WriteLine("DotnetTun dry-run plan");
            Console.WriteLine($"Outbound: {socks5}");
            Console.WriteLine($"Fake-IP CIDR: {options.FakeIpCidr}");
            Console.WriteLine();

            Console.WriteLine("Exact domain leases:");
            foreach (var lease in plan.ExactDomainLeases)
            {
                Console.WriteLine($"  {lease.Domain} -> {lease.FakeIp}");
            }

            Console.WriteLine();
            Console.WriteLine("Wildcard patterns:");
            foreach (string pattern in plan.WildcardPatterns)
            {
                Console.WriteLine($"  {pattern}");
            }

            Console.WriteLine();
            Console.WriteLine("macOS configure commands (not executed):");
            foreach (MacCommand command in commandBuilder.BuildConfigureCommands(macOptions))
            {
                Console.WriteLine($"  {command}");
            }

            Console.WriteLine();
            Console.WriteLine("Sample DNS log preview:");
            var logger = new ConsoleProxyLogger();
            var pool = new FakeIpPool();
            var router = new DomainInterceptRouter(options.InterceptDomains.Select(domain => new DomainInterceptRule(domain)), pool);
            var resolver = new FakeDnsResolver(router, logger);
            foreach (string domain in options.InterceptDomains.Where(domain => !domain.StartsWith("*.", StringComparison.Ordinal)))
            {
                _ = resolver.TryResolve(CreatePreviewAQuery(0xD07A, domain), out _);
            }

            if (options.ExcludedIps.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("macOS exclude commands with sample default gateway 192.168.1.1 (not executed):");
                foreach (MacCommand command in commandBuilder.BuildExcludeCommands(macOptions, IPAddress.Parse("192.168.1.1")))
                {
                    Console.WriteLine($"  {command}");
                }
            }

            return Task.FromResult(0);
        }
    }

    private sealed class DnsDemoCommand(IPAddress listenAddress, int port, IReadOnlyList<string> domains) : DotnetTunDemoCommand
    {
        public static DnsDemoCommand Create(string[] args)
        {
            var listenAddress = IPAddress.Loopback;
            int port = 5353;
            List<string> domains = [];

            for (int i = 0; i < args.Length; i++)
            {
                string current = args[i];
                switch (current)
                {
                    case "--listen":
                        (listenAddress, port) = ParseEndpoint(ReadValue(args, ref i, "--listen"));
                        break;
                    case "--domain":
                        domains.Add(ReadValue(args, ref i, "--domain"));
                        break;
                    default:
                        throw new ArgumentException($"Unknown dns argument: {current}");
                }
            }

            if (domains.Count == 0)
            {
                throw new ArgumentException("dns command requires at least one --domain value.");
            }

            return new DnsDemoCommand(listenAddress, port, domains);
        }

        public override async Task<int> RunAsync(CancellationToken cancellationToken = default)
        {
            using var stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stopSource.Cancel();
            };

            await using DotnetTunDemoCommandHandle handle = await StartAsync(stopSource.Token).ConfigureAwait(false);
            Console.WriteLine($"DotnetTun fake DNS listening on {listenAddress}:{handle.Port}");
            Console.WriteLine("Press Ctrl+C to stop.");

            Console.CancelKeyPress += cancelHandler;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stopSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopSource.IsCancellationRequested)
            {
                return 0;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }

            return 0;
        }

        public override async ValueTask<DotnetTunDemoCommandHandle> StartAsync(CancellationToken cancellationToken = default)
        {
            var pool = new FakeIpPool();
            var router = new DomainInterceptRouter(domains.Select(domain => new DomainInterceptRule(domain)), pool);
            var resolver = new FakeDnsResolver(router, new ConsoleProxyLogger());
            var server = new FakeDnsServer(resolver, listenAddress, port);
            await server.StartAsync(cancellationToken).ConfigureAwait(false);
            return new DotnetTunDemoCommandHandle(server);
        }
    }

    private sealed class BridgeDemoCommand(
        IPAddress listenAddress,
        int listenPort,
        IPAddress fakeIp,
        string domain,
        int targetPort,
        string socks5Endpoint) : DotnetTunDemoCommand
    {
        public static BridgeDemoCommand Create(string[] args)
        {
            var listenAddress = IPAddress.Loopback;
            int listenPort = 18080;
            IPAddress? fakeIp = null;
            string? domain = null;
            int targetPort = 443;
            string socks5Endpoint = "127.0.0.1:7890";

            for (int i = 0; i < args.Length; i++)
            {
                string current = args[i];
                switch (current)
                {
                    case "--listen":
                        (listenAddress, listenPort) = ParseEndpoint(ReadValue(args, ref i, "--listen"));
                        break;
                    case "--fake-ip":
                        fakeIp = IPAddress.Parse(ReadValue(args, ref i, "--fake-ip"));
                        break;
                    case "--domain":
                        domain = ReadValue(args, ref i, "--domain");
                        break;
                    case "--target-port":
                        targetPort = int.Parse(ReadValue(args, ref i, "--target-port"));
                        break;
                    case "--socks5":
                        socks5Endpoint = ReadValue(args, ref i, "--socks5");
                        break;
                    default:
                        throw new ArgumentException($"Unknown bridge argument: {current}");
                }
            }

            if (fakeIp is null)
            {
                throw new ArgumentException("bridge command requires --fake-ip.");
            }

            if (string.IsNullOrWhiteSpace(domain))
            {
                throw new ArgumentException("bridge command requires --domain.");
            }

            if (targetPort is < 1 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(args), "--target-port must be between 1 and 65535.");
            }

            return new BridgeDemoCommand(listenAddress, listenPort, fakeIp, domain.Trim(), targetPort, socks5Endpoint);
        }

        public override async Task<int> RunAsync(CancellationToken cancellationToken = default)
        {
            using var stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stopSource.Cancel();
            };

            await using DotnetTunDemoCommandHandle handle = await StartAsync(stopSource.Token).ConfigureAwait(false);
            Console.WriteLine($"DotnetTun bridge listening on {listenAddress}:{handle.Port}");
            Console.WriteLine($"Fake-IP mapping: {fakeIp} -> {domain}:{targetPort}");
            Console.WriteLine($"Outbound: socks5://{socks5Endpoint}");
            Console.WriteLine("Press Ctrl+C to stop.");

            Console.CancelKeyPress += cancelHandler;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stopSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopSource.IsCancellationRequested)
            {
                return 0;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }

            return 0;
        }

        public override async ValueTask<DotnetTunDemoCommandHandle> StartAsync(CancellationToken cancellationToken = default)
        {
            var pool = new FakeIpPool(fakeIp, fakeIp);
            _ = pool.Allocate(domain);
            var outbound = new Socks5Outbound(ParseSocks5(socks5Endpoint));
            var bridge = new FakeIpTcpBridge(pool, outbound);
            var server = new FakeIpTcpBridgeServer(bridge, listenAddress, listenPort, fakeIp, targetPort);
            await server.StartAsync(cancellationToken).ConfigureAwait(false);
            return new DotnetTunDemoCommandHandle(server, server.Port);
        }
    }

    private sealed class TunDemoCommand(
        IPAddress fakeIp,
        string domain,
        string socks5Endpoint,
        int mtu,
        bool dryRun,
        TimeSpan? responseReadTimeout,
        TunDemoRuntime runtime) : DotnetTunDemoCommand
    {
        public static TunDemoCommand Create(string[] args, TunDemoRuntime runtime)
        {
            IPAddress? fakeIp = null;
            string? domain = null;
            string socks5Endpoint = "127.0.0.1:7890";
            int mtu = 1500;
            bool dryRun = false;
            TimeSpan? responseReadTimeout = null;

            for (int i = 0; i < args.Length; i++)
            {
                string current = args[i];
                switch (current)
                {
                    case "--dry-run":
                        dryRun = true;
                        break;
                    case "--fake-ip":
                        fakeIp = IPAddress.Parse(ReadValue(args, ref i, "--fake-ip"));
                        break;
                    case "--domain":
                        domain = ReadValue(args, ref i, "--domain");
                        break;
                    case "--socks5":
                        socks5Endpoint = ReadValue(args, ref i, "--socks5");
                        break;
                    case "--mtu":
                        mtu = int.Parse(ReadValue(args, ref i, "--mtu"));
                        break;
                    case "--response-read-timeout-ms":
                        responseReadTimeout = TimeSpan.FromMilliseconds(int.Parse(ReadValue(args, ref i, "--response-read-timeout-ms")));
                        break;
                    default:
                        throw new ArgumentException($"Unknown tun argument: {current}");
                }
            }

            if (fakeIp is null)
            {
                throw new ArgumentException("tun command requires --fake-ip.");
            }

            if (string.IsNullOrWhiteSpace(domain))
            {
                throw new ArgumentException("tun command requires --domain.");
            }

            if (mtu <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(args), "--mtu must be positive.");
            }

            return new TunDemoCommand(fakeIp, domain.Trim(), socks5Endpoint, mtu, dryRun, responseReadTimeout, runtime);
        }

        public override async Task<int> RunAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (dryRun)
            {
                PrintPlan();
                return 0;
            }

            using var stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stopSource.Cancel();
            };

            Console.CancelKeyPress += cancelHandler;
            MacTunOptions? macOptions = null;
            MacTunConfigurator? configurator = null;
            ITunDevice? tunDevice = null;
            int? openedFileDescriptor = null;
            try
            {
                PrintPlan();
                Console.WriteLine("Opening macOS utun and running raw TCP packet pump. Press Ctrl+C to stop.");
                var pool = new FakeIpPool(fakeIp, fakeIp);
                _ = pool.Allocate(domain);
                Socks5OutboundOptions socks5Options = ParseSocks5(socks5Endpoint);
                var outbound = new Socks5Outbound(socks5Options);
                tunDevice = runtime.CreateTunDevice();
                configurator = runtime.CreateConfigurator();

                TunDeviceOpenResult openResult = await tunDevice.OpenTunAsync(stopSource.Token).ConfigureAwait(false);
                if (!openResult.Success || string.IsNullOrWhiteSpace(openResult.InterfaceName))
                {
                    throw new IOException($"macOS utun open failed with error {openResult.ErrorNumber}.");
                }

                openedFileDescriptor = openResult.FileDescriptor;
                IPAddress? defaultGateway = await runtime.GetDefaultGatewayAsync(stopSource.Token).ConfigureAwait(false);
                macOptions = CreateMacOptions(
                    openResult.InterfaceName,
                    ResolveExcludedServerIps(socks5Options.Host),
                    defaultGateway);
                await configurator.ConfigureAsync(macOptions, stopSource.Token).ConfigureAwait(false);
                await using ITunDemoRawTunProxy proxy = runtime.CreateRawTunProxy(
                    tunDevice,
                    pool,
                    outbound,
                    mtu,
                    responseReadTimeout);
                await proxy.RunOpenAsync(openedFileDescriptor.Value, stopSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopSource.IsCancellationRequested)
            {
                return 0;
            }
            finally
            {
                try
                {
                    if (configurator is not null && macOptions is not null)
                    {
                        await configurator.CleanupAsync(macOptions, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                finally
                {
                    try
                    {
                        if (tunDevice is not null && openedFileDescriptor is not null)
                        {
                            TunDeviceCloseResult closeResult = await tunDevice.CloseTunAsync(openedFileDescriptor.Value, CancellationToken.None).ConfigureAwait(false);
                            if (!closeResult.Success)
                            {
                                throw new IOException($"macOS utun close failed with error {closeResult.ErrorNumber}.");
                            }
                        }
                    }
                    finally
                    {
                        Console.CancelKeyPress -= cancelHandler;
                    }
                }
            }

            return 0;
        }

        private void PrintPlan()
        {
            Console.WriteLine("DotnetTun raw TUN plan");
            Console.WriteLine($"Fake-IP mapping: {fakeIp} -> {domain}");
            Console.WriteLine($"Outbound: socks5://{socks5Endpoint}");
            Console.WriteLine($"MTU: {mtu}");
            Console.WriteLine($"Response read timeout: {(responseReadTimeout is null ? "disabled" : responseReadTimeout.Value.TotalMilliseconds + " ms")}");
            Console.WriteLine();
            Console.WriteLine("macOS configure commands:");
            foreach (MacCommand command in new MacRouteCommandBuilder().BuildConfigureCommands(CreateMacOptions("utun-auto")))
            {
                Console.WriteLine($"  {command}");
            }

            Console.WriteLine();
            Console.WriteLine("macOS cleanup commands:");
            foreach (MacCommand command in new MacRouteCommandBuilder().BuildCleanupCommands(CreateMacOptions("utun-auto")))
            {
                Console.WriteLine($"  {command}");
            }
        }

        private MacTunOptions CreateMacOptions(string interfaceName, IReadOnlyList<IPAddress>? excludedIps = null, IPAddress? defaultGateway = null)
        {
            string fakeIpCidr = fakeIp.ToString() + "/32";
            return new MacTunOptions(
                InterfaceName: interfaceName,
                Address: IPAddress.Parse("10.88.0.2"),
                Gateway: IPAddress.Parse("10.88.0.1"),
                Mtu: mtu,
                FakeIpCidr: fakeIpCidr,
                ExcludedIps: excludedIps ?? [],
                DefaultGateway: defaultGateway);
        }

        private static IReadOnlyList<IPAddress> ResolveExcludedServerIps(string host)
        {
            if (!IPAddress.TryParse(host, out IPAddress? address) || IPAddress.IsLoopback(address))
            {
                return [];
            }

            return [address];
        }
    }

    private static DotnetTunOptions ParseOptions(string[] args)
    {
        List<string> domains = [];
        List<IPAddress> excludes = [];
        string fakeIpCidr = "198.18.0.0/15";

        for (int i = 0; i < args.Length; i++)
        {
            string current = args[i];
            switch (current)
            {
                case "--dry-run":
                    break;
                case "--domain":
                    domains.Add(ReadValue(args, ref i, "--domain"));
                    break;
                case "--fake-ip-cidr":
                    fakeIpCidr = ReadValue(args, ref i, "--fake-ip-cidr");
                    break;
                case "--socks5":
                    _ = ReadValue(args, ref i, "--socks5");
                    break;
                case "--exclude-ip":
                    excludes.Add(IPAddress.Parse(ReadValue(args, ref i, "--exclude-ip")));
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {current}");
            }
        }

        if (domains.Count == 0)
        {
            domains.AddRange(["api.anthropic.com", "*.anthropic.com"]);
        }

        return new DotnetTunOptions
        {
            InterceptDomains = domains,
            FakeIpCidr = fakeIpCidr,
            ExcludedIps = excludes
        };
    }

    private static string ParseSocks5Endpoint(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--socks5")
            {
                return ReadValue(args, ref i, "--socks5");
            }
        }

        return "127.0.0.1:7890";
    }

    private static string ReadValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{optionName} requires a value.");
        }

        index++;
        return args[index];
    }

    private static Socks5OutboundOptions ParseSocks5(string endpoint)
    {
        string[] parts = endpoint.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], out int port))
        {
            throw new ArgumentException("SOCKS5 endpoint must be host:port, for example 127.0.0.1:7890.", nameof(endpoint));
        }

        return new Socks5OutboundOptions(parts[0], port);
    }

    private static (IPAddress Address, int Port) ParseEndpoint(string endpoint)
    {
        string[] parts = endpoint.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out IPAddress? address) || !int.TryParse(parts[1], out int port))
        {
            throw new ArgumentException("Endpoint must be ip:port, for example 127.0.0.1:5353.", nameof(endpoint));
        }

        return (address, port);
    }

    private static byte[] CreatePreviewAQuery(ushort transactionId, string domain)
    {
        using var stream = new MemoryStream();
        stream.WriteByte((byte)(transactionId >> 8));
        stream.WriteByte((byte)(transactionId & 0xFF));
        stream.Write([0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);

        foreach (string label in domain.Split('.'))
        {
            stream.WriteByte((byte)label.Length);
            stream.Write(System.Text.Encoding.ASCII.GetBytes(label));
        }

        stream.WriteByte(0x00);
        stream.Write([0x00, 0x01, 0x00, 0x01]);
        return stream.ToArray();
    }
}

public sealed class DotnetTunDemoCommandHandle : IAsyncDisposable
{
    private readonly IAsyncDisposable _server;

    public DotnetTunDemoCommandHandle(FakeDnsServer server)
        : this(server, server.Port)
    {
    }

    public DotnetTunDemoCommandHandle(IAsyncDisposable server, int port)
    {
        _server = server;
        Port = port;
    }

    public int Port { get; }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}
