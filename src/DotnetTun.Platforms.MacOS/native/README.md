# macOS native assets

This directory contains the packaged `libutunshim.dylib` binaries used by `DotnetTun.Platforms.MacOS`.

## Source

The current `libutunshim.dylib` assets were copied from a local upstream linker reference during the migration that moved the binaries into this repository-owned project directory. The original linker reference directory is intentionally not referenced by the project file.

## Build

The current provenance is the local upstream linker reference used during migration; this repository does not currently contain a fully reproducible source/build recipe for these binaries. Future binary updates should document the upstream source, build inputs, toolchain, and commands before replacing either asset.

## SHA-256

| Asset | SHA-256 |
| --- | --- |
| `native/osx-arm64/libutunshim.dylib` | `862aa238a467d7d808eda6f4265f3452d87665893c013e5a1bce56f15d16deee` |
| `native/osx-x64/libutunshim.dylib` | `862aa238a467d7d808eda6f4265f3452d87665893c013e5a1bce56f15d16deee` |

## Review policy

Any change to these native binaries must be reviewed as a security-sensitive supply-chain change. Reviewers should verify the replacement SHA-256 values, update this README and the packaging tests in the same change, and confirm the NuGet package still contains only the expected macOS runtime native entries.
