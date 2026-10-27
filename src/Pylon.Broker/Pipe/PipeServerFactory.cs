using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;
using Microsoft.Win32.SafeHandles;

namespace Pylon.Broker.Pipe;

/// <summary>The broker refused to start because something already owns the pipe name.</summary>
public sealed class PipeInUseException(string pipeName, Exception inner)
    : Exception($"The pipe '{pipeName}' already exists: another Pylon is running, or something is squatting on the name.", inner);

/// <summary>
/// Creates every pipe instance Kestrel listens on, exactly as Postern does (proved in Postern's spikes/pipe-broker):
/// <list type="bullet">
/// <item>DACL: the current user's SID gets read/write + create-instance; nobody else is listed. NETWORK is denied.</item>
/// <item><c>PIPE_REJECT_REMOTE_CLIENTS</c>, which .NET doesn't expose, hence <c>CreateNamedPipeW</c>.</item>
/// <item><c>FILE_FLAG_FIRST_PIPE_INSTANCE</c> on the first instance: if the name exists, fail rather than share it.</item>
/// </list>
/// Event topics can name private repos and mail folders, so subscriptions get the same pipe protection as credentials.
/// </summary>
internal static class PipeServerFactory
{
    const uint PipeAccessDuplex = 0x00000003;
    const uint FileFlagFirstPipeInstance = 0x00080000;
    const uint FileFlagOverlapped = 0x40000000;
    const uint PipeRejectRemoteClients = 0x00000008;
    const uint PipeUnlimitedInstances = 255;
    const int ErrorAccessDenied = 5;

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateNamedPipeW")]
    static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes securityAttributes);

    public static PipeSecurity CurrentUserOnly()
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return security;
    }

    public static Func<CreateNamedPipeServerStreamContext, NamedPipeServerStream> Create(PipeSecurity security)
    {
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var first = 1;
        return context =>
        {
            var isFirst = Interlocked.Exchange(ref first, 0) == 1;
            var openMode = PipeAccessDuplex | FileFlagOverlapped | (isFirst ? FileFlagFirstPipeInstance : 0);
            var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
            try
            {
                var attributes = new SecurityAttributes
                {
                    nLength = Marshal.SizeOf<SecurityAttributes>(),
                    lpSecurityDescriptor = pinned.AddrOfPinnedObject(),
                };
                var handle = CreateNamedPipe(@"\\.\pipe\" + context.NamedPipeEndPoint.PipeName, openMode, PipeRejectRemoteClients,
                    PipeUnlimitedInstances, 0, 0, 0, ref attributes);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastPInvokeError();
                    handle.Dispose();
                    var inner = new Win32Exception(error);
                    throw isFirst && error == ErrorAccessDenied
                        ? new PipeInUseException(context.NamedPipeEndPoint.PipeName, inner)
                        : new IOException($"CreateNamedPipe failed: {inner.Message}", inner);
                }
                return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
            }
            finally
            {
                pinned.Free();
            }
        };
    }
}
