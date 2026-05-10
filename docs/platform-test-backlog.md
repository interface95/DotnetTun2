# Platform Test Backlog

These tests are intentionally not implemented in this pass. They should be added when a Windows-capable machine is available, and when Linux privileged test execution is acceptable.

## Windows Wintun tests

- `WindowsTunDevice.OpenAsync_WithEmptyAdapterName_ThrowsArgumentException`
  - Scope: unit test, no native Wintun required.
  - Expected: empty or whitespace adapter names are rejected before native calls.

- `WindowsTunDevice.OpenAsync_WithNativeFailure_ReturnsFailedResult`
  - Scope: unit test with fake `IWindowsTunNativeApi`.
  - Expected: failed native open is surfaced as `WindowsTunOpenResult.Success == false` with message/error code.

- `WindowsTunDevice.OpenAsync_WithNativeSuccess_ReturnsAdapterHandleAndInterfaceName`
  - Scope: unit test with fake `IWindowsTunNativeApi`.
  - Expected: successful native open returns handle and interface name unchanged.

- `WindowsTunNativeApi.OpenAdapter_WithoutPackagedWintun_ReturnsExplicitFailure`
  - Scope: Windows-only integration/smoke test.
  - Expected: until `wintun.dll` native packaging exists, opening through the default native adapter fails explicitly instead of pretending Windows support is active.

- `WindowsTunNativeApi.OpenAdapter_WithPackagedWintun_CreatesOrOpensAdapter`
  - Scope: future Windows-only privileged integration test.
  - Prerequisites: official `wintun.dll` packaged side-by-side, administrator privileges, cleanup policy for created adapters.
  - Expected: adapter/session can be opened and closed without leaking handles.

## Linux TUN tests

- `LinuxTunDevice.OpenAsync_WithEmptyRequestedName_ThrowsArgumentException`
  - Scope: unit test, no `/dev/net/tun` required.
  - Expected: empty or whitespace names are rejected before native calls.

- `LinuxTunDevice.OpenAsync_WithNativeFailure_ReturnsFailedResult`
  - Scope: unit test with fake `ILinuxTunNativeApi`.
  - Expected: native failure maps to `LinuxTunOpenResult.Success == false` and preserves errno.

- `LinuxTunDevice.OpenAsync_WithNativeSuccess_ReturnsFileDescriptorAndInterfaceName`
  - Scope: unit test with fake `ILinuxTunNativeApi`.
  - Expected: successful native open returns fd and kernel-assigned interface name unchanged.

- `LinuxTunNativeApi.OpenTun_WithoutCapNetAdmin_ReturnsPermissionFailure`
  - Scope: Linux-only integration/smoke test.
  - Prerequisites: `/dev/net/tun` exists, run without `CAP_NET_ADMIN`.
  - Expected: open/ioctl failure is explicit and closes any partially opened fd.

- `LinuxTunNativeApi.OpenTun_WithCapNetAdmin_CreatesTunInterface`
  - Scope: future Linux-only privileged integration test.
  - Prerequisites: root or `CAP_NET_ADMIN`, `/dev/net/tun`, cleanup command for created interface.
  - Expected: `TUNSETIFF` with `IFF_TUN | IFF_NO_PI` returns a valid fd and interface name.

## Cross-platform contract tests

- Add a shared abstraction once `ITunDevice` exists.
- Verify all platform devices expose the same lifecycle shape: open, close/dispose, read packet, write packet.
- Verify unsupported or unconfigured native dependencies fail explicitly with actionable messages.
