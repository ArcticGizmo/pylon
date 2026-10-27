using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using Pylon.Interop;

namespace Pylon.Client;

/// <summary>Opens the pipe and checks, before a byte is sent, that the server is this user's Pylon (as Postern.Client does).</summary>
[SupportedOSPlatform("windows")]
internal static class PipeConnector
{
    public static bool PipeExists(string pipeName)
    {
        try
        {
            return Directory.EnumerateFiles(@"\\.\pipe\", pipeName).Any();
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static string? PublishedBrokerPath(string pipeName)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Pylon\Pipes\{pipeName}");
        return key?.GetValue("BrokerPath") as string;
    }

    public static SocketsHttpHandler CreateHandler(PylonClientOptions options) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        ConnectCallback = async (_, cancellationToken) =>
        {
            var stream = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await stream.ConnectAsync((int)options.ConnectTimeout.TotalMilliseconds, cancellationToken);
                if (!options.SkipServerVerification)
                    Verify(stream, options);
                return stream;
            }
            catch (UnauthorizedAccessException)
            {
                await stream.DisposeAsync();
                throw new PylonVerificationException($"The '{options.PipeName}' pipe refused this account: it belongs to another user's Pylon.");
            }
            catch (TimeoutException ex)
            {
                await stream.DisposeAsync();
                throw new PylonUnavailableException($"Pylon isn't running on pipe '{options.PipeName}'.", ex);
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }
        },
    };

    static void Verify(NamedPipeClientStream stream, PylonClientOptions options)
    {
        var expected = options.ExpectedServerExe ?? PublishedBrokerPath(options.PipeName)
            ?? throw new PylonVerificationException($"Pylon hasn't published its location for pipe '{options.PipeName}'; is it running?");

        var server = PipeProcess.Describe(PipeProcess.ServerProcessId(stream.SafePipeHandle));
        var me = WindowsIdentity.GetCurrent().User;
        if (server.User is null || server.User != me)
            throw new PylonVerificationException($"The '{options.PipeName}' pipe is served by pid {server.ProcessId}, which isn't running as you. Not subscribing.");
        if (server.ExePath is null || !string.Equals(Path.GetFullPath(server.ExePath), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            throw new PylonVerificationException($"The '{options.PipeName}' pipe is served by {server.ExePath ?? "an unknown exe"}, not Pylon ({expected}). Not subscribing.");
    }
}
