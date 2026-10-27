using Microsoft.Win32;

namespace Pylon.Broker;

/// <summary>
/// HKCU\Software\Pylon\Pipes\{pipe} → BrokerPath, ProcessId. Pylon.Client reads BrokerPath and refuses a pipe served
/// by any other exe, the same check Postern.Client makes.
/// </summary>
public static class Discovery
{
    public const string Root = @"Software\Pylon\Pipes";

    public static void Publish(string pipeName)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{Root}\{pipeName}");
        key.SetValue("BrokerPath", Environment.ProcessPath ?? "");
        key.SetValue("ProcessId", Environment.ProcessId);
        key.SetValue("StartedAt", DateTimeOffset.UtcNow.ToString("O"));
    }

    public static void Withdraw(string pipeName)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{Root}\{pipeName}", writable: true);
        if (key?.GetValue("ProcessId") is int pid && pid == Environment.ProcessId)
            Registry.CurrentUser.DeleteSubKeyTree($@"{Root}\{pipeName}", throwOnMissingSubKey: false);
    }
}
