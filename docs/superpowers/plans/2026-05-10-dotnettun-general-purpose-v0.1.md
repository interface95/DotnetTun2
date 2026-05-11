# DotnetTun General-Purpose Library v0.1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn DotnetTun from a partially-wired demo into a real reusable library where third-party code can plug in `IOutbound` / `IRouter` / `IDnsHijacker` without touching `DotnetTun.Core` source — verified end-to-end on macOS via the existing SOCKS5 outbound.

**Architecture:** Introduce three first-class extension interfaces (`IRouter`, `IFakeIpStore`, `IDnsHijacker`) in `DotnetTun.Abstractions`, refactor existing concrete classes (`FakeIpPool`, `DomainInterceptRouter`, `FakeDnsResolver`) to implement them, drop the leaking `int fileDescriptor` from `ITunDevice`, replace the bespoke `IProxyLogger` with `Microsoft.Extensions.Logging.ILogger<T>`, and finally make `BuiltTransparentProxy.StartAsync` actually construct and run the packet pipeline using the injected dependencies. A new UDP/DNS branch is added to the existing IPv4 packet handler so on-TUN DNS queries reach `IDnsHijacker`. `DotnetTun.Hosting` exposes an `IHostedService` so DI users get start/stop for free. `Microsoft.CodeAnalysis.PublicApiAnalyzers` is enabled with a captured baseline to lock the public surface from this version forward.

**Tech Stack:** .NET 10, C# 14, xUnit v3, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.CodeAnalysis.PublicApiAnalyzers`. macOS-only runtime validation via the existing `MacUtunDevice` and `Socks5Outbound`.

---

## Scope

### In scope (this plan)

1. New abstractions: `IRouter`, `ConnectionContext`, `RouteDecision`, `IFakeIpStore`, `IDnsHijacker`, `DnsHandlingResult`.
2. Refining existing public types: `IOutbound` (add `Name`), `ITunDevice` (drop `int fileDescriptor`), `ITransparentProxy` (add `IAsyncDisposable`).
3. Refactoring concrete classes to implement the new interfaces.
4. Hiding platform-private result records (`MacUtunOpenResult`, `LinuxTunOpenResult`, `WindowsTunOpenResult`) as `internal`.
5. Removing `IProxyLogger` / `NullProxyLogger`; migrating loggers to `ILogger<T>`.
6. Adding a UDP packet handler so DNS queries on the TUN device reach `IDnsHijacker`.
7. A concrete `UdpUpstreamDnsResolver` (so unmatched domains are not silently dropped).
8. Builder API redesign: `UseTunDevice` / `UseRouter` / `UseFakeIpStore` / `UseDnsHijacker` / `AddOutbound` / `AddRule`.
9. End-to-end wiring of `BuiltTransparentProxy.StartAsync`.
10. `IHostedService` integration in `DotnetTun.Hosting`.
11. Enabling `PublicApiAnalyzers` and capturing the v0.1 baseline.
12. Updating `samples/DotnetTun.Demo.Cli` to use the new builder API.

### Out of scope (deferred to follow-up plans)

- Linux `ITunDevice` runtime (read/write syscalls, configurator).
- Windows `wintun` runtime.
- HTTP/2 CONNECT outbound + Kestrel demo server.
- `System.Diagnostics.Metrics.Meter` / `ActivitySource` instrumentation.
- AOT / Trimming annotations (`[RequiresUnreferencedCode]` etc.).
- macOS NetworkExtension production path.

---

## Architecture Decisions

### Public-facing interface surface (final)

```csharp
namespace DotnetTun.Abstractions;

public interface ITunDevice : IAsyncDisposable
{
    bool IsOpen { get; }
    string? InterfaceName { get; }
    ValueTask OpenAsync(CancellationToken cancellationToken = default);
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
    ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default);
    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}

public interface IOutbound
{
    string Name { get; }
    ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default);
}

public interface ITransparentProxy : IAsyncDisposable
{
    ValueTask StartAsync(CancellationToken cancellationToken = default);
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}
```

```csharp
namespace DotnetTun.Abstractions.Routing;

public interface IRouter
{
    ValueTask<RouteDecision> RouteAsync(ConnectionContext context, CancellationToken cancellationToken = default);
}

public sealed record ConnectionContext(string Host, int Port);

public sealed record RouteDecision
{
    public required bool Intercept { get; init; }
    public string? OutboundName { get; init; }

    public static RouteDecision Direct() => new() { Intercept = false };
    public static RouteDecision Through(string outboundName)
        => new() { Intercept = true, OutboundName = outboundName ?? throw new ArgumentNullException(nameof(outboundName)) };
}
```

```csharp
namespace DotnetTun.Abstractions.Dns;

public interface IFakeIpStore
{
    IPAddress Allocate(string domain);
    bool TryResolve(IPAddress fakeIp, [NotNullWhen(true)] out string? domain);
}

public interface IDnsHijacker
{
    ValueTask<DnsHandlingResult> HandleAsync(ReadOnlyMemory<byte> query, CancellationToken cancellationToken = default);
}

public enum DnsHandlingDisposition { Intercepted, Forwarded, Dropped }

public sealed record DnsHandlingResult
{
    public required DnsHandlingDisposition Disposition { get; init; }
    public byte[]? Response { get; init; }

    public static DnsHandlingResult Intercepted(byte[] response) => new() { Disposition = DnsHandlingDisposition.Intercepted, Response = response };
    public static DnsHandlingResult Forwarded(byte[] response) => new() { Disposition = DnsHandlingDisposition.Forwarded, Response = response };
    public static DnsHandlingResult Dropped() => new() { Disposition = DnsHandlingDisposition.Dropped };
}
```

### Composition rule

`IRouter` is the single source of truth for routing decisions. The DNS hijacker queries `IRouter` with `new ConnectionContext(domain, port: 0)` to decide whether to mint a FakeIP for a given DNS query. The TCP redirector queries `IRouter` with `new ConnectionContext(domain, actualPort)` after reverse-resolving the FakeIP.

### Internal flow (post-refactor)

```
DNS query "api.anthropic.com" arrives on TUN UDP:53
  └─ Ipv4TunPacketHandler dispatches to UdpIpv4PacketHandler
     └─ UdpIpv4PacketHandler routes UDP:53 to IDnsHijacker.HandleAsync(query)
        └─ Default RoutingDnsHijacker:
             1. parse question
             2. router.RouteAsync(domain, 0)
             3. if Intercept: fakeIpStore.Allocate(domain) → build A response
             4. if !Intercept: upstream.ResolveAsync(query)
        └─ response written back to TUN as UDP reply

TCP SYN to FakeIP arrives on TUN
  └─ existing RawTcpSessionHandler accepts TCP redirect
     └─ on Established: domain = fakeIpStore.TryResolve(dstIp)
        └─ decision = router.RouteAsync(domain, dstPort)
        └─ outbound = registry[decision.OutboundName!]
        └─ stream = outbound.ConnectAsync(domain, dstPort)
        └─ bridge stream ↔ TCP session (existing OutboundTcpPayloadSink logic)
```

---

## File Map

### New files

| Path | Responsibility |
|---|---|
| `src/DotnetTun.Abstractions/Routing/IRouter.cs` | Router contract |
| `src/DotnetTun.Abstractions/Routing/ConnectionContext.cs` | Routing input record |
| `src/DotnetTun.Abstractions/Routing/RouteDecision.cs` | Routing output record |
| `src/DotnetTun.Abstractions/Dns/IFakeIpStore.cs` | FakeIP store contract |
| `src/DotnetTun.Abstractions/Dns/IDnsHijacker.cs` | DNS hijacker contract |
| `src/DotnetTun.Abstractions/Dns/DnsHandlingResult.cs` | DNS hijacker output |
| `src/DotnetTun.Core/Routing/DomainRuleRouter.cs` | Default `IRouter` impl (suffix/exact match) |
| `src/DotnetTun.Core/Dns/FakeIpStore.cs` | Default `IFakeIpStore` impl (replaces `FakeIpPool`) |
| `src/DotnetTun.Core/Dns/RoutingDnsHijacker.cs` | Default `IDnsHijacker` composing `IRouter` + `IFakeIpStore` + `IUpstreamDnsResolver` |
| `src/DotnetTun.Core/Dns/UdpUpstreamDnsResolver.cs` | Concrete `IUpstreamDnsResolver` over `UdpClient` |
| `src/DotnetTun.Core/Sessions/UdpIpv4PacketHandler.cs` | UDP branch of the TUN pipeline |
| `src/DotnetTun.Core/Sessions/Dns53Sink.cs` | UDP:53 sink that calls `IDnsHijacker` |
| `src/DotnetTun.Hosting/TransparentProxyHostedService.cs` | `IHostedService` adapter |
| `tests/DotnetTun.Core.Tests/Routing/DomainRuleRouterTests.cs` | Router tests |
| `tests/DotnetTun.Core.Tests/Dns/FakeIpStoreTests.cs` | Store tests (replaces `FakeIpPoolTests`) |
| `tests/DotnetTun.Core.Tests/Dns/RoutingDnsHijackerTests.cs` | Hijacker tests |
| `tests/DotnetTun.Core.Tests/Dns/UdpUpstreamDnsResolverTests.cs` | Resolver tests |
| `tests/DotnetTun.Core.Tests/Sessions/UdpIpv4PacketHandlerTests.cs` | UDP dispatcher tests |
| `tests/DotnetTun.Core.Tests/Sessions/Dns53SinkTests.cs` | DNS sink tests |
| `tests/DotnetTun.Core.Tests/TransparentProxyEndToEndTests.cs` | Wired-up Start/Stop test using fakes |
| `tests/DotnetTun.Hosting.Tests/HostedService/TransparentProxyHostedServiceTests.cs` | Hosted service tests |
| `src/DotnetTun.Abstractions/PublicAPI.Shipped.txt` | API baseline (auto-generated) |
| `src/DotnetTun.Abstractions/PublicAPI.Unshipped.txt` | API baseline (auto-generated) |
| `src/DotnetTun.Core/PublicAPI.Shipped.txt` | API baseline |
| `src/DotnetTun.Core/PublicAPI.Unshipped.txt` | API baseline |
| `src/DotnetTun.Hosting/PublicAPI.Shipped.txt` | API baseline |
| `src/DotnetTun.Hosting/PublicAPI.Unshipped.txt` | API baseline |
| `src/DotnetTun.Outbounds.Socks5/PublicAPI.Shipped.txt` | API baseline |
| `src/DotnetTun.Outbounds.Socks5/PublicAPI.Unshipped.txt` | API baseline |
| `src/DotnetTun.Platforms.MacOS/PublicAPI.Shipped.txt` | API baseline |
| `src/DotnetTun.Platforms.MacOS/PublicAPI.Unshipped.txt` | API baseline |

### Modified files

| Path | Change |
|---|---|
| `src/DotnetTun.Abstractions/IOutbound.cs` | Add `string Name { get; }` |
| `src/DotnetTun.Abstractions/ITunDevice.cs` | Drop `int fileDescriptor`; add `IAsyncDisposable`, `IsOpen`, `InterfaceName` |
| `src/DotnetTun.Abstractions/ITransparentProxy.cs` | Drop `Options` and `Outbounds` properties; add `IAsyncDisposable` |
| `src/DotnetTun.Abstractions/TunDeviceOpenResult.cs` | **Deleted** (no longer in public surface) |
| `src/DotnetTun.Abstractions/TunDeviceCloseResult.cs` | **Deleted** |
| `src/DotnetTun.Abstractions/TunPacketIoResult.cs` | **Deleted** |
| `src/DotnetTun.Abstractions/Diagnostics/IProxyLogger.cs` | **Deleted** |
| `src/DotnetTun.Abstractions/Diagnostics/NullProxyLogger.cs` | **Deleted** |
| `src/DotnetTun.Abstractions/Routing/InterceptDecision.cs` | **Deleted** (subsumed by `RouteDecision`) |
| `src/DotnetTun.Abstractions/Routing/DomainInterceptRule.cs` | Stays (used by builder) |
| `src/DotnetTun.Core/Dns/FakeIpPool.cs` | **Deleted** (replaced by `FakeIpStore.cs`) |
| `src/DotnetTun.Core/Dns/FakeDnsResolver.cs` | Replaced by `RoutingDnsHijacker.cs` |
| `src/DotnetTun.Core/Routing/DomainInterceptRouter.cs` | **Deleted** (replaced by `DomainRuleRouter.cs`) |
| `src/DotnetTun.Core/TransparentProxyBuilder.cs` | Rewritten — new fluent surface |
| `src/DotnetTun.Core/TransparentProxy.cs` | `BuiltTransparentProxy` becomes a real implementation |
| `src/DotnetTun.Core/DotnetTunEngine.cs` | **Deleted** (dry-run plan moves into builder) |
| `src/DotnetTun.Core/DotnetTunDryRunPlan.cs` | **Deleted** |
| `src/DotnetTun.Core/Sessions/RawTcpTunPipeline.cs` | Accept `IFakeIpStore` + `IRouter` + outbound registry instead of single `IOutbound` |
| `src/DotnetTun.Core/Sessions/OutboundTcpPayloadSink.cs` | Look up outbound by name via registry |
| `src/DotnetTun.Core/Sessions/Ipv4TunPacketHandler.cs` | Add UDP dispatch |
| `src/DotnetTun.Outbounds.Socks5/Socks5Outbound.cs` | Add `Name` (default `"socks5"`, configurable) |
| `src/DotnetTun.Platforms.MacOS/Networking/MacUtunDevice.cs` | New `ITunDevice` shape (handle managed internally) |
| `src/DotnetTun.Platforms.MacOS/Networking/MacUtunOpenResult.cs` | Make `internal sealed` |
| `src/DotnetTun.Platforms.Linux/Networking/LinuxTunOpenResult.cs` | Make `internal sealed` |
| `src/DotnetTun.Platforms.Windows/Networking/WindowsTunOpenResult.cs` | Make `internal sealed` |
| `src/DotnetTun.Hosting/TransparentProxyServiceCollectionExtensions.cs` | Register `IHostedService` |
| `samples/DotnetTun.Demo.Cli/DotnetTunDemoCommand.cs` | `tun` subcommand uses the new builder API |
| `Directory.Packages.props` | Add `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.CodeAnalysis.PublicApiAnalyzers` |
| `Directory.Build.props` | Wire `PublicApiAnalyzers` package globally |

---

## Task 1: Add `IRouter`, `ConnectionContext`, `RouteDecision`

**Files:**
- Create: `src/DotnetTun.Abstractions/Routing/ConnectionContext.cs`
- Create: `src/DotnetTun.Abstractions/Routing/RouteDecision.cs`
- Create: `src/DotnetTun.Abstractions/Routing/IRouter.cs`

These are pure additions — no failing test step. They are validated by the consumer test added in Task 2.

- [ ] **Step 1: Create `ConnectionContext.cs`**

```csharp
namespace DotnetTun.Abstractions.Routing;

public sealed record ConnectionContext(string Host, int Port)
{
    public string Host { get; } = string.IsNullOrWhiteSpace(Host)
        ? throw new ArgumentException("Host must not be empty.", nameof(Host))
        : Host.Trim().TrimEnd('.').ToLowerInvariant();

    public int Port { get; } = Port is < 0 or > 65535
        ? throw new ArgumentOutOfRangeException(nameof(Port), "Port must be between 0 and 65535.")
        : Port;
}
```

- [ ] **Step 2: Create `RouteDecision.cs`**

```csharp
namespace DotnetTun.Abstractions.Routing;

public sealed record RouteDecision
{
    public required bool Intercept { get; init; }

    public string? OutboundName { get; init; }

    public static RouteDecision Direct() => new() { Intercept = false };

    public static RouteDecision Through(string outboundName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outboundName);
        return new RouteDecision { Intercept = true, OutboundName = outboundName.Trim() };
    }
}
```

- [ ] **Step 3: Create `IRouter.cs`**

```csharp
namespace DotnetTun.Abstractions.Routing;

public interface IRouter
{
    ValueTask<RouteDecision> RouteAsync(ConnectionContext context, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 4: Build the abstractions project**

Run: `dotnet build src/DotnetTun.Abstractions/DotnetTun.Abstractions.csproj`
Expected: build succeeds with no warnings.

- [ ] **Step 5: Commit**

```bash
git add src/DotnetTun.Abstractions/Routing/
git commit -m "feat(abstractions): add IRouter, ConnectionContext, RouteDecision"
```

---

## Task 2: Replace `DomainInterceptRouter` with `DomainRuleRouter` (`IRouter`)

**Files:**
- Create: `src/DotnetTun.Core/Routing/DomainRuleRouter.cs`
- Create: `tests/DotnetTun.Core.Tests/Routing/DomainRuleRouterTests.cs`
- Delete: `src/DotnetTun.Core/Routing/DomainInterceptRouter.cs`
- Delete: `tests/DotnetTun.Core.Tests/Routing/DomainInterceptRouterTests.cs`
- Delete: `src/DotnetTun.Abstractions/Routing/InterceptDecision.cs`

- [ ] **Step 1: Write failing tests in `DomainRuleRouterTests.cs`**

```csharp
using DotnetTun.Abstractions.Routing;
using DotnetTun.Core.Routing;
using Xunit;

namespace DotnetTun.Core.Tests.Routing;

public sealed class DomainRuleRouterTests
{
    [Fact]
    public async Task RouteAsync_WhenExactDomainMatches_ReturnsThroughDecision()
    {
        DomainRuleRouter router = new([new("api.anthropic.com", "h2")]);

        RouteDecision decision = await router.RouteAsync(new ConnectionContext("api.anthropic.com", 443), TestContext.Current.CancellationToken);

        Assert.True(decision.Intercept);
        Assert.Equal("h2", decision.OutboundName);
    }

    [Fact]
    public async Task RouteAsync_WhenSuffixMatches_ReturnsThroughDecision()
    {
        DomainRuleRouter router = new([new("*.anthropic.com", "h2")]);

        RouteDecision decision = await router.RouteAsync(new ConnectionContext("api.anthropic.com", 443), TestContext.Current.CancellationToken);

        Assert.True(decision.Intercept);
        Assert.Equal("h2", decision.OutboundName);
    }

    [Fact]
    public async Task RouteAsync_WhenNoRuleMatches_ReturnsDirect()
    {
        DomainRuleRouter router = new([new("api.anthropic.com", "h2")]);

        RouteDecision decision = await router.RouteAsync(new ConnectionContext("example.com", 443), TestContext.Current.CancellationToken);

        Assert.False(decision.Intercept);
        Assert.Null(decision.OutboundName);
    }

    [Fact]
    public async Task RouteAsync_DomainComparisonIsCaseInsensitive()
    {
        DomainRuleRouter router = new([new("API.anthropic.com", "h2")]);

        RouteDecision decision = await router.RouteAsync(new ConnectionContext("api.ANTHROPIC.com", 443), TestContext.Current.CancellationToken);

        Assert.True(decision.Intercept);
    }
}
```

- [ ] **Step 2: Run the tests, expect failure**

Run: `dotnet test tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj --filter FullyQualifiedName~DomainRuleRouterTests`
Expected: compile error (`DomainRuleRouter` does not exist).

- [ ] **Step 3: Create `DomainRuleRouter.cs`**

```csharp
using DotnetTun.Abstractions.Routing;

namespace DotnetTun.Core.Routing;

public sealed class DomainRuleRouter : IRouter
{
    private readonly DomainPattern[] _patterns;

    public DomainRuleRouter(IEnumerable<DomainInterceptRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _patterns = [.. rules.Select(rule => DomainPattern.Parse(rule.Pattern, rule.OutboundName))];
    }

    public ValueTask<RouteDecision> RouteAsync(ConnectionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (DomainPattern pattern in _patterns)
        {
            if (pattern.IsMatch(context.Host))
            {
                return ValueTask.FromResult(RouteDecision.Through(pattern.OutboundName));
            }
        }

        return ValueTask.FromResult(RouteDecision.Direct());
    }

    private readonly record struct DomainPattern(string Value, bool IsWildcard, string OutboundName)
    {
        public static DomainPattern Parse(string pattern, string outboundName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
            ArgumentException.ThrowIfNullOrWhiteSpace(outboundName);

            string normalized = pattern.Trim().TrimEnd('.').ToLowerInvariant();
            return normalized.StartsWith("*.", StringComparison.Ordinal)
                ? new DomainPattern(normalized[2..], IsWildcard: true, outboundName.Trim())
                : new DomainPattern(normalized, IsWildcard: false, outboundName.Trim());
        }

        public bool IsMatch(string host)
            => IsWildcard
                ? host.EndsWith($".{Value}", StringComparison.OrdinalIgnoreCase) || host.Equals(Value, StringComparison.OrdinalIgnoreCase)
                : host.Equals(Value, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 4: Run the tests, expect green**

Run: `dotnet test tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj --filter FullyQualifiedName~DomainRuleRouterTests`
Expected: all 4 pass.

- [ ] **Step 5: Delete `DomainInterceptRouter.cs`, `DomainInterceptRouterTests.cs`, `InterceptDecision.cs`**

```bash
git rm src/DotnetTun.Core/Routing/DomainInterceptRouter.cs
git rm tests/DotnetTun.Core.Tests/Routing/DomainInterceptRouterTests.cs
git rm src/DotnetTun.Abstractions/Routing/InterceptDecision.cs
```

- [ ] **Step 6: Build the entire solution to surface compile errors**

Run: `dotnet build DotnetTun.slnx`
Expected: errors only in places that referenced the deleted types — record them, those become hot-spots fixed in later tasks (`FakeDnsResolver`, demo CLI, etc.). For this commit, keep the broken state isolated only if every other consumer is fixed in the same commit; otherwise fix call sites inline.

For Task 2, the only direct consumer is `FakeDnsResolver` and the demo CLI. Update them minimally — replace `decision.FakeIp is null` checks with `decision.Intercept == false` and remove the FakeIP allocation from the router (FakeIP allocation moves to `RoutingDnsHijacker` in Task 4). If this is awkward, postpone deletion of `InterceptDecision` to Task 4 and leave `DomainInterceptRouter` deprecated until then.

Pragmatic choice: **leave `InterceptDecision.cs` and `DomainInterceptRouter.cs` in the tree until Task 4 lands**, but mark them `[Obsolete("Use IRouter / RouteDecision instead.", error: false)]`. Update the deletion in Task 4 cleanup.

- [ ] **Step 7: Apply the obsolete markers and rebuild**

```csharp
// src/DotnetTun.Abstractions/Routing/InterceptDecision.cs
[Obsolete("Use DotnetTun.Abstractions.Routing.RouteDecision instead.", error: false)]
public sealed record InterceptDecision { /* existing body */ }

// src/DotnetTun.Core/Routing/DomainInterceptRouter.cs
[Obsolete("Use DotnetTun.Core.Routing.DomainRuleRouter instead.", error: false)]
public sealed class DomainInterceptRouter { /* existing body */ }
```

Add `<NoWarn>$(NoWarn);CS0618</NoWarn>` to `tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj` so the existing `DomainInterceptRouterTests` keeps compiling.

Run: `dotnet build DotnetTun.slnx`
Expected: build succeeds.

- [ ] **Step 8: Commit**

```bash
git add src/DotnetTun.Core/Routing/DomainRuleRouter.cs \
        tests/DotnetTun.Core.Tests/Routing/DomainRuleRouterTests.cs \
        src/DotnetTun.Abstractions/Routing/InterceptDecision.cs \
        src/DotnetTun.Core/Routing/DomainInterceptRouter.cs \
        tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj
git commit -m "feat(core): add DomainRuleRouter; obsolete DomainInterceptRouter"
```

---

## Task 3: Replace `FakeIpPool` with `FakeIpStore` (`IFakeIpStore`)

**Files:**
- Create: `src/DotnetTun.Abstractions/Dns/IFakeIpStore.cs`
- Create: `src/DotnetTun.Core/Dns/FakeIpStore.cs`
- Create: `tests/DotnetTun.Core.Tests/Dns/FakeIpStoreTests.cs`
- Delete: `src/DotnetTun.Core/Dns/FakeIpPool.cs` (after Task 4)
- Delete: `tests/DotnetTun.Core.Tests/FakeIp/FakeIpPoolTests.cs` (after Task 4)

- [ ] **Step 1: Create `IFakeIpStore.cs`**

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace DotnetTun.Abstractions.Dns;

public interface IFakeIpStore
{
    IPAddress Allocate(string domain);
    bool TryResolve(IPAddress fakeIp, [NotNullWhen(true)] out string? domain);
}
```

- [ ] **Step 2: Write failing tests in `FakeIpStoreTests.cs`**

```csharp
using System.Net;
using DotnetTun.Abstractions.Dns;
using DotnetTun.Core.Dns;
using Xunit;

namespace DotnetTun.Core.Tests.Dns;

public sealed class FakeIpStoreTests
{
    [Fact]
    public void Allocate_AssignsAddressFromRange()
    {
        IFakeIpStore store = new FakeIpStore(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.3"));

        IPAddress address = store.Allocate("api.anthropic.com");

        Assert.Equal(IPAddress.Parse("198.18.0.1"), address);
    }

    [Fact]
    public void Allocate_SameDomainTwice_ReturnsSameAddress()
    {
        IFakeIpStore store = new FakeIpStore(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.10"));

        IPAddress first = store.Allocate("api.anthropic.com");
        IPAddress second = store.Allocate("API.anthropic.com.");

        Assert.Equal(first, second);
    }

    [Fact]
    public void TryResolve_RoundTripsAllocatedDomain()
    {
        IFakeIpStore store = new FakeIpStore(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.10"));
        IPAddress address = store.Allocate("api.anthropic.com");

        bool resolved = store.TryResolve(address, out string? domain);

        Assert.True(resolved);
        Assert.Equal("api.anthropic.com", domain);
    }

    [Fact]
    public void TryResolve_UnknownAddress_ReturnsFalse()
    {
        IFakeIpStore store = new FakeIpStore(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.10"));

        Assert.False(store.TryResolve(IPAddress.Parse("198.18.0.5"), out string? domain));
        Assert.Null(domain);
    }

    [Fact]
    public void Allocate_WhenRangeExhausted_Throws()
    {
        IFakeIpStore store = new FakeIpStore(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.1"));
        store.Allocate("a.example.com");

        Assert.Throws<InvalidOperationException>(() => store.Allocate("b.example.com"));
    }
}
```

- [ ] **Step 3: Run tests, expect failure**

Run: `dotnet test tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj --filter FullyQualifiedName~FakeIpStoreTests`
Expected: compile error.

- [ ] **Step 4: Create `FakeIpStore.cs`**

Copy the implementation body from the existing `FakeIpPool.cs`, change the type name to `FakeIpStore`, drop the `FakeIpLease` return type (return `IPAddress` directly from `Allocate`), and `: IFakeIpStore` on the class. Keep the bidirectional dictionaries and the same range-validation logic. The interface methods replace the old `FakeIpLease` shape.

```csharp
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using DotnetTun.Abstractions.Dns;

namespace DotnetTun.Core.Dns;

public sealed class FakeIpStore : IFakeIpStore
{
    private readonly uint _start;
    private readonly uint _end;
    private uint _next;
    private readonly Dictionary<string, IPAddress> _addressByDomain = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IPAddress, string> _domainByAddress = new();
    private readonly object _gate = new();

    public FakeIpStore() : this(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.19.255.254")) { }

    public FakeIpStore(IPAddress start, IPAddress end)
    {
        _start = ToUInt32(start);
        _end = ToUInt32(end);
        if (_start > _end) throw new ArgumentException("Fake-IP range start must be <= end.", nameof(start));
        _next = _start;
    }

    public IPAddress Allocate(string domain)
    {
        string normalized = NormalizeDomain(domain);
        lock (_gate)
        {
            if (_addressByDomain.TryGetValue(normalized, out IPAddress? existing)) return existing;
            if (_next > _end) throw new InvalidOperationException("Fake-IP pool is exhausted.");
            IPAddress address = FromUInt32(_next++);
            _addressByDomain.Add(normalized, address);
            _domainByAddress.Add(address, normalized);
            return address;
        }
    }

    public bool TryResolve(IPAddress fakeIp, [NotNullWhen(true)] out string? domain)
    {
        lock (_gate)
        {
            return _domainByAddress.TryGetValue(fakeIp, out domain);
        }
    }

    private static string NormalizeDomain(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        return domain.Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static uint ToUInt32(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Fake-IP store supports IPv4 only.", nameof(address));
        return BinaryPrimitives.ReadUInt32BigEndian(address.MapToIPv4().GetAddressBytes());
    }

    private static IPAddress FromUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
```

- [ ] **Step 5: Run tests, expect green**

Run: `dotnet test tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj --filter FullyQualifiedName~FakeIpStoreTests`
Expected: 5 pass.

- [ ] **Step 6: Build the solution**

Run: `dotnet build DotnetTun.slnx`
Expected: build succeeds (existing `FakeIpPool` still alive — no removal yet).

- [ ] **Step 7: Commit**

```bash
git add src/DotnetTun.Abstractions/Dns/IFakeIpStore.cs \
        src/DotnetTun.Core/Dns/FakeIpStore.cs \
        tests/DotnetTun.Core.Tests/Dns/FakeIpStoreTests.cs
git commit -m "feat(core): add FakeIpStore implementing IFakeIpStore"
```

---

## Task 4: Add `IDnsHijacker` + `RoutingDnsHijacker`; replace `FakeDnsResolver`

**Files:**
- Create: `src/DotnetTun.Abstractions/Dns/IDnsHijacker.cs`
- Create: `src/DotnetTun.Abstractions/Dns/DnsHandlingResult.cs`
- Create: `src/DotnetTun.Core/Dns/RoutingDnsHijacker.cs`
- Create: `tests/DotnetTun.Core.Tests/Dns/RoutingDnsHijackerTests.cs`
- Delete: `src/DotnetTun.Core/Dns/FakeDnsResolver.cs`
- Delete: `tests/DotnetTun.Core.Tests/Dns/FakeDnsResolverTests.cs`

- [ ] **Step 1: Create `DnsHandlingResult.cs`**

```csharp
namespace DotnetTun.Abstractions.Dns;

public enum DnsHandlingDisposition
{
    Intercepted,
    Forwarded,
    Dropped
}

public sealed record DnsHandlingResult
{
    public required DnsHandlingDisposition Disposition { get; init; }
    public byte[]? Response { get; init; }

    public static DnsHandlingResult Intercepted(byte[] response)
        => new() { Disposition = DnsHandlingDisposition.Intercepted, Response = response ?? throw new ArgumentNullException(nameof(response)) };

    public static DnsHandlingResult Forwarded(byte[] response)
        => new() { Disposition = DnsHandlingDisposition.Forwarded, Response = response ?? throw new ArgumentNullException(nameof(response)) };

    public static DnsHandlingResult Dropped() => new() { Disposition = DnsHandlingDisposition.Dropped };
}
```

- [ ] **Step 2: Create `IDnsHijacker.cs`**

```csharp
namespace DotnetTun.Abstractions.Dns;

public interface IDnsHijacker
{
    ValueTask<DnsHandlingResult> HandleAsync(ReadOnlyMemory<byte> query, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 3: Write failing tests in `RoutingDnsHijackerTests.cs`**

```csharp
using System.Net;
using DotnetTun.Abstractions.Dns;
using DotnetTun.Abstractions.Routing;
using DotnetTun.Core.Dns;
using Xunit;

namespace DotnetTun.Core.Tests.Dns;

public sealed class RoutingDnsHijackerTests
{
    [Fact]
    public async Task HandleAsync_WhenRouterIntercepts_ReturnsAResponseFromStore()
    {
        IFakeIpStore store = new FakeIpStore(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.10"));
        IRouter router = new StubRouter(("api.anthropic.com", RouteDecision.Through("h2")));
        RoutingDnsHijacker hijacker = new(router, store, upstream: null);

        byte[] query = FakeDnsMessage.BuildAQuery("api.anthropic.com", id: 0x1234);

        DnsHandlingResult result = await hijacker.HandleAsync(query, TestContext.Current.CancellationToken);

        Assert.Equal(DnsHandlingDisposition.Intercepted, result.Disposition);
        Assert.NotNull(result.Response);
        Assert.True(store.TryResolve(IPAddress.Parse("198.18.0.1"), out _));
    }

    [Fact]
    public async Task HandleAsync_WhenRouterDirectAndUpstreamPresent_ReturnsForwardedResponse()
    {
        IFakeIpStore store = new FakeIpStore();
        IRouter router = new StubRouter();
        StubUpstreamResolver upstream = new(response: [0xAA, 0xBB]);
        RoutingDnsHijacker hijacker = new(router, store, upstream);

        byte[] query = FakeDnsMessage.BuildAQuery("example.com", id: 0x1234);

        DnsHandlingResult result = await hijacker.HandleAsync(query, TestContext.Current.CancellationToken);

        Assert.Equal(DnsHandlingDisposition.Forwarded, result.Disposition);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, result.Response);
    }

    [Fact]
    public async Task HandleAsync_WhenRouterDirectAndNoUpstream_ReturnsDropped()
    {
        RoutingDnsHijacker hijacker = new(new StubRouter(), new FakeIpStore(), upstream: null);

        byte[] query = FakeDnsMessage.BuildAQuery("example.com", id: 0x1234);

        DnsHandlingResult result = await hijacker.HandleAsync(query, TestContext.Current.CancellationToken);

        Assert.Equal(DnsHandlingDisposition.Dropped, result.Disposition);
    }

    [Fact]
    public async Task HandleAsync_AaaaQueryForInterceptedDomain_ReturnsNoDataResponse()
    {
        IFakeIpStore store = new FakeIpStore();
        IRouter router = new StubRouter(("api.anthropic.com", RouteDecision.Through("h2")));
        RoutingDnsHijacker hijacker = new(router, store, upstream: null);

        byte[] query = FakeDnsMessage.BuildAaaaQuery("api.anthropic.com", id: 0x1234);

        DnsHandlingResult result = await hijacker.HandleAsync(query, TestContext.Current.CancellationToken);

        Assert.Equal(DnsHandlingDisposition.Intercepted, result.Disposition);
        Assert.NotNull(result.Response);
    }

    private sealed class StubRouter(params (string Host, RouteDecision Decision)[] entries) : IRouter
    {
        public ValueTask<RouteDecision> RouteAsync(ConnectionContext context, CancellationToken cancellationToken = default)
        {
            foreach ((string host, RouteDecision decision) in entries)
            {
                if (string.Equals(host, context.Host, StringComparison.OrdinalIgnoreCase))
                {
                    return ValueTask.FromResult(decision);
                }
            }
            return ValueTask.FromResult(RouteDecision.Direct());
        }
    }

    private sealed class StubUpstreamResolver(byte[] response) : IUpstreamDnsResolver
    {
        public ValueTask<byte[]?> ResolveAsync(ReadOnlyMemory<byte> query, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<byte[]?>(response);
    }
}
```

Note: `FakeDnsMessage.BuildAQuery` and `BuildAaaaQuery` helpers do not exist yet. Add them to `FakeDnsMessage.cs` as `internal static` helpers (constructing a minimal one-question DNS query packet).

- [ ] **Step 4: Add helper builders to `FakeDnsMessage.cs`**

Append:

```csharp
internal static byte[] BuildAQuery(string domain, ushort id) => BuildQuery(domain, id, qtype: 1);
internal static byte[] BuildAaaaQuery(string domain, ushort id) => BuildQuery(domain, id, qtype: 28);

private static byte[] BuildQuery(string domain, ushort id, ushort qtype)
{
    using MemoryStream stream = new();
    Span<byte> header = stackalloc byte[12];
    BinaryPrimitives.WriteUInt16BigEndian(header[0..], id);
    header[2] = 0x01; // RD set
    header[3] = 0x00;
    BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1); // QDCOUNT
    stream.Write(header);

    foreach (string label in domain.Split('.', StringSplitOptions.RemoveEmptyEntries))
    {
        byte[] bytes = Encoding.ASCII.GetBytes(label);
        stream.WriteByte((byte)bytes.Length);
        stream.Write(bytes);
    }
    stream.WriteByte(0);

    Span<byte> tail = stackalloc byte[4];
    BinaryPrimitives.WriteUInt16BigEndian(tail[0..], qtype);
    BinaryPrimitives.WriteUInt16BigEndian(tail[2..], 1);
    stream.Write(tail);

    return stream.ToArray();
}
```

(`using System.Text;` and `using System.Buffers.Binary;` may need to be added.)

- [ ] **Step 5: Create `RoutingDnsHijacker.cs`**

```csharp
using System.Net;
using DotnetTun.Abstractions.Dns;
using DotnetTun.Abstractions.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetTun.Core.Dns;

public sealed class RoutingDnsHijacker : IDnsHijacker
{
    private readonly IRouter _router;
    private readonly IFakeIpStore _store;
    private readonly IUpstreamDnsResolver? _upstream;
    private readonly ILogger<RoutingDnsHijacker> _logger;

    public RoutingDnsHijacker(IRouter router, IFakeIpStore store, IUpstreamDnsResolver? upstream = null, ILogger<RoutingDnsHijacker>? logger = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _upstream = upstream;
        _logger = logger ?? NullLogger<RoutingDnsHijacker>.Instance;
    }

    public async ValueTask<DnsHandlingResult> HandleAsync(ReadOnlyMemory<byte> query, CancellationToken cancellationToken = default)
    {
        if (!FakeDnsMessage.TryReadQuestion(query.Span, out DnsQuestion question))
        {
            _logger.LogDebug("Dropping malformed DNS query (length={Length})", query.Length);
            return DnsHandlingResult.Dropped();
        }

        RouteDecision decision = await _router.RouteAsync(new ConnectionContext(question.Domain, 0), cancellationToken).ConfigureAwait(false);

        if (decision.Intercept)
        {
            if (question.RecordType == DnsRecordType.Aaaa)
            {
                byte[] noData = FakeDnsMessage.CreateNoDataResponse(question);
                _logger.LogDebug("Intercepted AAAA for {Domain}, returning NODATA", question.Domain);
                return DnsHandlingResult.Intercepted(noData);
            }

            if (question.RecordType != DnsRecordType.A)
            {
                _logger.LogDebug("Dropping unsupported intercepted question type {Type} for {Domain}", question.RecordType, question.Domain);
                return DnsHandlingResult.Dropped();
            }

            IPAddress fakeIp = _store.Allocate(question.Domain);
            byte[] response = FakeDnsMessage.CreateAResponse(question, fakeIp);
            _logger.LogDebug("Intercepted A for {Domain} -> {FakeIp}", question.Domain, fakeIp);
            return DnsHandlingResult.Intercepted(response);
        }

        if (_upstream is null)
        {
            _logger.LogDebug("No upstream resolver configured; dropping query for {Domain}", question.Domain);
            return DnsHandlingResult.Dropped();
        }

        byte[]? upstreamResponse = await _upstream.ResolveAsync(query, cancellationToken).ConfigureAwait(false);
        if (upstreamResponse is null)
        {
            _logger.LogDebug("Upstream returned no answer for {Domain}", question.Domain);
            return DnsHandlingResult.Dropped();
        }

        return DnsHandlingResult.Forwarded(upstreamResponse);
    }
}
```

- [ ] **Step 6: Run tests, expect green**

Run: `dotnet test tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj --filter FullyQualifiedName~RoutingDnsHijackerTests`
Expected: 4 pass.

- [ ] **Step 7: Delete obsolete code**

```bash
git rm src/DotnetTun.Core/Dns/FakeDnsResolver.cs
git rm tests/DotnetTun.Core.Tests/Dns/FakeDnsResolverTests.cs
git rm src/DotnetTun.Core/Dns/FakeIpPool.cs
git rm tests/DotnetTun.Core.Tests/FakeIp/FakeIpPoolTests.cs
git rm src/DotnetTun.Core/Routing/DomainInterceptRouter.cs
git rm src/DotnetTun.Abstractions/Routing/InterceptDecision.cs
```

Update `src/DotnetTun.Core/Dns/FakeDnsServer.cs` to take `IDnsHijacker` instead of `FakeDnsResolver`:

```csharp
public sealed class FakeDnsServer(IDnsHijacker hijacker, IPAddress listenAddress, int port) : IAsyncDisposable
{
    // ... existing fields ...
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        // ... existing receive logic ...
        DnsHandlingResult result = await hijacker.HandleAsync(receiveResult.Buffer, cancellationToken).ConfigureAwait(false);
        if (result.Response is not null)
        {
            await udpClient.SendAsync(result.Response, receiveResult.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
        }
    }
}
```

Update `tests/DotnetTun.Core.Tests/Dns/FakeDnsServerTests.cs` to use `IDnsHijacker` test double instead of `FakeDnsResolver`.

- [ ] **Step 8: Run the full test suite**

Run: `dotnet test DotnetTun.slnx`
Expected: all green. If any test references deleted types, update or delete the test as part of this commit.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat(core): replace FakeDnsResolver/FakeIpPool with IDnsHijacker + IFakeIpStore"
```

---

## Task 5: Add `Name` to `IOutbound`; update `Socks5Outbound`

**Files:**
- Modify: `src/DotnetTun.Abstractions/IOutbound.cs`
- Modify: `src/DotnetTun.Outbounds.Socks5/Socks5Outbound.cs`
- Modify: `src/DotnetTun.Outbounds.Socks5/Socks5OutboundOptions.cs`
- Modify: `tests/DotnetTun.Core.Tests/Outbounds/Socks5OutboundTests.cs`

- [ ] **Step 1: Add `Name` to `IOutbound`**

```csharp
namespace DotnetTun.Abstractions;

public interface IOutbound
{
    string Name { get; }
    ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Add `Name` to `Socks5OutboundOptions`**

```csharp
public sealed record Socks5OutboundOptions
{
    public Socks5OutboundOptions(string host, int port, string name = "socks5", TimeSpan? handshakeTimeout = null)
    {
        // ... existing validation ...
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Outbound name must not be empty.", nameof(name))
            : name.Trim();
        HandshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(10);
    }

    public string Name { get; }
    public string Host { get; }
    public int Port { get; }
    public TimeSpan HandshakeTimeout { get; }
    public override string ToString() => $"socks5://{Host}:{Port}";
}
```

- [ ] **Step 3: Expose `Name` on `Socks5Outbound`**

Add at top of class:

```csharp
public string Name => options.Name;
```

- [ ] **Step 4: Add a test for `Name`**

```csharp
[Fact]
public void Name_ReflectsOptionsName()
{
    Socks5Outbound outbound = new(new Socks5OutboundOptions("127.0.0.1", 1080, name: "my-socks"));
    Assert.Equal("my-socks", outbound.Name);
}
```

- [ ] **Step 5: Run tests, expect green**

Run: `dotnet test tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj --filter FullyQualifiedName~Socks5Outbound`
Expected: pass.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(outbounds): add Name to IOutbound and Socks5Outbound"
```

---

## Task 6: Refactor `ITunDevice` — drop `int fileDescriptor`

**Files:**
- Modify: `src/DotnetTun.Abstractions/ITunDevice.cs`
- Modify: `src/DotnetTun.Platforms.MacOS/Networking/MacUtunDevice.cs`
- Modify: `src/DotnetTun.Core/Sessions/TunPacketPump.cs`
- Modify: `src/DotnetTun.Core/Sessions/RawTunProxy.cs`
- Modify: `tests/DotnetTun.Platforms.MacOS.Tests/Networking/MacUtunDeviceTests.cs`
- Modify: `tests/DotnetTun.Core.Tests/Sessions/TunPacketPumpTests.cs`
- Delete: `src/DotnetTun.Abstractions/TunDeviceOpenResult.cs`
- Delete: `src/DotnetTun.Abstractions/TunDeviceCloseResult.cs`
- Delete: `src/DotnetTun.Abstractions/TunPacketIoResult.cs`

- [ ] **Step 1: Replace `ITunDevice.cs`**

```csharp
namespace DotnetTun.Abstractions;

public interface ITunDevice : IAsyncDisposable
{
    bool IsOpen { get; }
    string? InterfaceName { get; }
    ValueTask OpenAsync(CancellationToken cancellationToken = default);
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
    ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default);
    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}
```

`ReadAsync` returns the byte count written into `buffer`. Failures throw (`IOException`/`OperationCanceledException`); the old `Failed/Transferred` result wrapper is gone.

- [ ] **Step 2: Delete `TunDeviceOpenResult.cs`, `TunDeviceCloseResult.cs`, `TunPacketIoResult.cs`**

```bash
git rm src/DotnetTun.Abstractions/TunDeviceOpenResult.cs \
       src/DotnetTun.Abstractions/TunDeviceCloseResult.cs \
       src/DotnetTun.Abstractions/TunPacketIoResult.cs
```

- [ ] **Step 3: Adapt `MacUtunDevice` to the new shape**

Internal state: keep `IUtunNativeApi`. Add private `int? _fileDescriptor`. Implement methods:

```csharp
public bool IsOpen => _fileDescriptor is not null;
public string? InterfaceName { get; private set; }

public ValueTask OpenAsync(CancellationToken cancellationToken = default)
{
    if (IsOpen) return ValueTask.CompletedTask;
    cancellationToken.ThrowIfCancellationRequested();

    Span<byte> nameBuffer = stackalloc byte[256];
    int fd = _nativeApi.OpenUtun(-1, nameBuffer, out int errno);
    if (fd < 0) throw new IOException($"utun open failed (errno={errno})");

    int nullIndex = nameBuffer.IndexOf((byte)0);
    string interfaceName = Encoding.ASCII.GetString(nullIndex < 0 ? nameBuffer : nameBuffer[..nullIndex]).Trim();
    if (string.IsNullOrWhiteSpace(interfaceName))
    {
        _nativeApi.Close(fd, out _);
        throw new IOException("utun open returned empty interface name");
    }

    _fileDescriptor = fd;
    InterfaceName = interfaceName;
    return ValueTask.CompletedTask;
}

public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
{
    int fd = RequireFd();
    cancellationToken.ThrowIfCancellationRequested();
    using var registration = cancellationToken.Register(static state =>
    {
        ReadCancellationState s = (ReadCancellationState)state!;
        if (s.NativeApi.Close(s.FileDescriptor, out _) == 0)
            s.CancellationClosedFileDescriptors[s.FileDescriptor] = 0;
    }, new ReadCancellationState(_nativeApi, _cancellationClosedFileDescriptors, fd));

    byte[] frame = new byte[buffer.Length + UtunHeaderLength];
    int n = _nativeApi.ReadPacket(fd, frame, out int errno);
    if (n < 0 && cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
    if (n < 0) throw new IOException($"utun read failed (errno={errno})");
    if (n < UtunHeaderLength) throw new IOException("utun frame shorter than family header");
    uint family = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(0, UtunHeaderLength));
    if (family is not AddressFamilyInet and not AddressFamilyInet6) throw new IOException($"unsupported utun family: {family}");

    int payload = n - UtunHeaderLength;
    frame.AsSpan(UtunHeaderLength, payload).CopyTo(buffer.Span);
    return ValueTask.FromResult(payload);
}

public ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
{
    int fd = RequireFd();
    cancellationToken.ThrowIfCancellationRequested();
    if (!TryGetUtunAddressFamily(packet.Span, out uint family))
        throw new IOException("unsupported IP version");

    byte[] frame = new byte[packet.Length + UtunHeaderLength];
    BinaryPrimitives.WriteUInt32BigEndian(frame, family);
    packet.Span.CopyTo(frame.AsSpan(UtunHeaderLength));
    int n = _nativeApi.WritePacket(fd, frame, out int errno);
    if (n < 0) throw new IOException($"utun write failed (errno={errno})");
    return ValueTask.CompletedTask;
}

public ValueTask CloseAsync(CancellationToken cancellationToken = default)
{
    if (_fileDescriptor is null) return ValueTask.CompletedTask;
    int fd = _fileDescriptor.Value;
    if (_cancellationClosedFileDescriptors.TryRemove(fd, out _))
    {
        _fileDescriptor = null;
        return ValueTask.CompletedTask;
    }
    int rc = _nativeApi.Close(fd, out int errno);
    _fileDescriptor = null;
    if (rc < 0) throw new IOException($"utun close failed (errno={errno})");
    return ValueTask.CompletedTask;
}

public ValueTask DisposeAsync() => CloseAsync();

private int RequireFd() => _fileDescriptor ?? throw new InvalidOperationException("Tun device is not open.");
```

Drop `OpenAsync` legacy overload returning `MacUtunOpenResult` (move it to `internal` if any consumers remain — none expected).

- [ ] **Step 4: Adapt `TunPacketPump` and `RawTunProxy`**

`TunPacketPump.RunAsync(CancellationToken)` no longer needs `int fileDescriptor`. Its inner loop becomes:

```csharp
public async Task RunAsync(CancellationToken cancellationToken = default)
{
    if (!_tunDevice.IsOpen) await _tunDevice.OpenAsync(cancellationToken).ConfigureAwait(false);

    using var runSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    Task? outboundWriteTask = _outboundPackets is null
        ? null
        : WriteOutboundPacketsAsync(_outboundPackets, runSource.Token);

    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await PumpOnceAsync(runSource.Token).ConfigureAwait(false);
        }
    }
    finally
    {
        await runSource.CancelAsync().ConfigureAwait(false);
        if (outboundWriteTask is not null)
        {
            try { await outboundWriteTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }
}

public async ValueTask PumpOnceAsync(CancellationToken cancellationToken = default)
{
    byte[] readBuffer = new byte[_mtu];
    int read = await _tunDevice.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
    if (read <= 0) return;

    byte[] packet = readBuffer.AsSpan(0, read).ToArray();
    IReadOnlyList<ReadOnlyMemory<byte>> responses = await _handler.HandleAsync(packet, cancellationToken).ConfigureAwait(false);
    foreach (ReadOnlyMemory<byte> response in responses)
    {
        await WritePacketAsync(response, cancellationToken).ConfigureAwait(false);
    }
}

private async ValueTask WritePacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
{
    await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
    try { await _tunDevice.WriteAsync(packet, cancellationToken).ConfigureAwait(false); }
    finally { _writeLock.Release(); }
}
```

`RawTunProxy.RunAsync` calls `_packetPump.RunAsync` directly. Remove the `RunOpenAsync(int fileDescriptor, …)` overload.

- [ ] **Step 5: Update `MacUtunDeviceTests.cs`**

Remove tests that asserted `int fileDescriptor` plumbing. Replace with tests that drive `OpenAsync → ReadAsync/WriteAsync → CloseAsync` against `IUtunNativeApi` doubles. Keep cancellation test, adapted to not pass an explicit fd.

Specifically: the existing AF-prefix tests (already correct) need `device.OpenAsync()` first, then they call `device.ReadAsync(buffer, ct)` (no fd argument). Update each test to open the device first, drop the explicit `123` fd argument throughout.

- [ ] **Step 6: Update `TunPacketPumpTests.cs` and `RawTunProxyTests.cs`**

Drop the `fileDescriptor` arg threading. Tests that used `RunOpenAsync(int fd, ...)` switch to `RunAsync(ct)` and expect the tun device's `OpenAsync` to be called.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test DotnetTun.slnx`
Expected: all green.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "refactor(abstractions): drop int fileDescriptor from ITunDevice"
```

---

## Task 7: Hide platform-private `*OpenResult` types as `internal`

**Files:**
- Modify: `src/DotnetTun.Platforms.MacOS/Networking/MacUtunOpenResult.cs`
- Modify: `src/DotnetTun.Platforms.Linux/Networking/LinuxTunOpenResult.cs`
- Modify: `src/DotnetTun.Platforms.Windows/Networking/WindowsTunOpenResult.cs`

- [ ] **Step 1: Change visibility on each file**

Each file has `public sealed record …`. Change `public` to `internal`. Audit references — `MacUtunDevice` is the only consumer for the macOS one (after Task 6). Linux/Windows result types are referenced only inside their respective platform projects.

- [ ] **Step 2: Build the solution**

Run: `dotnet build DotnetTun.slnx`
Expected: no errors. Any external consumer that referenced the old public name now gets a compile error — fix in place by switching to throwing `IOException` or by reading `ITunDevice.IsOpen`.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "refactor(platforms): make platform-private OpenResult types internal"
```

---

## Task 8: Replace `IProxyLogger` with `Microsoft.Extensions.Logging.ILogger<T>`

**Files:**
- Modify: `Directory.Packages.props` (add `Microsoft.Extensions.Logging.Abstractions`)
- Modify: `src/DotnetTun.Abstractions/DotnetTun.Abstractions.csproj` (add reference)
- Modify: `src/DotnetTun.Core/DotnetTun.Core.csproj` (add reference)
- Delete: `src/DotnetTun.Abstractions/Diagnostics/IProxyLogger.cs`
- Delete: `src/DotnetTun.Abstractions/Diagnostics/NullProxyLogger.cs`
- Delete: `tests/DotnetTun.Core.Tests/Diagnostics/FakeDnsLoggingTests.cs` (or rewrite against `ILogger`)

- [ ] **Step 1: Add the package version**

In `Directory.Packages.props`, add inside `<ItemGroup>`:

```xml
<PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="9.0.0" />
```

- [ ] **Step 2: Reference from Core and Abstractions**

In `src/DotnetTun.Abstractions/DotnetTun.Abstractions.csproj` and `src/DotnetTun.Core/DotnetTun.Core.csproj`, add:

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
</ItemGroup>
```

- [ ] **Step 3: Delete `IProxyLogger.cs` and `NullProxyLogger.cs`**

```bash
git rm src/DotnetTun.Abstractions/Diagnostics/IProxyLogger.cs
git rm src/DotnetTun.Abstractions/Diagnostics/NullProxyLogger.cs
```

- [ ] **Step 4: Update consumers to take `ILogger<T>`**

`RoutingDnsHijacker` already uses `ILogger<RoutingDnsHijacker>`. Audit any other consumer:

```bash
grep -rln "IProxyLogger\|NullProxyLogger" src/ tests/ samples/
```

For each match: replace the field with `ILogger<TConsumer>`, replace constructor parameter, replace call sites with `LogDebug` / `LogInformation` / `LogWarning` (use structured templates).

- [ ] **Step 5: Update tests**

Tests using `IProxyLogger` doubles switch to `Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance` for happy paths, or a small `ListLogger<T>` capture helper for assertion (write a helper in `tests/DotnetTun.Core.Tests/TestSupport/ListLogger.cs`):

```csharp
public sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}
```

- [ ] **Step 6: Run the full suite**

Run: `dotnet test DotnetTun.slnx`
Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "refactor(diagnostics): replace IProxyLogger with ILogger<T>"
```

---

## Task 9: Add UDP path in `Ipv4TunPacketHandler`; introduce `UdpIpv4PacketHandler` and `Dns53Sink`

**Files:**
- Create: `src/DotnetTun.Core/Sessions/IUdpSegmentHandler.cs`
- Create: `src/DotnetTun.Core/Sessions/UdpIpv4PacketHandler.cs`
- Create: `src/DotnetTun.Core/Sessions/Dns53Sink.cs`
- Create: `src/DotnetTun.Core/Packets/UdpDatagram.cs`
- Create: `src/DotnetTun.Core/Packets/UdpDatagramBuilder.cs`
- Create: `tests/DotnetTun.Core.Tests/Sessions/UdpIpv4PacketHandlerTests.cs`
- Create: `tests/DotnetTun.Core.Tests/Sessions/Dns53SinkTests.cs`
- Modify: `src/DotnetTun.Core/Sessions/Ipv4TunPacketHandler.cs`
- Modify: `src/DotnetTun.Core/Sessions/RawTcpTunPipeline.cs` → rename `RawTunPipeline.cs`

- [ ] **Step 1: Add IPv4 protocol-number constant**

Inside `Ipv4Packet` (or a `Protocols.cs` companion):

```csharp
public static class IpProtocols
{
    public const byte Tcp = 6;
    public const byte Udp = 17;
}
```

- [ ] **Step 2: Write a parser for UDP datagrams**

`UdpDatagram` shape: `record struct UdpDatagram(ushort SourcePort, ushort DestinationPort, ReadOnlyMemory<byte> Payload)`. Static `TryParse(ReadOnlyMemory<byte> ipPacket, out UdpDatagram, out int payloadOffset)`.

`UdpDatagramBuilder.Build(srcAddr, dstAddr, srcPort, dstPort, payload) -> byte[]` constructs an IPv4 + UDP packet with correct checksums (use existing `Ipv4Checksum`, add `UdpChecksum` similar to `TcpChecksum`).

- [ ] **Step 3: Define `IUdpSegmentHandler` and `UdpIpv4PacketHandler`**

```csharp
public interface IUdpSegmentHandler
{
    ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> HandleAsync(Ipv4Packet ipPacket, UdpDatagram datagram, CancellationToken cancellationToken);
}

public sealed class UdpIpv4PacketHandler(IReadOnlyDictionary<ushort, IUdpSegmentHandler> portHandlers) : ITcpSegmentHandler
{
    public async ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> HandleAsync(Ipv4Packet packet, CancellationToken cancellationToken)
    {
        if (!UdpDatagram.TryParse(packet.Payload, out UdpDatagram datagram)) return Array.Empty<ReadOnlyMemory<byte>>();
        if (!portHandlers.TryGetValue(datagram.DestinationPort, out IUdpSegmentHandler? handler)) return Array.Empty<ReadOnlyMemory<byte>>();
        return await handler.HandleAsync(packet, datagram, cancellationToken).ConfigureAwait(false);
    }
}
```

(`UdpIpv4PacketHandler` implements the same interface shape as `TcpIpv4PacketHandler`: `HandleAsync(Ipv4Packet)`. Both are dispatched from `Ipv4TunPacketHandler`. Inspect the existing `Ipv4TunPacketHandler` to confirm dispatcher contract — extract a common `IIpv4ProtocolHandler` if needed.)

- [ ] **Step 4: Modify `Ipv4TunPacketHandler` to dispatch by protocol number**

The current `Ipv4TunPacketHandler` only consumes a TCP handler. Refactor:

```csharp
public sealed class Ipv4TunPacketHandler(ITcpSegmentHandler tcpHandler, UdpIpv4PacketHandler? udpHandler = null) : ITunPacketHandler
{
    public async ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> HandleAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        if (!Ipv4Packet.TryParse(packet, out Ipv4Packet parsed)) return Array.Empty<ReadOnlyMemory<byte>>();
        return parsed.Protocol switch
        {
            IpProtocols.Tcp => await tcpHandler.HandleAsync(parsed, cancellationToken).ConfigureAwait(false),
            IpProtocols.Udp when udpHandler is not null => await udpHandler.HandleAsync(parsed, cancellationToken).ConfigureAwait(false),
            _ => Array.Empty<ReadOnlyMemory<byte>>()
        };
    }
}
```

- [ ] **Step 5: Add `Dns53Sink`**

```csharp
public sealed class Dns53Sink(IDnsHijacker hijacker) : IUdpSegmentHandler
{
    public async ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> HandleAsync(Ipv4Packet packet, UdpDatagram datagram, CancellationToken cancellationToken)
    {
        DnsHandlingResult result = await hijacker.HandleAsync(datagram.Payload, cancellationToken).ConfigureAwait(false);
        if (result.Response is null) return Array.Empty<ReadOnlyMemory<byte>>();

        byte[] responsePacket = UdpDatagramBuilder.Build(
            sourceAddress: packet.DestinationAddress,
            destinationAddress: packet.SourceAddress,
            sourcePort: datagram.DestinationPort,
            destinationPort: datagram.SourcePort,
            payload: result.Response);
        return [responsePacket];
    }
}
```

- [ ] **Step 6: Tests**

Cover: TCP-only packet pass-through (UDP handler null), UDP:53 DNS query → hijacker invoked → response packet returned with swapped 5-tuple, UDP non-53 dropped.

- [ ] **Step 7: Run tests**

Run: `dotnet test tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj`
Expected: green.

- [ ] **Step 8: Rename `RawTcpTunPipeline.cs` → `RawTunPipeline.cs`** and accept `IDnsHijacker`:

```csharp
public sealed class RawTunPipeline(ITunPacketHandler packetHandler, TunOutboundPacketQueue? outboundPackets = null, IAsyncDisposable? disposable = null) : IAsyncDisposable
{
    public static RawTunPipeline Create(
        IFakeIpStore fakeIpStore,
        IRouter router,
        IReadOnlyDictionary<string, IOutbound> outbounds,
        IDnsHijacker? dnsHijacker,
        uint serverInitialSequence,
        TimeSpan? responseReadTimeout = null)
    {
        TcpSessionTable sessions = new();
        TunOutboundPacketQueue outboundPackets = new();
        OutboundTcpPayloadSink payloadSink = new(fakeIpStore, router, outbounds, responseReadTimeout, /*remotePayloadHandler=*/...);
        RawTcpSessionHandler rawTcpHandler = new(sessions, serverInitialSequence, payloadSink);
        TcpIpv4PacketHandler tcpHandler = new(rawTcpHandler);

        UdpIpv4PacketHandler? udpHandler = null;
        if (dnsHijacker is not null)
        {
            Dictionary<ushort, IUdpSegmentHandler> handlers = new() { [53] = new Dns53Sink(dnsHijacker) };
            udpHandler = new UdpIpv4PacketHandler(handlers);
        }

        Ipv4TunPacketHandler packetHandler = new(tcpHandler, udpHandler);
        return new RawTunPipeline(packetHandler, outboundPackets, payloadSink);
    }
}
```

`OutboundTcpPayloadSink` constructor changes to accept `IRouter` + `IReadOnlyDictionary<string, IOutbound>` and look up the outbound by `decision.OutboundName`. Audit all call sites; update tests accordingly.

- [ ] **Step 9: Run the full suite**

Run: `dotnet test DotnetTun.slnx`
Expected: green.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat(core): add UDP/DNS path in TUN pipeline; rename RawTcpTunPipeline to RawTunPipeline"
```

---

## Task 10: Implement `UdpUpstreamDnsResolver`

**Files:**
- Create: `src/DotnetTun.Core/Dns/UdpUpstreamDnsResolver.cs`
- Create: `tests/DotnetTun.Core.Tests/Dns/UdpUpstreamDnsResolverTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
using System.Net;
using System.Net.Sockets;
using DotnetTun.Core.Dns;
using Xunit;

namespace DotnetTun.Core.Tests.Dns;

public sealed class UdpUpstreamDnsResolverTests
{
    [Fact]
    public async Task ResolveAsync_RoundTripsThroughLocalUdpServer()
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;

        Task<UdpReceiveResult> recvTask = server.ReceiveAsync();
        UdpUpstreamDnsResolver resolver = new(new IPEndPoint(IPAddress.Loopback, port), TimeSpan.FromSeconds(2));

        Task<byte[]?> resolveTask = resolver.ResolveAsync(new byte[] { 0x12, 0x34 }, TestContext.Current.CancellationToken).AsTask();
        UdpReceiveResult received = await recvTask;
        await server.SendAsync([0xAB, 0xCD], received.RemoteEndPoint);

        byte[]? response = await resolveTask;
        Assert.Equal(new byte[] { 0xAB, 0xCD }, response);
    }
}
```

- [ ] **Step 2: Run, expect fail**

Run: `dotnet test tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj --filter UdpUpstreamDnsResolverTests`
Expected: type does not exist.

- [ ] **Step 3: Implement `UdpUpstreamDnsResolver.cs`**

```csharp
using System.Net;
using System.Net.Sockets;
using DotnetTun.Core.Dns; // namespace for IUpstreamDnsResolver

namespace DotnetTun.Core.Dns;

public sealed class UdpUpstreamDnsResolver(IPEndPoint upstream, TimeSpan timeout) : IUpstreamDnsResolver
{
    public async ValueTask<byte[]?> ResolveAsync(ReadOnlyMemory<byte> query, CancellationToken cancellationToken = default)
    {
        using UdpClient client = new(AddressFamily.InterNetwork);
        using CancellationTokenSource timeoutSource = new(timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await client.SendAsync(query, upstream, linked.Token).ConfigureAwait(false);
            UdpReceiveResult result = await client.ReceiveAsync(linked.Token).ConfigureAwait(false);
            return result.Buffer;
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: Run tests, expect green**

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): add UdpUpstreamDnsResolver"
```

---

## Task 11: Refactor `TransparentProxyBuilder` API

**Files:**
- Modify: `src/DotnetTun.Core/TransparentProxyBuilder.cs`
- Modify: `src/DotnetTun.Core/TransparentProxy.cs`
- Modify: `src/DotnetTun.Abstractions/ITransparentProxy.cs`
- Modify: `tests/DotnetTun.Core.Tests/Builder/TransparentProxyBuilderTests.cs`
- Delete: `src/DotnetTun.Core/DotnetTunEngine.cs`
- Delete: `src/DotnetTun.Core/DotnetTunDryRunPlan.cs`
- Delete: `tests/DotnetTun.Core.Tests/Engine/DotnetTunEngineTests.cs`

- [ ] **Step 1: Slim `ITransparentProxy`**

```csharp
namespace DotnetTun.Abstractions;

public interface ITransparentProxy : IAsyncDisposable
{
    ValueTask StartAsync(CancellationToken cancellationToken = default);
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}
```

(`Options` and `Outbounds` properties removed.)

- [ ] **Step 2: Replace `TransparentProxyBuilder.cs`**

```csharp
using System.Net;
using DotnetTun.Abstractions;
using DotnetTun.Abstractions.Dns;
using DotnetTun.Abstractions.Routing;
using DotnetTun.Core.Dns;
using DotnetTun.Core.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetTun.Core;

public sealed class TransparentProxyBuilder
{
    private ITunDevice? _tunDevice;
    private IRouter? _router;
    private IFakeIpStore? _fakeIpStore;
    private IDnsHijacker? _dnsHijacker;
    private IUpstreamDnsResolver? _upstreamDnsResolver;
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private readonly List<DomainInterceptRule> _rules = [];
    private readonly Dictionary<string, IOutbound> _outbounds = new(StringComparer.OrdinalIgnoreCase);
    private string _fakeIpStart = "198.18.0.1";
    private string _fakeIpEnd = "198.19.255.254";
    private uint _serverInitialSequence = 9_000;

    public TransparentProxyBuilder UseTunDevice(ITunDevice tunDevice)
    {
        _tunDevice = tunDevice ?? throw new ArgumentNullException(nameof(tunDevice));
        return this;
    }

    public TransparentProxyBuilder UseRouter(IRouter router)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        return this;
    }

    public TransparentProxyBuilder UseFakeIpStore(IFakeIpStore store)
    {
        _fakeIpStore = store ?? throw new ArgumentNullException(nameof(store));
        return this;
    }

    public TransparentProxyBuilder UseDnsHijacker(IDnsHijacker hijacker)
    {
        _dnsHijacker = hijacker ?? throw new ArgumentNullException(nameof(hijacker));
        return this;
    }

    public TransparentProxyBuilder UseUpstreamDns(IUpstreamDnsResolver resolver)
    {
        _upstreamDnsResolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        return this;
    }

    public TransparentProxyBuilder UseFakeIpRange(string startInclusive, string endInclusive)
    {
        _fakeIpStart = startInclusive ?? throw new ArgumentNullException(nameof(startInclusive));
        _fakeIpEnd = endInclusive ?? throw new ArgumentNullException(nameof(endInclusive));
        return this;
    }

    public TransparentProxyBuilder UseLoggerFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        return this;
    }

    public TransparentProxyBuilder AddRule(string pattern, string outboundName)
    {
        _rules.Add(new DomainInterceptRule(pattern, outboundName));
        return this;
    }

    public TransparentProxyBuilder AddOutbound(IOutbound outbound)
    {
        ArgumentNullException.ThrowIfNull(outbound);
        if (string.IsNullOrWhiteSpace(outbound.Name))
            throw new ArgumentException("Outbound.Name must not be empty.", nameof(outbound));
        _outbounds[outbound.Name] = outbound;
        return this;
    }

    public ITransparentProxy Build()
    {
        if (_tunDevice is null) throw new InvalidOperationException("UseTunDevice(...) must be called before Build().");
        if (_outbounds.Count == 0) throw new InvalidOperationException("At least one AddOutbound(...) is required.");

        IFakeIpStore store = _fakeIpStore ?? new FakeIpStore(IPAddress.Parse(_fakeIpStart), IPAddress.Parse(_fakeIpEnd));
        IRouter router = _router ?? new DomainRuleRouter(_rules);
        IDnsHijacker hijacker = _dnsHijacker ?? new RoutingDnsHijacker(
            router,
            store,
            _upstreamDnsResolver,
            _loggerFactory.CreateLogger<RoutingDnsHijacker>());

        return new BuiltTransparentProxy(_tunDevice, router, store, hijacker, _outbounds, _serverInitialSequence, _loggerFactory);
    }
}
```

- [ ] **Step 3: Make `BuiltTransparentProxy` real (sketch only — full body is Task 12)**

```csharp
internal sealed class BuiltTransparentProxy(
    ITunDevice tunDevice,
    IRouter router,
    IFakeIpStore fakeIpStore,
    IDnsHijacker dnsHijacker,
    IReadOnlyDictionary<string, IOutbound> outbounds,
    uint serverInitialSequence,
    ILoggerFactory loggerFactory) : ITransparentProxy
{
    private RawTunPipeline? _pipeline;
    private TunPacketPump? _pump;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    public ValueTask StartAsync(CancellationToken cancellationToken = default) => /* see Task 12 */ throw new NotImplementedException();
    public ValueTask StopAsync(CancellationToken cancellationToken = default) => /* see Task 12 */ throw new NotImplementedException();
    public ValueTask DisposeAsync() => StopAsync();
}
```

- [ ] **Step 4: Update `TransparentProxyBuilderTests.cs`**

Cover: missing tun device throws, missing outbound throws, default fake-ip range used, rule + outbound flow into router/registry.

- [ ] **Step 5: Delete `DotnetTunEngine.cs`, `DotnetTunDryRunPlan.cs`, `DotnetTunEngineTests.cs`**

```bash
git rm src/DotnetTun.Core/DotnetTunEngine.cs \
       src/DotnetTun.Core/DotnetTunDryRunPlan.cs \
       tests/DotnetTun.Core.Tests/Engine/DotnetTunEngineTests.cs
```

- [ ] **Step 6: Build the solution, expect compile errors only in the demo CLI** (which will be fixed in Task 14)

Run: `dotnet build DotnetTun.slnx`
Expected: `samples/DotnetTun.Demo.Cli` has compile errors. Other projects build clean.

To unblock the commit: temporarily comment out the dry-run path in `samples/DotnetTun.Demo.Cli/DotnetTunDemoCommand.cs` referencing `DotnetTunEngine`, leaving a `// TODO(Task 14): rewrite using TransparentProxyBuilder`.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(core): redesign TransparentProxyBuilder; remove DotnetTunEngine"
```

---

## Task 12: Wire `BuiltTransparentProxy.StartAsync` end-to-end

**Files:**
- Modify: `src/DotnetTun.Core/TransparentProxyBuilder.cs` (replace `BuiltTransparentProxy` body)
- Create: `tests/DotnetTun.Core.Tests/TransparentProxyEndToEndTests.cs`

- [ ] **Step 1: Write failing end-to-end test**

```csharp
using System.Net;
using DotnetTun.Abstractions;
using DotnetTun.Abstractions.Dns;
using DotnetTun.Abstractions.Routing;
using DotnetTun.Core;
using Xunit;

namespace DotnetTun.Core.Tests;

public sealed class TransparentProxyEndToEndTests
{
    [Fact]
    public async Task StartAsync_OpensTunAndPumpsPackets_StopAsyncCloses()
    {
        FakeTunDevice tun = new();
        StubOutbound outbound = new(name: "test");

        ITransparentProxy proxy = TransparentProxy.CreateBuilder()
            .UseTunDevice(tun)
            .AddRule("api.example.com", "test")
            .AddOutbound(outbound)
            .Build();

        await proxy.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(tun.IsOpen);

        await proxy.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(tun.IsOpen);
    }

    private sealed class FakeTunDevice : ITunDevice
    {
        public bool IsOpen { get; private set; }
        public string? InterfaceName => "fake0";
        public ValueTask OpenAsync(CancellationToken ct) { IsOpen = true; return ValueTask.CompletedTask; }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
            => new(Task.Delay(Timeout.Infinite, ct).ContinueWith<int>(_ => throw new OperationCanceledException(ct), TaskContinuationOptions.ExecuteSynchronously));
        public ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask CloseAsync(CancellationToken ct) { IsOpen = false; return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => CloseAsync(default);
    }

    private sealed class StubOutbound(string name) : IOutbound
    {
        public string Name { get; } = name;
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken ct) => throw new NotSupportedException();
    }
}
```

- [ ] **Step 2: Run, expect failure (`NotImplementedException`)**

Run: `dotnet test tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj --filter TransparentProxyEndToEndTests`

- [ ] **Step 3: Implement `BuiltTransparentProxy.StartAsync` and `StopAsync`**

```csharp
internal sealed class BuiltTransparentProxy(
    ITunDevice tunDevice,
    IRouter router,
    IFakeIpStore fakeIpStore,
    IDnsHijacker dnsHijacker,
    IReadOnlyDictionary<string, IOutbound> outbounds,
    uint serverInitialSequence,
    ILoggerFactory loggerFactory) : ITransparentProxy
{
    private readonly ILogger<BuiltTransparentProxy> _logger = loggerFactory.CreateLogger<BuiltTransparentProxy>();
    private RawTunPipeline? _pipeline;
    private TunPacketPump? _pump;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private int _state; // 0 = stopped, 1 = starting, 2 = running, 3 = stopping

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException("Proxy is not stopped.");

        try
        {
            _logger.LogInformation("Starting transparent proxy");
            await tunDevice.OpenAsync(cancellationToken).ConfigureAwait(false);

            _pipeline = RawTunPipeline.Create(fakeIpStore, router, outbounds, dnsHijacker, serverInitialSequence);
            _pump = new TunPacketPump(tunDevice, _pipeline.PacketHandler, mtu: 1500, _pipeline.OutboundPackets);

            _runCts = new CancellationTokenSource();
            _runTask = _pump.RunAsync(_runCts.Token);

            Volatile.Write(ref _state, 2);
            _logger.LogInformation("Transparent proxy running on {Interface}", tunDevice.InterfaceName);
        }
        catch
        {
            Volatile.Write(ref _state, 0);
            try { await tunDevice.CloseAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* swallow */ }
            throw;
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _state, 3, 2) != 2 && Volatile.Read(ref _state) != 1) return;

        _logger.LogInformation("Stopping transparent proxy");
        try
        {
            _runCts?.Cancel();
            if (_runTask is not null)
            {
                try { await _runTask.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            if (_pipeline is not null) await _pipeline.DisposeAsync().ConfigureAwait(false);
            await tunDevice.CloseAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
            _runTask = null;
            _pump = null;
            _pipeline = null;
            Volatile.Write(ref _state, 0);
        }
    }

    public ValueTask DisposeAsync() => StopAsync();
}
```

- [ ] **Step 4: Run tests, expect green**

Run: `dotnet test tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj --filter TransparentProxyEndToEndTests`

- [ ] **Step 5: Run the full suite**

Run: `dotnet test DotnetTun.slnx`
Expected: green.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): wire BuiltTransparentProxy.StartAsync end-to-end"
```

---

## Task 13: Add `IHostedService` integration in `DotnetTun.Hosting`

**Files:**
- Modify: `Directory.Packages.props` (add `Microsoft.Extensions.Hosting.Abstractions`)
- Modify: `src/DotnetTun.Hosting/DotnetTun.Hosting.csproj`
- Create: `src/DotnetTun.Hosting/TransparentProxyHostedService.cs`
- Modify: `src/DotnetTun.Hosting/TransparentProxyServiceCollectionExtensions.cs`
- Create: `tests/DotnetTun.Hosting.Tests/HostedService/TransparentProxyHostedServiceTests.cs`

- [ ] **Step 1: Add the package**

```xml
<PackageVersion Include="Microsoft.Extensions.Hosting.Abstractions" Version="9.0.0" />
```

```xml
<PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
```

- [ ] **Step 2: Implement `TransparentProxyHostedService`**

```csharp
using DotnetTun.Abstractions;
using Microsoft.Extensions.Hosting;

namespace DotnetTun.Hosting;

public sealed class TransparentProxyHostedService(ITransparentProxy proxy) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
        => proxy.StartAsync(cancellationToken).AsTask();

    public Task StopAsync(CancellationToken cancellationToken)
        => proxy.StopAsync(cancellationToken).AsTask();
}
```

- [ ] **Step 3: Update `TransparentProxyServiceCollectionExtensions`**

Replace existing extension with one that wires `Func<IServiceProvider, ITransparentProxy>` factory + the hosted service:

```csharp
using DotnetTun.Abstractions;
using DotnetTun.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotnetTun.Hosting;

public static class TransparentProxyServiceCollectionExtensions
{
    public static IServiceCollection AddTransparentProxy(this IServiceCollection services, Action<TransparentProxyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.TryAddSingleton<ITransparentProxy>(sp =>
        {
            TransparentProxyBuilder builder = TransparentProxy.CreateBuilder();
            builder.UseLoggerFactory(sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
            configure(builder);
            return builder.Build();
        });
        services.AddHostedService<TransparentProxyHostedService>();
        return services;
    }
}
```

- [ ] **Step 4: Tests**

```csharp
using DotnetTun.Abstractions;
using DotnetTun.Core;
using DotnetTun.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DotnetTun.Hosting.Tests.HostedService;

public sealed class TransparentProxyHostedServiceTests
{
    [Fact]
    public async Task HostedServiceStartsAndStopsProxy()
    {
        FakeTunDevice tun = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddTransparentProxy(b => b
            .UseTunDevice(tun)
            .AddRule("api.example.com", "test")
            .AddOutbound(new StubOutbound("test")));
        await using ServiceProvider sp = services.BuildServiceProvider();

        IHostedService hosted = sp.GetServices<IHostedService>().Single();
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(tun.IsOpen);
        await hosted.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(tun.IsOpen);
    }

    /* FakeTunDevice + StubOutbound copied from TransparentProxyEndToEndTests; or extract to tests/DotnetTun.TestSupport/ */
}
```

- [ ] **Step 5: Run, expect green**

Run: `dotnet test tests/DotnetTun.Hosting.Tests/DotnetTun.Hosting.Tests.csproj`

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(hosting): add TransparentProxyHostedService and IHostedService wiring"
```

---

## Task 14: Update demo CLI to use the new builder API

**Files:**
- Modify: `samples/DotnetTun.Demo.Cli/DotnetTunDemoCommand.cs`
- Modify: `tests/DotnetTun.Demo.Cli.Tests/TunCommandTests.cs`
- Modify: `tests/DotnetTun.Demo.Cli.Tests/DnsCommandTests.cs`
- Modify: `tests/DotnetTun.Demo.Cli.Tests/BridgeCommandTests.cs`

- [ ] **Step 1: Replace `tun` subcommand body**

The new `tun` `RunAsync` flow:

```csharp
TransparentProxyBuilder builder = TransparentProxy.CreateBuilder()
    .UseTunDevice(runtime.CreateTunDevice())
    .UseUpstreamDns(new UdpUpstreamDnsResolver(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53), TimeSpan.FromSeconds(2)))
    .AddRule(domain, "socks5")
    .AddOutbound(new Socks5Outbound(ParseSocks5(socks5Endpoint)));

await using ITransparentProxy proxy = builder.Build();
await proxy.StartAsync(stopSource.Token);

MacTunOptions macOptions = CreateMacOptions(/* read InterfaceName from somewhere */ "utun-auto");
MacTunConfigurator configurator = runtime.CreateConfigurator();
await configurator.ConfigureAsync(macOptions, stopSource.Token);

try
{
    await Task.Delay(Timeout.Infinite, stopSource.Token);
}
catch (OperationCanceledException) { }
finally
{
    await configurator.CleanupAsync(macOptions, CancellationToken.None);
    await proxy.StopAsync(CancellationToken.None);
}
```

(Note: the macOS configurator path needs the actual interface name picked by the kernel. Add `string? CurrentInterfaceName` accessor on `BuiltTransparentProxy` or surface `tun.InterfaceName` after `OpenAsync`. Concretely: after `proxy.StartAsync`, the demo retrieves the device via the runtime and reads `tun.InterfaceName`.)

Drop the `Func<ITunDevice, FakeIpPool, IOutbound, int, TimeSpan?, ITunDemoRawTunProxy>` factory closure on `TunDemoRuntime` — the new builder removes the need.

Drop the `--dry-run` branch entirely (no longer modeled).

- [ ] **Step 2: Update `TunCommandTests`**

Adapt assertions to the new flow: instead of asserting `RawTunProxy.RunOpenAsync(fd, ct)`, assert that `TransparentProxy.StartAsync` is called with a builder having `UseTunDevice`, `AddRule`, `AddOutbound`. Use a stub `ITransparentProxy` factory injected through `TunDemoRuntime`.

- [ ] **Step 3: Run demo CLI tests**

Run: `dotnet test tests/DotnetTun.Demo.Cli.Tests/DotnetTun.Demo.Cli.Tests.csproj`
Expected: green.

- [ ] **Step 4: Build full solution**

Run: `dotnet build DotnetTun.slnx`
Expected: green.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(samples): rewire demo CLI to use new TransparentProxyBuilder API"
```

---

## Task 15: Enable `Microsoft.CodeAnalysis.PublicApiAnalyzers` and capture the v0.1 baseline

**Files:**
- Modify: `Directory.Packages.props` (add the analyzer)
- Modify: `Directory.Build.props` (reference globally for packable projects)
- Create: `src/DotnetTun.Abstractions/PublicAPI.Shipped.txt`
- Create: `src/DotnetTun.Abstractions/PublicAPI.Unshipped.txt`
- Create: `src/DotnetTun.Core/PublicAPI.Shipped.txt`
- Create: `src/DotnetTun.Core/PublicAPI.Unshipped.txt`
- Create: `src/DotnetTun.Hosting/PublicAPI.Shipped.txt`
- Create: `src/DotnetTun.Hosting/PublicAPI.Unshipped.txt`
- Create: `src/DotnetTun.Outbounds.Socks5/PublicAPI.Shipped.txt`
- Create: `src/DotnetTun.Outbounds.Socks5/PublicAPI.Unshipped.txt`
- Create: `src/DotnetTun.Platforms.MacOS/PublicAPI.Shipped.txt`
- Create: `src/DotnetTun.Platforms.MacOS/PublicAPI.Unshipped.txt`

- [ ] **Step 1: Add the package version**

```xml
<PackageVersion Include="Microsoft.CodeAnalysis.PublicApiAnalyzers" Version="3.3.4" />
```

- [ ] **Step 2: Wire it for packable projects**

In `Directory.Build.props`, add:

```xml
<ItemGroup Condition="'$(IsPackable)' == 'true'">
  <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers">
    <PrivateAssets>all</PrivateAssets>
  </PackageReference>
  <AdditionalFiles Include="PublicAPI.Shipped.txt" Condition="Exists('PublicAPI.Shipped.txt')" />
  <AdditionalFiles Include="PublicAPI.Unshipped.txt" Condition="Exists('PublicAPI.Unshipped.txt')" />
</ItemGroup>
```

- [ ] **Step 3: Build the solution**

Run: `dotnet build DotnetTun.slnx 2>&1 | tee /tmp/dotnettun-build.log`
Expected: thousands of `RS0016` warnings ("symbol is not part of the declared public API"). This is expected.

- [ ] **Step 4: Capture the v0.1 baseline**

For each packable project, scan the warnings and populate the corresponding `PublicAPI.Unshipped.txt` with one declaration per line. The analyzer ships a code fix; in CI / locally, run the IDE bulk fix-all, or alternately use the script:

```bash
# Per project, regex the build log:
grep "PublicAPI.Unshipped.txt" /tmp/dotnettun-build.log | grep "DotnetTun.Abstractions" | ...
```

The simplest path is using the bundled fix:
```bash
dotnet format analyzers DotnetTun.slnx --diagnostics RS0016
```

After this completes, every packable project has `PublicAPI.Shipped.txt` (empty for first cut) and `PublicAPI.Unshipped.txt` (full surface).

- [ ] **Step 5: Move shipped surface from Unshipped to Shipped**

For v0.1, treat the entire current surface as "shipped". For each packable project:

```bash
mv src/DotnetTun.Abstractions/PublicAPI.Unshipped.txt src/DotnetTun.Abstractions/PublicAPI.Shipped.txt
touch src/DotnetTun.Abstractions/PublicAPI.Unshipped.txt
# repeat for each project
```

- [ ] **Step 6: Build, expect zero analyzer warnings**

Run: `dotnet build DotnetTun.slnx`
Expected: build succeeds with no `RS00xx` warnings.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "build: enable PublicApiAnalyzers and capture v0.1 baseline"
```

---

## Task 16: Cleanup — sweep dead code paths

**Files:**
- (Audit-driven; expected residue from earlier tasks)

- [ ] **Step 1: Search for any remaining references to deleted types**

```bash
grep -rln "FakeDnsResolver\|DomainInterceptRouter\|InterceptDecision\|FakeIpPool\|DotnetTunEngine\|DotnetTunDryRunPlan\|IProxyLogger\|TunDeviceOpenResult\|TunPacketIoResult\|TunDeviceCloseResult" \
  src/ tests/ samples/
```

For each match:
- If in a test file: delete or rewrite to use the new types.
- If in production code: file a separate task — should not exist after Tasks 2–11.

- [ ] **Step 2: Build and run all tests**

Run: `dotnet build DotnetTun.slnx && dotnet test DotnetTun.slnx`
Expected: green.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "chore: remove residual references to retired types"
```

---

## Self-Review Checklist

Before handing this plan off:

- [ ] **Coverage**: every "in scope" item from the Scope section maps to a task.
  - IRouter/ConnectionContext/RouteDecision → Task 1, 2
  - IFakeIpStore → Task 3
  - IDnsHijacker/DnsHandlingResult → Task 4
  - IOutbound.Name → Task 5
  - ITunDevice refactor → Task 6
  - Hide *OpenResult → Task 7
  - ILogger<T> migration → Task 8
  - UDP/DNS path → Task 9
  - UdpUpstreamDnsResolver → Task 10
  - Builder API → Task 11
  - StartAsync wiring → Task 12
  - IHostedService → Task 13
  - Demo CLI rewire → Task 14
  - PublicApiAnalyzers → Task 15
  - Dead-code sweep → Task 16
- [ ] **No TBD/TODO** in any step (a single `// TODO(Task 14)` placeholder appears in Task 11 step 6 and is intentionally cleared by Task 14).
- [ ] **Type consistency**: `RouteDecision` / `IRouter` / `IFakeIpStore` / `IDnsHijacker` / `DnsHandlingResult` signatures are identical across every task that mentions them.
- [ ] **Path consistency**: every file path appears under `src/DotnetTun.*/` or `tests/DotnetTun.*Tests/` or `samples/DotnetTun.Demo.Cli/`.
- [ ] **TDD ordering**: every behavior-bearing task starts with a failing test, then implementation, then green.
- [ ] **Commit cadence**: every task ends with one `git commit`.

---

## Post-plan validation (manual, on macOS)

After Codex finishes Task 16, on a real macOS host run:

```bash
sudo dotnet run --project samples/DotnetTun.Demo.Cli/DotnetTun.Demo.Cli.csproj -- \
  tun --domain api.anthropic.com --fake-ip 198.18.0.7 --socks5 127.0.0.1:1080
```

Then in another terminal:

```bash
dig api.anthropic.com   # expect 198.18.0.7
curl https://api.anthropic.com/v1/models -H 'x-api-key: ...'   # expect to flow through SOCKS5
```

If both work, v0.1 is shipped.
