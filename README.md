# DotnetTun2

DotnetTun2 is an experimental .NET 10 TUN-based transparent proxy toolkit. It provides core fake-IP DNS, routing, packet parsing/building, TCP/UDP session handling, SOCKS5 outbound connectivity, and macOS `utun` integration for local proxy experiments.

> Status: active development. The macOS path is the most complete platform path today; Linux and Windows projects currently contain early platform boundary stubs rather than full production TUN runtimes.

## What is included

- **Core proxy primitives** (`src/DotnetTun.Core`)
  - Fake-IP pool and DNS response handling
  - Domain routing rules and outbound selection
  - IPv4, TCP, and UDP packet parsing/building helpers
  - Raw TCP/UDP dispatch pipeline and session table
- **Abstractions** (`src/DotnetTun.Abstractions`)
  - TUN device and outbound interfaces
  - DNS/routing contracts
- **SOCKS5 outbound** (`src/DotnetTun.Outbounds.Socks5`)
  - TCP outbound connections via a SOCKS5 proxy
- **macOS platform support** (`src/DotnetTun.Platforms.MacOS`)
  - `utun` open/read/write/close boundary
  - 4-byte utun address-family framing
  - macOS route/ifconfig command construction and cleanup orchestration
  - Packaged `libutunshim.dylib` native assets for `osx-arm64` and `osx-x64`
- **Demo CLI** (`samples/DotnetTun.Demo.Cli`)
  - Dry-run command preview
  - Fake DNS server demo
  - Fake-IP TCP bridge demo
  - macOS raw TUN demo
- **Benchmarks** (`benchmarks/DotnetTun.Core.Benchmarks`)
  - BenchmarkDotNet microbenchmarks for core DNS, packet, and TCP pipeline paths

## Requirements

- .NET SDK capable of building `net10.0` / C# 14 projects
- macOS for the real `utun` demo path
- A local SOCKS5 proxy for outbound traffic, for example `127.0.0.1:7890`
- Elevated privileges for commands that actually configure TUN interfaces or routes on macOS

The normal build and test commands below do **not** require privileged TUN access.

## Build and test

```bash
dotnet restore DotnetTun.slnx
dotnet build DotnetTun.slnx
dotnet test DotnetTun.slnx --no-build
```

Current test projects:

- `tests/DotnetTun.Core.Tests`
- `tests/DotnetTun.Platforms.MacOS.Tests`
- `tests/DotnetTun.Hosting.Tests`
- `tests/DotnetTun.Demo.Cli.Tests`

## Demo CLI

Run the sample project with `dotnet run --project samples/DotnetTun.Demo.Cli -- ...`.

### Dry-run preview

Without a subcommand, the CLI prints a dry-run plan and macOS command preview. It does not execute the listed macOS commands.

```bash
dotnet run --project samples/DotnetTun.Demo.Cli -- \
  --domain api.anthropic.com \
  --domain '*.anthropic.com' \
  --fake-ip-cidr 198.18.0.0/15 \
  --socks5 127.0.0.1:7890
```

### Fake DNS demo

Starts a local fake DNS server for the configured domains.

```bash
dotnet run --project samples/DotnetTun.Demo.Cli -- dns \
  --listen 127.0.0.1:5353 \
  --domain api.anthropic.com \
  --domain '*.anthropic.com'
```

### Fake-IP TCP bridge demo

Maps one fake IP to one domain/port and forwards through SOCKS5.

```bash
dotnet run --project samples/DotnetTun.Demo.Cli -- bridge \
  --listen 127.0.0.1:18080 \
  --fake-ip 198.18.0.10 \
  --domain example.com \
  --target-port 443 \
  --socks5 127.0.0.1:7890
```

### macOS raw TUN demo

Use `--dry-run` first to inspect the plan without opening/configuring a real TUN device:

```bash
dotnet run --project samples/DotnetTun.Demo.Cli -- tun \
  --dry-run \
  --fake-ip 198.18.0.10 \
  --domain example.com \
  --socks5 127.0.0.1:7890
```

Running without `--dry-run` opens a macOS `utun` device and configures routes. Treat it as privileged local networking code; review the printed plan and understand the cleanup behavior before running it.

```bash
dotnet run --project samples/DotnetTun.Demo.Cli -- tun \
  --fake-ip 198.18.0.10 \
  --domain example.com \
  --socks5 127.0.0.1:7890 \
  --mtu 1500
```

## macOS notes

`DotnetTun.Platforms.MacOS` packages `libutunshim.dylib` for both Apple Silicon and Intel macOS runtimes. See [`src/DotnetTun.Platforms.MacOS/native/README.md`](src/DotnetTun.Platforms.MacOS/native/README.md) for native asset provenance, SHA-256 values, and review policy.

The macOS configurator builds structured `sudo ifconfig`, `sudo sysctl`, and `sudo route` commands. Tests use fake command runners and do not execute privileged commands.

## Benchmarks

Run BenchmarkDotNet benchmarks with:

```bash
dotnet run -c Release --project benchmarks/DotnetTun.Core.Benchmarks -- --filter '*'
```

Benchmark output is written under `BenchmarkDotNet.Artifacts/`, which is intentionally ignored by Git.

## Repository layout

```text
src/
  DotnetTun.Abstractions/        Shared contracts and options
  DotnetTun.Core/                DNS, routing, packet, and session pipeline code
  DotnetTun.Hosting/             Dependency-injection integration
  DotnetTun.Outbounds.Socks5/    SOCKS5 outbound implementation
  DotnetTun.Platforms.MacOS/     macOS utun and network configuration support
  DotnetTun.Platforms.Linux/     Early Linux platform boundary
  DotnetTun.Platforms.Windows/   Early Windows platform boundary
samples/
  DotnetTun.Demo.Cli/            Demo and dry-run command-line entry point
tests/                           Unit and integration-style tests with fakes
benchmarks/                      BenchmarkDotNet benchmark project
docs/superpowers/plans/          Implementation and audit plans
```

## Current limitations

- The macOS path is the primary implemented platform path.
- Linux and Windows projects are not full end-to-end TUN implementations yet.
- The raw TCP path is a pragmatic packet/session pipeline, not a complete general-purpose TCP stack.
- Real macOS TUN/route execution requires local privileges and should be exercised carefully.
- Native macOS binary updates require supply-chain review and README hash updates.

## Development notes

- Keep privileged operations behind testable seams; tests should use fake native APIs or fake command runners.
- Prefer non-privileged unit/integration tests for packet, DNS, routing, and macOS command behavior.
- For performance-sensitive paths, pair changes with allocation tests or BenchmarkDotNet coverage where practical.
