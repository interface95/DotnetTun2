# DotnetTun Audit Blocker Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the full-review blockers in the DotnetTun transparent proxy milestone so the branch is safe to review, package, and continue toward real macOS TUN validation.

**Architecture:** Keep the current milestone boundaries: no MITM, no privileged commands in tests, and small testable seams around native/platform effects. Harden the raw packet pipeline by rejecting unsupported input before state mutation, make TUN shutdown reliable by unblocking native reads on cancellation, replace shell-string execution with argv-based commands, document native dylib provenance, and add explicit resource deadlines/limits.

**Tech Stack:** .NET 10, C# primary constructors/records, xUnit v3, macOS utun native shim, NuGet native assets, fake test doubles for privileged/system effects.

---

## File Structure

- Modify `src/DotnetTun.Platforms.MacOS/Networking/MacUtunDevice.cs` — make blocking native reads cancellable by closing the fd on cancellation and normalizing cancellation results.
- Modify `tests/DotnetTun.Platforms.MacOS.Tests/Networking/MacUtunDeviceTests.cs` — add cancellation/unblock tests using a controllable fake native API.
- Modify `src/DotnetTun.Core/Sessions/RawTcpSessionHandler.cs` — reject duplicate, skipped, or out-of-order payload before forwarding to `ITcpPayloadSink`.
- Modify `src/DotnetTun.Core/Sessions/TcpSessionTable.cs` and `src/DotnetTun.Core/Sessions/TcpSession.cs` only if the existing table lacks an exact expected-sequence helper.
- Modify `tests/DotnetTun.Core.Tests/Sessions/RawTcpSessionHandlerTests.cs` — add duplicate/out-of-order/sequence-gap regression tests.
- Modify `src/DotnetTun.Core/Packets/Ipv4Packet.cs` — expose fragment/checksum validity and reject unsupported fragmented IPv4 packets.
- Modify `src/DotnetTun.Core/Packets/TcpSegment.cs` and/or `src/DotnetTun.Core/Sessions/TcpIpv4PacketHandler.cs` — validate TCP checksum before TCP session handling.
- Modify `tests/DotnetTun.Core.Tests/Packets/Ipv4PacketTests.cs`, `tests/DotnetTun.Core.Tests/Packets/TcpSegmentTests.cs`, and `tests/DotnetTun.Core.Tests/Sessions/TcpIpv4PacketHandlerTests.cs` — cover invalid checksums/fragments.
- Replace `src/DotnetTun.Platforms.MacOS/Networking/MacShellCommandRunner.cs` with argv-based process execution; keep the class name if helpful, but change the contract if needed.
- Modify `src/DotnetTun.Platforms.MacOS/Networking/IMacCommandRunner.cs`, `MacRouteCommandBuilder.cs`, `MacTunConfigurator.cs`, and related tests to avoid `/bin/zsh -lc`.
- Create `src/DotnetTun.Platforms.MacOS/native/README.md` — record dylib source, build command, hashes, and review policy.
- Create or modify `tests/DotnetTun.Platforms.MacOS.Tests/Native/MacNativeAssetPackagingTests.cs` — assert README provenance and SHA-256 hashes for the checked-in dylibs.
- Modify `src/DotnetTun.Outbounds.Socks5/Socks5OutboundOptions.cs` if present, otherwise create it, to include handshake timeout.
- Modify `src/DotnetTun.Outbounds.Socks5/Socks5Outbound.cs` — enforce timeout around connect/greeting/connect-response handshake.
- Modify `tests/DotnetTun.Core.Tests/Outbounds/Socks5OutboundTests.cs` — add stalled SOCKS5 handshake timeout tests.
- Modify `src/DotnetTun.Core/Sessions/TcpSessionTable.cs`, `FakeIpTcpBridgeServer.cs`, and tests — add hard session/connection limits where still unbounded.

---

### Task 1: Make macOS TUN reads cancellable and cleanup-safe

**Files:**
- Modify: `src/DotnetTun.Platforms.MacOS/Networking/MacUtunDevice.cs`
- Modify: `tests/DotnetTun.Platforms.MacOS.Tests/Networking/MacUtunDeviceTests.cs`

- [ ] **Step 1: Write failing cancellation test**

Add a fake native API in `MacUtunDeviceTests.cs` that blocks `ReadPacket` until the fd is closed. Add this test:

```csharp
[Fact]
public async Task ReadPacketAsync_WhenCancelled_ClosesFileDescriptorToUnblockRead()
{
    // Arrange
    var nativeApi = new BlockingReadUtunNativeApi();
    var device = new MacUtunDevice(nativeApi);
    using var cancellation = new CancellationTokenSource();
    byte[] buffer = new byte[1500];

    // Act
    ValueTask<TunPacketIoResult> readTask = device.ReadPacketAsync(42, buffer, cancellation.Token);
    await cancellation.CancelAsync();
    TunPacketIoResult result = await readTask;

    // Assert
    Assert.False(result.Success);
    Assert.True(nativeApi.ClosedFileDescriptors.Contains(42));
}
```

The fake must implement `IUtunNativeApi.ReadPacket` by waiting until `Close(42, out _)` is called, then returning `-1` with `EINTR` or another deterministic fake errno.

- [ ] **Step 2: Run test to verify RED**

Run:

```bash
dotnet test "tests/DotnetTun.Platforms.MacOS.Tests/DotnetTun.Platforms.MacOS.Tests.csproj" --filter "FullyQualifiedName~MacUtunDeviceTests.ReadPacketAsync_WhenCancelled_ClosesFileDescriptorToUnblockRead"
```

Expected: FAIL because `MacUtunDevice.ReadPacketAsync` currently calls `_nativeApi.ReadPacket` synchronously and does not close the fd on cancellation.

- [ ] **Step 3: Implement minimal cancellation bridge**

Change `ReadPacketAsync` to execute the blocking native read on the thread pool and register cancellation to close the fd:

```csharp
public async ValueTask<TunPacketIoResult> ReadPacketAsync(int fileDescriptor, Memory<byte> buffer, CancellationToken cancellationToken = default)
{
    const int OperationCanceledErrorNumber = 89;
    cancellationToken.ThrowIfCancellationRequested();

    using CancellationTokenRegistration registration = cancellationToken.Register(static state =>
    {
        var payload = ((IUtunNativeApi NativeApi, int FileDescriptor))state!;
        _ = payload.NativeApi.Close(payload.FileDescriptor, out _);
    }, (_nativeApi, fileDescriptor));

    if (!System.Runtime.InteropServices.MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment)
        || segment.Array is null)
    {
        throw new ArgumentException("macOS utun reads require array-backed memory.", nameof(buffer));
    }

    int bytesTransferred;
    int errorNumber = 0;
    try
    {
        bytesTransferred = await Task.Run(() =>
        {
            Span<byte> span = segment.Array.AsSpan(segment.Offset, segment.Count);
            return _nativeApi.ReadPacket(fileDescriptor, span, out errorNumber);
        }, CancellationToken.None).ConfigureAwait(false);

        return bytesTransferred < 0
            ? TunPacketIoResult.Failed(errorNumber)
            : TunPacketIoResult.Transferred(bytesTransferred);
    }
    catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
    {
        return TunPacketIoResult.Failed(OperationCanceledErrorNumber);
    }
}
```

If `Memory<byte>.Span` cannot be captured inside `Task.Run`, copy through `MemoryMarshal.TryGetArray` for arrays and add an implementation comment explaining the limitation. Do not introduce `async void`, empty catches, or fire-and-forget tasks.

- [ ] **Step 4: Run macOS device tests**

Run:

```bash
dotnet test "tests/DotnetTun.Platforms.MacOS.Tests/DotnetTun.Platforms.MacOS.Tests.csproj" --filter "FullyQualifiedName~MacUtunDeviceTests"
```

Expected: PASS.

- [ ] **Step 5: Commit this task when green**

Do not commit unless the user explicitly asks. If committing later, group `MacUtunDevice.cs` with `MacUtunDeviceTests.cs`.

---

### Task 2: Enforce TCP payload sequence correctness

**Files:**
- Modify: `src/DotnetTun.Core/Sessions/RawTcpSessionHandler.cs`
- Modify: `src/DotnetTun.Core/Sessions/TcpSessionTable.cs` if needed
- Modify: `tests/DotnetTun.Core.Tests/Sessions/RawTcpSessionHandlerTests.cs`

- [ ] **Step 1: Write duplicate payload test**

Add a test that establishes a session, sends payload at the expected sequence once, then sends the same payload again at the old sequence:

```csharp
[Fact]
public async Task HandleAsync_WhenPayloadSequenceIsDuplicate_DoesNotForwardOrAckAgain()
{
    // Arrange
    var sink = new RecordingTcpPayloadSink();
    var handler = new RawTcpSessionHandler(new TcpSessionTable(), serverInitialSequence: 9000, sink);
    Ipv4Packet packet = PacketFixtures.CreateTcpIpv4Packet("10.0.0.2", "198.18.0.1", 50000, 80, sequenceNumber: 1000, acknowledgmentNumber: 0, TcpFlags.Syn);
    TcpSegment syn = TcpSegment.Parse(packet.Payload);
    await handler.HandleAsync(packet, syn);
    Ipv4Packet ackPacket = PacketFixtures.CreateTcpIpv4Packet("10.0.0.2", "198.18.0.1", 50000, 80, sequenceNumber: 1001, acknowledgmentNumber: 9001, TcpFlags.Ack);
    await handler.HandleAsync(ackPacket, TcpSegment.Parse(ackPacket.Payload));
    Ipv4Packet firstPayload = PacketFixtures.CreateTcpIpv4Packet("10.0.0.2", "198.18.0.1", 50000, 80, sequenceNumber: 1001, acknowledgmentNumber: 9001, TcpFlags.Psh | TcpFlags.Ack, [1, 2, 3]);
    await handler.HandleAsync(firstPayload, TcpSegment.Parse(firstPayload.Payload));

    // Act
    IReadOnlyList<ReadOnlyMemory<byte>> responses = await handler.HandleAsync(firstPayload, TcpSegment.Parse(firstPayload.Payload));

    // Assert
    Assert.Empty(responses);
    Assert.Single(sink.Writes);
}
```

- [ ] **Step 2: Write sequence gap test**

Add:

```csharp
[Fact]
public async Task HandleAsync_WhenPayloadSequenceSkipsExpectedByte_DoesNotForwardPayload()
{
    // Arrange
    var sink = new RecordingTcpPayloadSink();
    var handler = CreateEstablishedHandler(sink, clientSequence: 1001, serverSequence: 9001);
    Ipv4Packet skippedPayload = PacketFixtures.CreateTcpIpv4Packet("10.0.0.2", "198.18.0.1", 50000, 80, sequenceNumber: 1005, acknowledgmentNumber: 9001, TcpFlags.Psh | TcpFlags.Ack, [1, 2, 3]);

    // Act
    IReadOnlyList<ReadOnlyMemory<byte>> responses = await handler.HandleAsync(skippedPayload, TcpSegment.Parse(skippedPayload.Payload));

    // Assert
    Assert.Empty(responses);
    Assert.Empty(sink.Writes);
}
```

Use existing helpers if present; otherwise add `CreateEstablishedHandler` locally in the test file.

- [ ] **Step 3: Run sequence tests to verify RED**

Run:

```bash
dotnet test "tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj" --filter "FullyQualifiedName~RawTcpSessionHandlerTests"
```

Expected: new duplicate/gap tests FAIL because current code accepts any ACK payload sequence and advances from the incoming segment number.

- [ ] **Step 4: Implement exact expected sequence gate**

Before calling `TryAdvanceClientSequence`, require:

```csharp
if (!_sessions.TryGet(key, out TcpSession? currentSession) || currentSession is null)
{
    return [];
}

if (segment.SequenceNumber != currentSession.NextClientSequence)
{
    return [];
}

uint nextClientSequence = segment.SequenceNumber + (uint)segment.Payload.Length;
```

If `TcpSessionTable` lacks `TryGet`, add:

```csharp
public bool TryGet(TcpFlowKey key, out TcpSession? session)
    => _sessions.TryGetValue(key, out session);
```

- [ ] **Step 5: Run raw TCP tests**

Run:

```bash
dotnet test "tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj" --filter "FullyQualifiedName~RawTcpSessionHandlerTests|FullyQualifiedName~TcpIpv4PacketHandlerTests|FullyQualifiedName~RawTcpTunPipelineTests"
```

Expected: PASS.

---

### Task 3: Reject fragmented and checksum-invalid inbound packets

**Files:**
- Modify: `src/DotnetTun.Core/Packets/Ipv4Packet.cs`
- Modify: `src/DotnetTun.Core/Packets/TcpSegment.cs`
- Modify: `src/DotnetTun.Core/Sessions/TcpIpv4PacketHandler.cs`
- Modify: `tests/DotnetTun.Core.Tests/Packets/Ipv4PacketTests.cs`
- Modify: `tests/DotnetTun.Core.Tests/Sessions/TcpIpv4PacketHandlerTests.cs`

- [ ] **Step 1: Write IPv4 fragment rejection test**

Add:

```csharp
[Theory]
[InlineData(0x2000)] // More Fragments
[InlineData(0x0001)] // Non-zero fragment offset
public void TryParse_WhenPacketIsFragmented_ReturnsFalse(ushort flagsAndFragmentOffset)
{
    // Arrange
    byte[] packet = PacketFixtures.CreateIpv4Packet(protocol: 6, payload: [0x00], flagsAndFragmentOffset: flagsAndFragmentOffset);

    // Act
    bool parsed = Ipv4Packet.TryParse(packet, out _);

    // Assert
    Assert.False(parsed);
}
```

- [ ] **Step 2: Write IPv4 checksum rejection test**

Add:

```csharp
[Fact]
public void TryParse_WhenHeaderChecksumIsInvalid_ReturnsFalse()
{
    // Arrange
    byte[] packet = PacketFixtures.CreateIpv4Packet(protocol: 6, payload: [0x00]);
    packet[10] ^= 0xFF;

    // Act
    bool parsed = Ipv4Packet.TryParse(packet, out _);

    // Assert
    Assert.False(parsed);
}
```

- [ ] **Step 3: Write TCP checksum rejection test at handler boundary**

Add to `TcpIpv4PacketHandlerTests.cs`:

```csharp
[Fact]
public async Task HandleAsync_WhenTcpChecksumIsInvalid_DoesNotInvokeSegmentHandler()
{
    // Arrange
    var segmentHandler = new RecordingTcpSegmentHandler();
    var handler = new TcpIpv4PacketHandler(segmentHandler);
    byte[] packet = PacketFixtures.CreateTcpIpv4PacketBytes("10.0.0.2", "198.18.0.1", 50000, 80, sequenceNumber: 1, acknowledgmentNumber: 0, TcpFlags.Syn);
    packet[^1] ^= 0xFF;
    Assert.True(Ipv4Packet.TryParse(packet, out Ipv4Packet ipv4Packet));

    // Act
    IReadOnlyList<ReadOnlyMemory<byte>> responses = await handler.HandleAsync(ipv4Packet);

    // Assert
    Assert.Empty(responses);
    Assert.Empty(segmentHandler.HandledSegments);
}
```

- [ ] **Step 4: Run packet tests to verify RED**

Run:

```bash
dotnet test "tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj" --filter "FullyQualifiedName~Ipv4PacketTests|FullyQualifiedName~TcpIpv4PacketHandlerTests"
```

Expected: new tests FAIL.

- [ ] **Step 5: Implement IPv4 fragment/checksum validation**

In `Ipv4Packet.TryParse`, after `totalLength` validation:

```csharp
ushort flagsAndFragmentOffset = BinaryPrimitives.ReadUInt16BigEndian(span[6..8]);
bool hasMoreFragments = (flagsAndFragmentOffset & 0x2000) != 0;
bool hasFragmentOffset = (flagsAndFragmentOffset & 0x1FFF) != 0;
if (hasMoreFragments || hasFragmentOffset)
{
    ipv4Packet = default;
    return false;
}

if (InternetChecksum.Compute(span[..headerLength]) != 0)
{
    ipv4Packet = default;
    return false;
}
```

- [ ] **Step 6: Implement TCP checksum gate**

Add a helper on `TcpSegment` or `TcpChecksum`:

```csharp
public static bool IsValid(ReadOnlySpan<byte> tcpSegment, IPAddress sourceAddress, IPAddress destinationAddress)
    => Compute(sourceAddress, destinationAddress, tcpSegment) == 0;
```

Then in `TcpIpv4PacketHandler.HandleAsync` before parsing/dispatch:

```csharp
ReadOnlyMemory<byte> tcpBytes = packet.RawPacket[packet.HeaderLength..packet.TotalLength];
if (!TcpChecksum.IsValid(tcpBytes.Span, packet.SourceAddress, packet.DestinationAddress))
{
    return [];
}
```

- [ ] **Step 7: Run packet/session tests**

Run:

```bash
dotnet test "tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj" --filter "FullyQualifiedName~Packets|FullyQualifiedName~TcpIpv4PacketHandlerTests|FullyQualifiedName~RawTcp"
```

Expected: PASS.

---

### Task 4: Replace macOS shell-string execution with argv commands

**Files:**
- Modify: `src/DotnetTun.Platforms.MacOS/Networking/IMacCommandRunner.cs`
- Modify: `src/DotnetTun.Platforms.MacOS/Networking/MacShellCommandRunner.cs`
- Modify: `src/DotnetTun.Platforms.MacOS/Networking/MacRouteCommandBuilder.cs`
- Modify: `src/DotnetTun.Platforms.MacOS/Networking/MacTunConfigurator.cs`
- Modify: `tests/DotnetTun.Platforms.MacOS.Tests/Networking/MacTunConfiguratorTests.cs`
- Modify: `tests/DotnetTun.Platforms.MacOS.Tests/Networking/MacRouteCommandBuilderTests.cs`

- [ ] **Step 1: Write command runner contract test**

Add a unit test for a recording runner that verifies commands are structured as executable + args, not shell strings:

```csharp
[Fact]
public async Task ConfigureAsync_UsesStructuredCommandsWithoutShellOperators()
{
    // Arrange
    var runner = new RecordingMacCommandRunner();
    var configurator = new MacTunConfigurator(runner);
    var options = new MacTunOptions("utun7", "198.18.0.0/16", "10.0.0.1");

    // Act
    await configurator.ConfigureAsync(options);

    // Assert
    Assert.All(runner.Commands, command =>
    {
        Assert.DoesNotContain("||", command.Executable, StringComparison.Ordinal);
        Assert.DoesNotContain("2>", command.Executable, StringComparison.Ordinal);
        Assert.DoesNotContain(";", command.Executable, StringComparison.Ordinal);
        Assert.DoesNotContain("-lc", command.Arguments);
    });
}
```

- [ ] **Step 2: Run macOS configurator tests to verify RED**

Run:

```bash
dotnet test "tests/DotnetTun.Platforms.MacOS.Tests/DotnetTun.Platforms.MacOS.Tests.csproj" --filter "FullyQualifiedName~MacTunConfiguratorTests|FullyQualifiedName~MacRouteCommandBuilderTests"
```

Expected: FAIL because current command API is `RunAsync(string command)`.

- [ ] **Step 3: Introduce structured command type**

Create in `MacCommand.cs` or inside existing networking folder:

```csharp
namespace DotnetTun.Platforms.MacOS.Networking;

public sealed record MacCommand(string Executable, IReadOnlyList<string> Arguments)
{
    public override string ToString() => $"{Executable} {string.Join(' ', Arguments)}";
}
```

Change runner interface:

```csharp
public interface IMacCommandRunner
{
    ValueTask RunAsync(MacCommand command, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 4: Implement argv-based runner**

Update `MacShellCommandRunner`:

```csharp
public async ValueTask RunAsync(MacCommand command, CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(command.Executable))
    {
        throw new ArgumentException("Command executable must not be empty.", nameof(command));
    }

    var startInfo = new ProcessStartInfo(command.Executable)
    {
        RedirectStandardError = true,
        RedirectStandardOutput = true,
    };

    foreach (string argument in command.Arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start macOS command process.");
    string standardError = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"macOS command failed with exit code {process.ExitCode}: {command}\n{standardError}");
    }
}
```

- [ ] **Step 5: Remove shell cleanup operators**

Represent cleanup commands as structured commands plus `IgnoreFailure` if needed:

```csharp
public sealed record MacCommand(string Executable, IReadOnlyList<string> Arguments, bool IgnoreFailure = false);
```

In runner:

```csharp
if (process.ExitCode != 0 && !command.IgnoreFailure)
{
    throw new InvalidOperationException(...);
}
```

Build cleanup commands as `/sbin/route delete ...` with `IgnoreFailure = true` instead of appending `2>/dev/null || true`.

- [ ] **Step 6: Run macOS tests**

Run:

```bash
dotnet test "tests/DotnetTun.Platforms.MacOS.Tests/DotnetTun.Platforms.MacOS.Tests.csproj"
```

Expected: PASS.

---

### Task 5: Add native dylib provenance and hash guard

**Files:**
- Create: `src/DotnetTun.Platforms.MacOS/native/README.md`
- Modify: `tests/DotnetTun.Platforms.MacOS.Tests/Native/MacNativeAssetPackagingTests.cs`

- [ ] **Step 1: Write provenance test**

Add:

```csharp
[Fact]
public void NativeAssetReadme_DocumentsSourceBuildAndHashes()
{
    // Arrange
    string readmePath = Path.Combine(FindRepositoryRoot(), "src", "DotnetTun.Platforms.MacOS", "native", "README.md");

    // Act
    string readme = File.ReadAllText(readmePath);

    // Assert
    Assert.Contains("Source", readme, StringComparison.Ordinal);
    Assert.Contains("Build", readme, StringComparison.Ordinal);
    Assert.Contains("SHA-256", readme, StringComparison.Ordinal);
    Assert.Contains("libutunshim.dylib", readme, StringComparison.Ordinal);
}
```

Add hash checks:

```csharp
[Theory]
[InlineData("native/osx-arm64/libutunshim.dylib", "862aa238a467d7d808eda6f4265f3452d87665893c013e5a1bce56f15d16deee")]
[InlineData("native/osx-x64/libutunshim.dylib", "862aa238a467d7d808eda6f4265f3452d87665893c013e5a1bce56f15d16deee")]
public void NativeSourceAsset_MatchesDocumentedSha256(string relativeAssetPath, string expectedSha256)
{
    string assetPath = Path.Combine(FindRepositoryRoot(), "src", "DotnetTun.Platforms.MacOS", relativeAssetPath);
    using FileStream stream = File.OpenRead(assetPath);
    byte[] hash = System.Security.Cryptography.SHA256.HashData(stream);
    string actualSha256 = Convert.ToHexString(hash).ToLowerInvariant();
    Assert.Equal(expectedSha256, actualSha256);
}
```

- [ ] **Step 2: Run native tests to verify RED**

Run:

```bash
dotnet test "tests/DotnetTun.Platforms.MacOS.Tests/DotnetTun.Platforms.MacOS.Tests.csproj" --filter "FullyQualifiedName~MacNativeAssetPackagingTests"
```

Expected: FAIL because README and hash values are not present yet.

- [ ] **Step 3: Compute hashes and create README**

Run:

```bash
shasum -a 256 "src/DotnetTun.Platforms.MacOS/native/osx-arm64/libutunshim.dylib" "src/DotnetTun.Platforms.MacOS/native/osx-x64/libutunshim.dylib"
```

Create `src/DotnetTun.Platforms.MacOS/native/README.md`:

```markdown
# macOS native utun shim

## Source

`libutunshim.dylib` exposes the native `open_utun` entry point used by `MacUtunNativeApi`.

Current source/provenance: copied from the local upstream reference at `linker/src/linker/libutunshim-osx-*.dylib` during the DotnetTun native packaging migration.

## Build

Until native source is migrated into this repository, rebuilds must be produced from the upstream linker native shim source and copied into:

- `native/osx-arm64/libutunshim.dylib`
- `native/osx-x64/libutunshim.dylib`

## SHA-256

- `native/osx-arm64/libutunshim.dylib`: `862aa238a467d7d808eda6f4265f3452d87665893c013e5a1bce56f15d16deee`
- `native/osx-x64/libutunshim.dylib`: `862aa238a467d7d808eda6f4265f3452d87665893c013e5a1bce56f15d16deee`

## Review policy

Any dylib replacement must update this README, the packaging hash tests, and pass `dotnet pack` native asset inspection.
```

- [ ] **Step 4: Confirm hash tests use the documented hashes**

Verify the `[InlineData]` attributes use `862aa238a467d7d808eda6f4265f3452d87665893c013e5a1bce56f15d16deee` for both current dylibs. If either binary changes, recompute the hash and update both the test and README in the same task.

- [ ] **Step 5: Run native packaging tests and pack inspection**

Run:

```bash
dotnet test "tests/DotnetTun.Platforms.MacOS.Tests/DotnetTun.Platforms.MacOS.Tests.csproj" --filter "FullyQualifiedName~MacNativeAssetPackagingTests"
dotnet pack "src/DotnetTun.Platforms.MacOS/DotnetTun.Platforms.MacOS.csproj" -c Debug --no-build
```

Expected: tests PASS; pack succeeds and native entries remain unchanged.

---

### Task 6: Add resource limits and SOCKS5 handshake deadlines

**Files:**
- Modify or create: `src/DotnetTun.Outbounds.Socks5/Socks5OutboundOptions.cs`
- Modify: `src/DotnetTun.Outbounds.Socks5/Socks5Outbound.cs`
- Modify: `tests/DotnetTun.Core.Tests/Outbounds/Socks5OutboundTests.cs`
- Modify: `src/DotnetTun.Core/Sessions/TcpSessionTable.cs`
- Modify: `src/DotnetTun.Core/Sessions/FakeIpTcpBridgeServer.cs`
- Modify: `tests/DotnetTun.Core.Tests/Sessions/OutboundTcpPayloadSinkTests.cs`
- Modify: `tests/DotnetTun.Core.Tests/Sessions/FakeIpTcpBridgeTests.cs`

- [ ] **Step 1: Write SOCKS5 stalled handshake timeout test**

Add to `Socks5OutboundTests.cs` using a fake TCP listener that accepts but never writes greeting response:

```csharp
[Fact]
public async Task ConnectAsync_WhenSocksServerStallsGreeting_ThrowsTimeout()
{
    // Arrange
    using var server = await StalledTcpServer.StartAsync();
    var outbound = new Socks5Outbound(new Socks5OutboundOptions("127.0.0.1", server.Port, HandshakeTimeout: TimeSpan.FromMilliseconds(100)));

    // Act / Assert
    await Assert.ThrowsAsync<TimeoutException>(() => outbound.ConnectAsync("example.com", 443).AsTask());
}
```

- [ ] **Step 2: Run SOCKS5 timeout test to verify RED**

Run:

```bash
dotnet test "tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj" --filter "FullyQualifiedName~Socks5OutboundTests.ConnectAsync_WhenSocksServerStallsGreeting_ThrowsTimeout"
```

Expected: FAIL or hang until test timeout because no handshake deadline exists.

- [ ] **Step 3: Add handshake timeout option**

Use a record shape compatible with existing constructor call sites:

```csharp
public sealed record Socks5OutboundOptions(string Host, int Port, TimeSpan? HandshakeTimeout = null)
{
    public TimeSpan EffectiveHandshakeTimeout => HandshakeTimeout ?? TimeSpan.FromSeconds(10);
}
```

Wrap the full connect/handshake sequence:

```csharp
using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
timeout.CancelAfter(options.EffectiveHandshakeTimeout);
try
{
    await client.ConnectAsync(options.Host, options.Port, timeout.Token).ConfigureAwait(false);
    NetworkStream stream = client.GetStream();
    await WriteGreetingAsync(stream, timeout.Token).ConfigureAwait(false);
    await ReadGreetingResponseAsync(stream, timeout.Token).ConfigureAwait(false);
    await WriteConnectRequestAsync(stream, host.Trim(), port, timeout.Token).ConfigureAwait(false);
    await ReadConnectResponseAsync(stream, timeout.Token).ConfigureAwait(false);
    return stream;
}
catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
{
    throw new TimeoutException($"SOCKS5 handshake timed out after {options.EffectiveHandshakeTimeout}.");
}
```

- [ ] **Step 4: Add session table capacity test**

Add a test that constructs `TcpSessionTable(maxSessions: 1)`, establishes one SYN, and verifies a second distinct SYN is rejected without growing state.

```csharp
[Fact]
public void GetOrAddSynReceived_WhenSessionTableIsFull_ReturnsFalse()
{
    var table = new TcpSessionTable(maxSessions: 1);
    Assert.True(table.TryGetOrAddSynReceived(Flow(50000), 1000, 9000, out _));
    Assert.False(table.TryGetOrAddSynReceived(Flow(50001), 2000, 9000, out _));
}
```

- [ ] **Step 5: Implement bounded session table API**

Replace unconditional add with a `TryGetOrAddSynReceived` method:

```csharp
public bool TryGetOrAddSynReceived(TcpFlowKey key, uint clientInitialSequence, uint serverInitialSequence, out TcpSession? session)
{
    if (_sessions.TryGetValue(key, out session))
    {
        return true;
    }

    if (_sessions.Count >= _maxSessions)
    {
        session = null;
        return false;
    }

    session = TcpSession.SynReceived(clientInitialSequence, serverInitialSequence);
    return _sessions.TryAdd(key, session);
}
```

Update `RawTcpSessionHandler` to return `[]` when the table is full.

- [ ] **Step 6: Add bridge connection cap if missing**

Add a constructor option to `FakeIpTcpBridgeServer` such as `maxActiveConnections = 1024`, guarded by `SemaphoreSlim`. Add a test where `maxActiveConnections: 1` causes a second accepted connection to be rejected/closed while the first is active.

- [ ] **Step 7: Run resource tests**

Run:

```bash
dotnet test "tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj" --filter "FullyQualifiedName~Socks5OutboundTests|FullyQualifiedName~TcpSessionTable|FullyQualifiedName~FakeIpTcpBridge"
```

Expected: PASS.

---

### Task 7: Remove empty catches or make them explicit and observable

**Files:**
- Modify: `src/DotnetTun.Core/Sessions/TunPacketPump.cs`
- Modify: `src/DotnetTun.Core/Sessions/OutboundTcpPayloadSink.cs`
- Modify: `src/DotnetTun.Core/Sessions/FakeIpTcpBridgeServer.cs`
- Modify related tests if behavior changes.

- [ ] **Step 1: Search for empty catches in changed code**

Run:

```bash
grep -R "catch .*{[[:space:]]*}" src tests || true
```

Expected: identify empty cancellation catches flagged by review.

- [ ] **Step 2: Replace empty cancellation catches with helper**

Use a named helper to make intent explicit without swallowing unexpected failures:

```csharp
private static bool IsExpectedCancellation(Exception exception, CancellationToken cancellationToken)
    => exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
```

Use:

```csharp
catch (Exception exception) when (IsExpectedCancellation(exception, cancellationToken))
{
    return;
}
```

For cleanup paths, prefer returning a result or logging through an injected testable seam if one exists. Do not catch broad exceptions without rethrowing or surfacing the failure.

- [ ] **Step 3: Run affected tests**

Run:

```bash
dotnet test "tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj" --filter "FullyQualifiedName~TunPacketPumpTests|FullyQualifiedName~OutboundTcpPayloadSinkTests|FullyQualifiedName~FakeIpTcpBridge"
```

Expected: PASS.

---

### Task 8: Final verification and review gates

**Files:**
- No production file changes unless earlier verification reveals a direct fix.

- [ ] **Step 1: Run LSP diagnostics on modified source/test files**

Run diagnostics on every changed `.cs` file. Expected: zero errors.

- [ ] **Step 2: Run focused test suites**

Run:

```bash
dotnet test "tests/DotnetTun.Core.Tests/DotnetTun.Core.Tests.csproj"
dotnet test "tests/DotnetTun.Platforms.MacOS.Tests/DotnetTun.Platforms.MacOS.Tests.csproj"
dotnet test "tests/DotnetTun.Demo.Cli.Tests/DotnetTun.Demo.Cli.Tests.csproj"
```

Expected: all pass.

- [ ] **Step 3: Run RID builds and package verification**

Run:

```bash
dotnet build "src/DotnetTun.Platforms.MacOS/DotnetTun.Platforms.MacOS.csproj" -c Debug -r osx-arm64 --no-self-contained
dotnet build "src/DotnetTun.Platforms.MacOS/DotnetTun.Platforms.MacOS.csproj" -c Debug -r osx-x64 --no-self-contained
dotnet pack "src/DotnetTun.Platforms.MacOS/DotnetTun.Platforms.MacOS.csproj" -c Debug --no-build
python3 - <<'PY'
from zipfile import ZipFile
from pathlib import Path
package = Path('src/DotnetTun.Platforms.MacOS/bin/Debug/DotnetTun.Platforms.MacOS.1.0.0.nupkg')
expected = {
    'runtimes/osx-arm64/native/libutunshim.dylib',
    'runtimes/osx-x64/native/libutunshim.dylib',
}
with ZipFile(package) as archive:
    names = set(archive.namelist())
missing = sorted(expected - names)
if missing:
    raise SystemExit(f'Missing native asset entries: {missing}')
print('native asset entries present')
PY
```

Expected: RID builds succeed, package succeeds, native entries present.

- [ ] **Step 4: Run full solution verification**

Run:

```bash
dotnet build "DotnetTun.slnx"
dotnet test "DotnetTun.slnx"
```

Expected: build succeeds with 0 warnings/errors; all tests pass.

- [ ] **Step 5: Re-run full review**

Run the post-implementation review again. Expected: code quality and security reviewers no longer report the six blocking issues from the prior audit.

---

## Self-Review

- Spec coverage: covers all six audit blockers plus the empty-catch code-quality item surfaced in the same review.
- Placeholder scan: no unresolved `TBD`, `TODO`, or `<actual-sha256>` placeholders remain; the current dylib hash is embedded in Task 5.
- Type consistency: all named files and APIs match the current code shape inspected before writing this plan, except where tasks explicitly introduce new types (`MacCommand`) or helper APIs (`TryGet`, `TryGetOrAddSynReceived`).
- Scope control: real privileged macOS E2E, full TCP retransmission/windowing, and Linux/Windows production implementation remain out of scope for this blocker-fix plan.
