# DotnetTun macOS Domain Intercept Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the first DotnetTun MVP: a .NET package set and CLI demo that can model macOS transparent domain interception with fake-IP DNS and SOCKS5 outbound boundaries.

**Architecture:** Create a clean package split: abstractions define stable contracts, core implements testable domain/fake-IP/routing decisions, macOS package owns utun/route command boundaries, SOCKS5 package owns outbound dialing, demo composes the pieces. The first implementation prioritizes deterministic core behavior and safe dry-run platform operations before privileged live utun routing.

**Tech Stack:** .NET 10, C# 14, xUnit v3, central package management, macOS utun/PInvoke boundary, SOCKS5 outbound over `System.Net.Sockets`.

**Commit Policy:** Do not create git commits unless the user explicitly asks. Checkpoint steps mean “run verification and report status”.

---

## File Structure

- Create `DotnetTun.slnx`: solution containing source and test projects.
- Create `global.json`: pin .NET SDK 10.0.100 with feature roll-forward.
- Create `Directory.Build.props`: shared build settings, nullable enabled, implicit usings, warnings as errors.
- Create `Directory.Packages.props`: central package versions for xUnit v3 and test SDK packages.
- Create `src/DotnetTun.Abstractions/`: public contracts and option records.
- Create `src/DotnetTun.Core/`: fake-IP allocation, domain matcher, route decision, packet/session primitives.
- Create `src/DotnetTun.Platforms.MacOS/`: macOS platform command builder and utun API boundary.
- Create `src/DotnetTun.Outbounds.Socks5/`: SOCKS5 outbound options and connector shell.
- Create `samples/DotnetTun.Demo.Cli/`: CLI composition demo for Claude/Codex/Kiro domain intercept testing.
- Create `tests/DotnetTun.Core.Tests/`: unit tests for fake-IP and domain routing.
- Create `tests/DotnetTun.Platforms.MacOS.Tests/`: unit tests for macOS route command generation.

---

### Task 1: Solution Skeleton

**Files:**
- Create: `global.json`
- Create: `Directory.Build.props`
- Create: `Directory.Packages.props`
- Create: `DotnetTun.slnx`
- Create: `src/*/*.csproj`, `samples/DotnetTun.Demo.Cli/DotnetTun.Demo.Cli.csproj`, `tests/*/*.csproj`

- [ ] **Step 1: Create SDK/build/package files**

Use .NET 10 with nullable and central package management. No production behavior is added in this step.

- [ ] **Step 2: Create projects and references**

Projects:
```text
DotnetTun.Abstractions
DotnetTun.Core -> Abstractions
DotnetTun.Platforms.MacOS -> Abstractions
DotnetTun.Outbounds.Socks5 -> Abstractions
DotnetTun.Demo.Cli -> Core, Platforms.MacOS, Outbounds.Socks5
DotnetTun.Core.Tests -> Core
DotnetTun.Platforms.MacOS.Tests -> Platforms.MacOS
```

- [ ] **Step 3: Verify restore/build**

Run: `dotnet restore DotnetTun.slnx && dotnet build DotnetTun.slnx`

Expected: restore succeeds and build succeeds with zero warnings.

---

### Task 2: Fake-IP Store via TDD

**Files:**
- Create: `tests/DotnetTun.Core.Tests/FakeIp/FakeIpPoolTests.cs`
- Create: `src/DotnetTun.Abstractions/Dns/FakeIpLease.cs`
- Create: `src/DotnetTun.Core/Dns/FakeIpPool.cs`

- [ ] **Step 1: Write failing tests**

Test behaviors:
```text
Allocate same domain twice returns same fake IP
Allocate different domains returns different fake IPs
Resolve fake IP returns original domain
CIDR exhaustion throws InvalidOperationException
```

- [ ] **Step 2: Run red test**

Run: `dotnet test tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj --filter FakeIpPoolTests`

Expected: fails because `FakeIpPool` does not exist.

- [ ] **Step 3: Implement minimal fake-IP pool**

Use `198.18.0.0/15` default range. Store lower-case domain keys. Avoid `as any`-style suppression equivalents and avoid nullable warnings.

- [ ] **Step 4: Run green test**

Run: `dotnet test tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj --filter FakeIpPoolTests`

Expected: all fake-IP tests pass.

---

### Task 3: Domain Intercept Router via TDD

**Files:**
- Create: `tests/DotnetTun.Core.Tests/Routing/DomainInterceptRouterTests.cs`
- Create: `src/DotnetTun.Abstractions/Routing/DomainInterceptRule.cs`
- Create: `src/DotnetTun.Abstractions/Routing/InterceptDecision.cs`
- Create: `src/DotnetTun.Core/Routing/DomainInterceptRouter.cs`

- [ ] **Step 1: Write failing tests**

Test behaviors:
```text
Exact domain api.anthropic.com is intercepted
Wildcard *.anthropic.com intercepts console.anthropic.com
Non-matching github.com is direct
Matching is case-insensitive
```

- [ ] **Step 2: Run red test**

Run: `dotnet test tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj --filter DomainInterceptRouterTests`

Expected: fails because router types do not exist.

- [ ] **Step 3: Implement minimal router**

Return decisions containing domain, whether intercepted, and optional fake-IP allocation.

- [ ] **Step 4: Run green test**

Run: `dotnet test tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj --filter DomainInterceptRouterTests`

Expected: all routing tests pass.

---

### Task 4: macOS Route Command Builder via TDD

**Files:**
- Create: `tests/DotnetTun.Platforms.MacOS.Tests/MacRouteCommandBuilderTests.cs`
- Create: `src/DotnetTun.Platforms.MacOS/Networking/MacRouteCommandBuilder.cs`
- Create: `src/DotnetTun.Platforms.MacOS/Networking/MacTunOptions.cs`

- [ ] **Step 1: Write failing tests**

Test behaviors:
```text
BuildConfigureCommands includes ifconfig for utun name, address, gateway, mtu
BuildConfigureCommands includes route for fake-IP CIDR via utun
BuildExcludeCommands creates host routes for excluded proxy/DNS IPs through default gateway
```

- [ ] **Step 2: Run red test**

Run: `dotnet test tests/DotnetTun.Platforms.MacOS.Tests/DotnetTun.Platforms.MacOS.Tests.csproj --filter MacRouteCommandBuilderTests`

Expected: fails because command builder does not exist.

- [ ] **Step 3: Implement command builder only**

Do not execute privileged commands in tests. Generate command strings only.

- [ ] **Step 4: Run green test**

Run: `dotnet test tests/DotnetTun.Platforms.MacOS.Tests/DotnetTun.Platforms.MacOS.Tests.csproj --filter MacRouteCommandBuilderTests`

Expected: all macOS command generation tests pass.

---

### Task 5: Public Options and Demo Composition

**Files:**
- Create: `src/DotnetTun.Abstractions/DotnetTunOptions.cs`
- Create: `src/DotnetTun.Core/DotnetTunEngine.cs`
- Create: `samples/DotnetTun.Demo.Cli/Program.cs`

- [ ] **Step 1: Write failing tests for options validation**

Add core tests that invalid empty intercept domains are rejected, and a valid Claude-domain configuration is accepted.

- [ ] **Step 2: Implement minimal engine/options validation**

Engine should support dry-run start that computes fake-IP leases and platform commands without opening utun.

- [ ] **Step 3: Implement demo CLI dry-run**

Demo command prints target domains, fake-IP CIDR, generated macOS commands, and SOCKS5 outbound target.

- [ ] **Step 4: Verify demo**

Run: `dotnet run --project samples/DotnetTun.Demo.Cli -- --dry-run --domain api.anthropic.com --domain "*.anthropic.com" --socks5 127.0.0.1:7890`

Expected: prints dry-run plan without requiring sudo.

---

### Task 6: Verification

**Files:**
- All created files.

- [ ] **Step 1: Run full tests**

Run: `dotnet test DotnetTun.slnx`

Expected: all tests pass.

- [ ] **Step 2: Run full build**

Run: `dotnet build DotnetTun.slnx`

Expected: build succeeds with zero warnings.

- [ ] **Step 3: Inspect generated public API surface manually**

Check that public types are in `DotnetTun.*` namespaces and project/package names match NuGet prefix `DotnetTun`.

---

## Self-Review

- Spec coverage: covers package structure, macOS-first MVP, fake-IP domain interception, SOCKS5 outbound boundary, and dry-run demo.
- Placeholder scan: no TBD/TODO placeholders; MITM/Kestrel is intentionally excluded from MVP and reserved for a later package.
- Type consistency: all planned project names use `DotnetTun.*`; repository/project folder can remain `DotnetTun2`.
- Scope check: focused on testable MVP skeleton and domain interception core, not live privileged packet interception yet.
