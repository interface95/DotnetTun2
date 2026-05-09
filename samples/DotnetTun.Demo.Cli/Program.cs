using System.Net;
using DotnetTun.Abstractions;
using DotnetTun.Core;
using DotnetTun.Outbounds.Socks5;
using DotnetTun.Platforms.MacOS.Networking;

DotnetTunOptions options = ParseOptions(args);
string socks5Endpoint = ParseSocks5Endpoint(args);
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
foreach (string command in commandBuilder.BuildConfigureCommands(macOptions))
{
    Console.WriteLine($"  {command}");
}

if (options.ExcludedIps.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("macOS exclude commands with sample default gateway 192.168.1.1 (not executed):");
    foreach (string command in commandBuilder.BuildExcludeCommands(macOptions, IPAddress.Parse("192.168.1.1")))
    {
        Console.WriteLine($"  {command}");
    }
}

static DotnetTunOptions ParseOptions(string[] args)
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

static string ParseSocks5Endpoint(string[] args)
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

static string ReadValue(string[] args, ref int index, string optionName)
{
    if (index + 1 >= args.Length)
    {
        throw new ArgumentException($"{optionName} requires a value.");
    }

    index++;
    return args[index];
}

static Socks5OutboundOptions ParseSocks5(string endpoint)
{
    string[] parts = endpoint.Split(':', StringSplitOptions.TrimEntries);
    if (parts.Length != 2 || !int.TryParse(parts[1], out int port))
    {
        throw new ArgumentException("SOCKS5 endpoint must be host:port, for example 127.0.0.1:7890.", nameof(endpoint));
    }

    return new Socks5OutboundOptions(parts[0], port);
}
