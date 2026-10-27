using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pylon.Interop;

/// <summary>What Windows says about a process. Any field may be null if Windows refused to say.</summary>
internal sealed record ProcessIdentity(uint ProcessId, string? ExePath, SecurityIdentifier? User, string? Error);

/// <summary>
/// The process at the other end of a named pipe. The same file as Postern's src/Shared/PipeProcess.cs, compiled into
/// the client library (is this really Pylon?) and later the broker (who is subscribing?).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PipeProcess
{
    const uint ProcessQueryLimitedInformation = 0x1000;
    const uint TokenQuery = 0x0008;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    public static uint ClientProcessId(SafePipeHandle pipe) =>
        GetNamedPipeClientProcessId(pipe, out var pid) ? pid : throw new Win32Exception(Marshal.GetLastPInvokeError());

    public static uint ServerProcessId(SafePipeHandle pipe) =>
        GetNamedPipeServerProcessId(pipe, out var pid) ? pid : throw new Win32Exception(Marshal.GetLastPInvokeError());

    public static ProcessIdentity Describe(uint pid)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid)
            return new(pid, null, null, $"OpenProcess failed (Win32 error {Marshal.GetLastPInvokeError()})");

        var buffer = new StringBuilder(1024);
        var size = (uint)buffer.Capacity;
        var exe = QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;

        SecurityIdentifier? user = null;
        if (OpenProcessToken(process, TokenQuery, out var token))
        {
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                user = identity.User;
        }

        var error = exe is null || user is null ? $"partial identity (Win32 error {Marshal.GetLastPInvokeError()})" : null;
        return new(pid, exe, user, error);
    }
}
