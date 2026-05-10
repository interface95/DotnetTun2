# DotnetTun2 macOS Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Harden the macOS transparent-proxy path so real runs avoid proxy-server route loops, answer DNS misses correctly, and have a clear path toward production macOS networking compliance.

**Architecture:** Implement the immediate runtime fixes in small, testable seams: route exclusion stays in macOS command/configuration classes, DNS fallback stays behind a resolver abstraction, and production-only concerns stay documented until they are intentionally scheduled. P0 must remain non-privileged and testable with fakes; P1/P2 introduce platform/system behaviors behind interfaces before any real command execution.

**Tech Stack:** .NET 10, xUnit v3, macOS `utun`, structured `MacCommand`, UDP DNS codec, SystemConfiguration/networksetup research-backed design.

---

## Scope and Priority

### P0: Do now

1. Apply exclude routes for non-loopback outbound server IPs during real `tun` runs.
2. Clean up exclude routes on shutdown.
3. Add DNS upstream fallback for unmatched/direct queries.
4. Handle intercepted AAAA queries without letting IPv6 bypass the fake-IP path.

### P1: Next iteration

1. Detect current macOS primary network service via `scutil`/SystemConfiguration.
2. Save, apply, and restore DNS settings with `networksetup`.
3. Add sudo preflight: `sudo -n -v`, then interactive `sudo -v`, then deterministic `sudo -n ...` commands.
4. Listen for network changes and reapply DNS when Wi-Fi/VPN changes.

### P2: Production/compliance track

1. Replace checked-in opaque dylibs with repository-owned C source and Makefile.
2. Add signing/notarization policy and CI checks.
3. Evaluate NetworkExtension/SystemExtension backend as the commercial macOS path.

---

## File Map

### P0 route loop prevention

- Modify: `samples/DotnetTun.Demo.Cli/DotnetTunDemoCommand.cs`
  - Parse outbound host from `--socks5`.
  - Resolve it to IP addresses when real `tun` run starts.
  - Ignore loopback addresses.
  - Pass excluded server IPs into `MacTunOptions`.
- Modify: `src/DotnetTun.Platforms.MacOS/Networking/MacTunConfigurator.cs`
  - Execute exclude-route commands after base configure commands.
  - Execute exclude-route cleanup during cleanup.
- Modify: `src/DotnetTun.Platforms.MacOS/Networking/MacRouteCommandBuilder.cs`
  - Add cleanup command builder for excluded host routes.
- Tests:
  - `tests/DotnetTun.Platforms.MacOS.Tests/Networking/MacTunConfiguratorTests.cs`
  - `tests/DotnetTun.Platforms.MacOS.Tests/Networking/MacRouteCommandBuilderTests.cs`
  - `tests/DotnetTun.Demo.Cli.Tests/TunCommandTests.cs`

### P0 DNS fallback and AAAA behavior

- Create: `src/DotnetTun.Core/Dns/IUpstreamDnsResolver.cs`
  - `ValueTask<byte[]?> ResolveAsync(ReadOnlyMemory<byte> query, CancellationToken cancellationToken = default)`.
- Modify: `src/DotnetTun.Core/Dns/FakeDnsResolver.cs`
  - Keep current intercepted A behavior.
  - For direct/unmatched queries, call upstream resolver when configured.
  - For intercepted AAAA, return an explicit no-data response.
- Modify: `src/DotnetTun.Core/Dns/FakeDnsMessage.cs`
  - Add response helper for no-data `NOERROR` responses.
  - Keep `CreateAResponse` A-only.
- Modify: `src/DotnetTun.Core/Dns/FakeDnsServer.cs`
  - If resolver becomes async, await resolver before send.
- Tests:
  - `tests/DotnetTun.Core.Tests/Dns/FakeDnsResolverTests.cs`
  - `tests/DotnetTun.Core.Tests/Dns/FakeDnsMessageTests.cs`
  - `tests/DotnetTun.Core.Tests/Dns/FakeDnsServerTests.cs`

---

## Task 1: Apply and clean exclude routes in real macOS runs

- [ ] **Step 1: Add failing MacRouteCommandBuilder test for exclude cleanup**

Add a test that expects `route delete -host <ip>` commands with `IgnoreFailure: true`.

Run:

```bash
dotnet test "tests/DotnetTun.Platforms.MacOS.Tests/DotnetTun.Platforms.MacOS.Tests.csproj" --filter "FullyQualifiedName~MacRouteCommandBuilderTests"
```

Expected: FAIL because exclude cleanup builder does not exist.

- [ ] **Step 2: Implement exclude cleanup builder**

Add `BuildExcludeCleanupCommands(MacTunOptions options)` to `MacRouteCommandBuilder`.

- [ ] **Step 3: Add failing MacTunConfigurator test for exclude configure/cleanup execution**

Expected order:
1. base configure commands;
2. exclude host route commands;
3. cleanup includes exclude host route deletes.

Run the configurator tests and verify RED.

- [ ] **Step 4: Implement configurator exclude execution**

`ConfigureAsync` runs `BuildExcludeCommands(options, defaultGateway)` only when a default gateway is available. For P0, use a gateway provided through `MacTunOptions` or a minimal `DefaultGateway` property; P1 replaces this with dynamic discovery.

- [ ] **Step 5: Add failing demo test for remote SOCKS5 server exclude**

Test `tun --socks5 203.0.113.10:7890` real run and assert a `route add -host 203.0.113.10 ...` command happens. Test `127.0.0.1:7890` and assert no exclude command happens.

- [ ] **Step 6: Implement demo wiring**

Parse SOCKS5 host, resolve IP literal for P0, skip loopback, pass excluded IPs to `MacTunOptions`.

- [ ] **Step 7: Focused verification**

Run:

```bash
dotnet test "tests/DotnetTun.Platforms.MacOS.Tests/DotnetTun.Platforms.MacOS.Tests.csproj" --filter "FullyQualifiedName~MacRouteCommandBuilderTests|FullyQualifiedName~MacTunConfiguratorTests"
dotnet test "tests/DotnetTun.Demo.Cli.Tests/DotnetTun.Demo.Cli.Tests.csproj" --filter "FullyQualifiedName~TunCommandTests"
```

Expected: PASS.

---

## Task 2: Add DNS upstream fallback and intercepted AAAA no-data response

- [ ] **Step 1: Add failing FakeDnsMessage no-data test**

Create an AAAA question and assert the response has:
- same transaction ID;
- QR response flag;
- RCODE 0 (`NOERROR`);
- question copied;
- answer count 0.

Run:

```bash
dotnet test "tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj" --filter "FullyQualifiedName~FakeDnsMessageTests"
```

Expected: FAIL because no no-data response helper exists.

- [ ] **Step 2: Implement no-data DNS response helper**

Add `CreateNoDataResponse(DnsQuestion question)` to `FakeDnsMessage`.

- [ ] **Step 3: Add failing resolver test for intercepted AAAA**

For an intercepted domain AAAA query, assert resolver returns a response with `NOERROR` and zero answers.

- [ ] **Step 4: Implement intercepted AAAA branch**

In `FakeDnsResolver`, parse the question first; if router intercepts and record type is AAAA, return no-data.

- [ ] **Step 5: Add failing upstream fallback test**

For unmatched A query, configure a fake upstream resolver that returns a known byte response. Assert resolver returns that upstream response.

- [ ] **Step 6: Implement `IUpstreamDnsResolver` and fallback wiring**

Add optional upstream dependency to `FakeDnsResolver`; direct/unmatched queries call it. Keep existing constructor behavior by defaulting to no upstream, preserving old tests unless intentionally updated.

- [ ] **Step 7: Focused DNS verification**

Run:

```bash
dotnet test "tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj" --filter "FullyQualifiedName~FakeDnsResolverTests|FullyQualifiedName~FakeDnsMessageTests|FullyQualifiedName~FakeDnsServerTests"
```

Expected: PASS.

---

## Task 3: P0 full verification

- [ ] **Step 1: LSP diagnostics**

Run diagnostics on modified route/DNS files.

- [ ] **Step 2: Full gate**

Run:

```bash
dotnet build "DotnetTun.slnx"
dotnet test "DotnetTun.slnx" --no-build
dotnet build "src/DotnetTun.Platforms.MacOS/DotnetTun.Platforms.MacOS.csproj" -c Debug -r osx-arm64 --no-self-contained
dotnet build "src/DotnetTun.Platforms.MacOS/DotnetTun.Platforms.MacOS.csproj" -c Debug -r osx-x64 --no-self-contained
dotnet pack "src/DotnetTun.Platforms.MacOS/DotnetTun.Platforms.MacOS.csproj" -c Debug --no-build
```

Expected: all commands exit 0. NuGet readme warning is known and non-blocking.

---

## P1/P2 Follow-up Design Notes

- `networksetup -listallnetworkservices` is not enough for active service detection. Use `scutil`/SystemConfiguration `State:/Network/Global/IPv4` and `PrimaryService`.
- Save original DNS/search domains and restore only when current DNS still matches DotnetTun2-applied state.
- Use `sudo -n -v` then interactive `sudo -v`; follow-up commands should be deterministic with `sudo -n`.
- Network changes should be treated as triggers to re-read full SystemConfiguration state.
- Native dylib work should add C source + Makefile + signing policy before claiming audit-grade provenance.

---

## Self-Review

- P0 route-loop requirement is covered by Task 1.
- P0 DNS upstream and AAAA requirement is covered by Task 2.
- P1/P2 are deliberately deferred and documented as follow-up; they are not required for P0 verification.
- No privileged macOS commands are required by this plan.
- All implementation work is test-first.
