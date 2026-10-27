namespace Pylon.Client;

public sealed class PylonClientOptions
{
    public const string DefaultPipeName = "pylon";

    /// <summary>Defaults to %PYLON_PIPE% or "pylon".</summary>
    public string PipeName { get; init; } = Environment.GetEnvironmentVariable("PYLON_PIPE") is { Length: > 0 } pipe ? pipe : DefaultPipeName;

    /// <summary>
    /// The exe the pipe server must be running. Null (the default) reads it from where the running Pylon published it
    /// (HKCU\Software\Pylon\Pipes\{pipe}\BrokerPath).
    /// </summary>
    public string? ExpectedServerExe { get; init; }

    /// <summary>Skips the server check. Only for tests that host the broker in the test process.</summary>
    public bool SkipServerVerification { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>Pylon isn't there, or the pipe isn't served by Pylon.</summary>
public class PylonUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class PylonVerificationException(string message) : PylonUnavailableException(message);
