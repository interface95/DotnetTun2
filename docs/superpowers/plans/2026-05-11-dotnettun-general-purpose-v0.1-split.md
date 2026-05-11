# DotnetTun General-Purpose Library v0.1 Split Execution Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement each plan slice task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split `2026-05-10-dotnettun-general-purpose-v0.1.md` into smaller implementation slices that keep the repository buildable and testable after every slice.

**Architecture:** Preserve the original v0.1 direction, but separate foundation abstractions, TUN API breaking changes, packet-pipeline work, runtime composition, demo migration, and public API baselining. Each slice has a clear dependency boundary and a verification gate.

**Tech Stack:** .NET 10, C# 14, xUnit v3, Microsoft.Extensions.* 10.x, PublicApiAnalyzers after the final public surface stabilizes.

---

## Audit Summary

The original plan direction is sound, but it is too broad to execute as one continuous plan. It also contains several stale assumptions and steps that would intentionally leave the repository broken.

### Blocking corrections before execution

- Do not commit any slice that leaves `dotnet build DotnetTun.slnx` broken.
- Do not commit a `BuiltTransparentProxy` whose `StartAsync` or `StopAsync` still throws `NotImplementedException`.
- Keep the macOS utun AF-prefix regression tests explicit through the `ITunDevice` refactor:
  - IPv4 read strips `00 00 00 02`.
  - IPv6 read strips `00 00 00 1e`.
  - Unsupported AF read fails.
  - Short utun header read fails.
  - Native read failure preserves the native errno.
  - IPv4/IPv6 write prepends the big-endian AF header.
- Use Microsoft.Extensions package versions consistent with the repository's .NET 10 stack, not `9.0.0`.
- Update `DomainInterceptRule` before any code assumes `new(pattern, outboundName)` exists.
- Split original Task 9; do not mix UDP packet primitives, DNS sink, TCP outbound registry, and pipeline rename in one task.
- Run PublicApiAnalyzers only after the final v0.1 public surface is stable.

---

## Plan A: Routing + DNS Model Foundation

**Original tasks included:** 1, 2, 3, 4, 10

**Depends on:** none

**Goal:** Add the public routing/DNS extension abstractions and default implementations while old concrete types can still coexist until consumers are migrated.

**Scope:**

- Add `IRouter`, `ConnectionContext`, `RouteDecision`.
- Add or update `DomainInterceptRule` so it carries both `Pattern` and `OutboundName`.
- Add `DomainRuleRouter`.
- Add `IFakeIpStore` and `FakeIpStore` without deleting `FakeIpPool` yet.
- Add `IDnsHijacker`, `DnsHandlingResult`, and `RoutingDnsHijacker`.
- Add `UdpUpstreamDnsResolver` with timeout/cancellation tests.

**Must fix from original plan:**

- `RoutingDnsHijacker` uses `ILogger<T>`, so add `Microsoft.Extensions.Logging.Abstractions` 10.x in this slice or avoid logging until a later slice.
- DNS query builders used by tests must live in test support or be public/internal-visible; do not add inaccessible `internal` helpers and call them from another test assembly.
- Do not delete `FakeDnsResolver`, `FakeIpPool`, `DomainInterceptRouter`, or `InterceptDecision` until all consumers are migrated.

### Tasks

- [ ] **A1: Add routing abstractions and update `DomainInterceptRule`**
  - Files: `src/DotnetTun.Abstractions/Routing/*.cs`
  - Tests: new `DomainRuleRouterTests` compile after abstractions exist.
  - Gate: `dotnet build src/DotnetTun.Abstractions/DotnetTun.Abstractions.csproj`

- [ ] **A2: Add `DomainRuleRouter` while keeping `DomainInterceptRouter`**
  - Files: `src/DotnetTun.Core/Routing/DomainRuleRouter.cs`
  - Tests: exact match, wildcard suffix, no match, case-insensitive matching.
  - Gate: `dotnet test tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj --filter FullyQualifiedName~DomainRuleRouterTests`

- [ ] **A3: Add `IFakeIpStore` + `FakeIpStore` while keeping `FakeIpPool`**
  - Tests: allocation range, stable same-domain allocation, reverse lookup, unknown lookup, exhaustion.
  - Gate: `dotnet test tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj --filter FullyQualifiedName~FakeIpStoreTests`

- [ ] **A4: Add `IDnsHijacker` + `RoutingDnsHijacker`**
  - Tests: intercepted A response, intercepted AAAA NODATA, direct upstream forwarded, direct without upstream dropped, malformed dropped.
  - Gate: `dotnet test tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj --filter FullyQualifiedName~RoutingDnsHijackerTests`

- [ ] **A5: Add `UdpUpstreamDnsResolver`**
  - Tests: local UDP round trip, timeout returns null, cancellation throws or observes cancellation consistently.
  - Gate: `dotnet test tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj --filter FullyQualifiedName~UdpUpstreamDnsResolverTests`

- [ ] **A6: Full slice verification**
  - Run: `dotnet build DotnetTun.slnx && dotnet test DotnetTun.slnx --no-build`
  - Expected: build succeeds with 0 warnings/errors; all tests pass.

---

## Plan B: `ITunDevice` Public API Refactor

**Original tasks included:** 6, 7

**Depends on:** none; can run before or after Plan A

**Goal:** Make `ITunDevice` own its native handle internally so callers no longer pass `int fileDescriptor` through the public abstraction.

**Scope:**

- Replace `ITunDevice` with `OpenAsync`, `ReadAsync`, `WriteAsync`, `CloseAsync`, `IsOpen`, `InterfaceName`, `IAsyncDisposable`.
- Update `MacUtunDevice` to hold the fd internally.
- Preserve macOS utun AF-prefix behavior and tests.
- Update `TunPacketPump`, `RawTunProxy`, tests, and any sample call sites needed to keep the solution green.
- Hide or remove platform-private `*OpenResult` types after consumers no longer need them.

**Must fix from original plan:**

- Do not delete `TunPacketIoResult`/open/close result wrappers until every consumer in `src/`, `tests/`, and `samples/` is migrated in the same slice.
- Make partial native write behavior explicit: either throw on partial framed write or document/test the chosen behavior.

### Tasks

- [ ] **B1: Add failing tests for new `ITunDevice` lifecycle**
  - Tests cover open, read, write, close, double close, read/write before open.

- [ ] **B2: Refactor `MacUtunDevice` to own fd internally**
  - Preserve cancellation-close tracking.
  - Preserve AF-prefix read/write validation.

- [ ] **B3: Update packet pump/proxy call sites**
  - Remove explicit fd threading from `TunPacketPump` and `RawTunProxy`.

- [ ] **B4: Update macOS tests**
  - Existing AF-prefix tests must call `OpenAsync()` before `ReadAsync`/`WriteAsync`.

- [ ] **B5: Hide/delete obsolete public result wrappers**
  - Only after build proves no remaining consumer needs them.

- [ ] **B6: Full slice verification**
  - Run: `dotnet build DotnetTun.slnx && dotnet test DotnetTun.slnx --no-build`
  - Run macOS RID builds: `dotnet build src/DotnetTun.Platforms.MacOS/DotnetTun.Platforms.MacOS.csproj -r osx-arm64` and `-r osx-x64`.

---

## Plan C: Outbound Registry + TCP Routing

**Original tasks included:** 5 and the TCP/outbound-registry parts of 9

**Depends on:** Plan A

**Goal:** Route TCP sessions through named outbounds selected by `IRouter` instead of a single hard-coded outbound.

**Scope:**

- Add `Name` to `IOutbound` and `Socks5Outbound`.
- Update `Socks5OutboundOptions` without breaking existing timeout call sites.
- Update `OutboundTcpPayloadSink` to use `IFakeIpStore`, `IRouter`, and `IReadOnlyDictionary<string, IOutbound>`.
- Keep `RawTcpTunPipeline` name until Plan D unless a rename can be fully verified in the same slice.

### Tasks

- [ ] **C1: Add `IOutbound.Name` compatibly**
  - Prefer overloads or optional parameter order that does not break existing positional timeout calls.

- [ ] **C2: Update SOCKS5 outbound tests**
  - Cover default name and custom name.

- [ ] **C3: Refactor TCP payload sink to resolve named outbound**
  - Tests cover fake IP reverse lookup, router decision, outbound lookup, missing outbound failure path.

- [ ] **C4: Full slice verification**
  - Run: `dotnet build DotnetTun.slnx && dotnet test DotnetTun.slnx --no-build`

---

## Plan D: UDP/DNS TUN Pipeline

**Original tasks included:** UDP-specific parts of 9

**Depends on:** Plan A; Plan B optional; Plan C not required unless sharing a factory

**Goal:** Let UDP/53 packets arriving from the TUN device reach `IDnsHijacker` and return valid IPv4/UDP DNS responses.

**Scope:**

- Add UDP packet parser and builder.
- Add UDP checksum support.
- Add `IUdpSegmentHandler`, `UdpIpv4PacketHandler`, and `Dns53Sink`.
- Refactor `Ipv4TunPacketHandler` into protocol dispatch.
- Rename `RawTcpTunPipeline` only if all call sites/tests are migrated in this slice; otherwise defer rename to Plan E.

**Must fix from original plan:**

- Do not implement `UdpIpv4PacketHandler : ITcpSegmentHandler`; use a common IPv4 protocol handler interface or an explicit UDP handler contract.
- Add or use an actual IPv4 payload accessor; do not assume `Ipv4Packet.Payload` exists unless this slice adds it with tests.

### Tasks

- [ ] **D1: Add UDP parser tests and implementation**
  - Tests: short datagram rejected, length mismatch rejected, source/destination ports parsed, payload slice parsed.

- [ ] **D2: Add UDP builder/checksum tests and implementation**
  - Tests verify swapped source/destination addresses and ports plus checksum validity.

- [ ] **D3: Add IPv4 protocol dispatch tests**
  - Tests: TCP dispatch still works, UDP dispatch works when handler present, unsupported protocol drops.

- [ ] **D4: Add `Dns53Sink` tests and implementation**
  - Tests: UDP destination port 53 invokes hijacker, response packet swaps tuple, no response drops.

- [ ] **D5: Full slice verification**
  - Run: `dotnet test tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj`
  - Run: `dotnet build DotnetTun.slnx && dotnet test DotnetTun.slnx --no-build`

---

## Plan E: Runtime Composition + Hosting

**Original tasks included:** 11, 12, 13

**Depends on:** Plans A-D

**Goal:** Make `TransparentProxyBuilder.Build()` produce a real proxy whose `StartAsync` opens the TUN device and runs the packet pump, and expose it via `IHostedService`.

**Scope:**

- Redesign builder API.
- Wire `BuiltTransparentProxy.StartAsync`/`StopAsync` end-to-end.
- Add hosted service adapter and service collection wiring.
- Delete old dry-run engine only after replacement call sites compile.

**Must fix from original plan:**

- Do not commit a builder returning a proxy with `NotImplementedException`.
- Do not comment out demo code with `TODO(Task 14)` to force a build.
- Include existing hosting tests in this slice; they will break when `ITransparentProxy.Options` is removed.

### Tasks

- [ ] **E1: Add builder API tests first**
  - Tests: missing tun throws, missing outbound throws, default store/router/hijacker composition, custom dependencies used.

- [ ] **E2: Implement builder without breaking full build**
  - Keep old dry-run engine alive until all current call sites are migrated.

- [ ] **E3: Add runtime start/stop tests**
  - Tests: `StartAsync` opens tun, starts pump, `StopAsync` cancels pump and closes tun, double start rejected.

- [ ] **E4: Implement `BuiltTransparentProxy`**
  - No `NotImplementedException` allowed in committed state.

- [ ] **E5: Add hosted service tests and implementation**
  - Use `Microsoft.Extensions.Hosting.Abstractions` 10.x.

- [ ] **E6: Full slice verification**
  - Run: `dotnet build DotnetTun.slnx && dotnet test DotnetTun.slnx --no-build`

---

## Plan F: Demo CLI Migration

**Original tasks included:** 14

**Depends on:** Plan E

**Goal:** Move the demo CLI to the new builder/runtime model without losing macOS route/DNS cleanup behavior.

**Scope:**

- Update `tun` command to create and keep a local tun device variable, pass it to the builder, start the proxy, then read `tun.InterfaceName` for macOS configuration.
- Remove dry-run only after replacement tests are green.
- Update demo tests for new runtime seams.

**Must fix from original plan:**

- Do not inline `runtime.CreateTunDevice()` into the builder if later code needs `InterfaceName`; store it in a local variable.
- Preserve route exclude cleanup and DNS fallback behavior from earlier macOS hardening work.

### Tasks

- [ ] **F1: Add failing demo tests for new builder path**
- [ ] **F2: Migrate `tun` command using a retained tun device variable**
- [ ] **F3: Remove obsolete raw proxy/dry-run seams only when tests pass**
- [ ] **F4: Full slice verification**
  - Run: `dotnet test tests/DotnetTun.Demo.Cli.Tests/DotnetTun.Demo.Cli.Tests.csproj`
  - Run: `dotnet build DotnetTun.slnx && dotnet test DotnetTun.slnx --no-build`

---

## Plan G: Public API Baseline + Cleanup

**Original tasks included:** 15, 16

**Depends on:** Plans A-F

**Goal:** Lock the v0.1 public API after the intended surface is stable and remove retired-type residue.

**Scope:**

- Sweep retired references in `src/`, `tests/`, and `samples/`.
- Enable `Microsoft.CodeAnalysis.PublicApiAnalyzers` for every packable project.
- Capture baselines for Abstractions, Core, Hosting, Socks5, MacOS, Linux, and Windows if packable.

**Must fix from original plan:**

- Include Linux and Windows PublicAPI files if those projects are packable.
- Account for `TreatWarningsAsErrors`; RS0016 may fail as errors until baseline files exist.
- Do not baseline transient public types from unfinished earlier plans.

### Tasks

- [ ] **G1: Retired reference sweep**
  - Search terms: `FakeDnsResolver`, `DomainInterceptRouter`, `InterceptDecision`, `FakeIpPool`, `DotnetTunEngine`, `DotnetTunDryRunPlan`, `IProxyLogger`, `TunDeviceOpenResult`, `TunPacketIoResult`, `TunDeviceCloseResult`.

- [ ] **G2: Enable PublicApiAnalyzers with empty files first**
- [ ] **G3: Generate/capture public API baseline**
- [ ] **G4: Final verification**
  - Run: `dotnet build DotnetTun.slnx`
  - Expected: build succeeds with no `RS00xx` warnings/errors.
  - Run: `dotnet test DotnetTun.slnx --no-build`

---

## Recommended Execution Order

1. Plan A — Routing + DNS Model Foundation
2. Plan D — UDP/DNS TUN Pipeline
3. Plan C — Outbound Registry + TCP Routing
4. Plan B — `ITunDevice` Public API Refactor
5. Plan E — Runtime Composition + Hosting
6. Plan F — Demo CLI Migration
7. Plan G — Public API Baseline + Cleanup

Plan B can run before Plan A if desired, but it is a public API break and must preserve the macOS AF-prefix tests. Plan G must remain last.

## First Slice Recommendation

Start with **Plan A**. It adds the core extension interfaces and DNS primitives without touching TUN fd lifetime or the macOS utun AF-prefix behavior. Its output makes Plan D and Plan C cleaner and lowers the risk of mixing DNS model changes with packet-pipeline mechanics.
