using System.Diagnostics;
using System.Net;

namespace DotnetTun.Platforms.MacOS.Networking;

public sealed class MacDefaultGatewayResolver
{
    public async ValueTask<IPAddress?> GetDefaultGatewayAsync(CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo("/sbin/route")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("get");
        startInfo.ArgumentList.Add("default");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start route command.");
        string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        _ = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return process.ExitCode == 0 && TryParseDefaultGateway(stdout, out IPAddress? gateway)
            ? gateway
            : null;
    }

    public static bool TryParseDefaultGateway(string output, out IPAddress? gateway)
    {
        gateway = null;
        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("gateway:", StringComparison.Ordinal))
            {
                continue;
            }

            string value = trimmed["gateway:".Length..].Trim();
            return IPAddress.TryParse(value, out gateway);
        }

        return false;
    }
}
