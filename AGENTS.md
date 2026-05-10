# DotnetTun2 Project Instructions

## C# Local Variable Style

Prefer `var` for local variables when the type is obvious from the right-hand side.

```csharp
// Preferred
var openResult = await _tunDevice.OpenTunAsync(cancellationToken).ConfigureAwait(false);

// Avoid
TunDeviceOpenResult openResult = await _tunDevice.OpenTunAsync(cancellationToken).ConfigureAwait(false);
```

Use explicit local variable types only when they improve readability, prevent ambiguity, or document an important abstraction boundary.
