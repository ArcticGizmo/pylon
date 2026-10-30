using System.Runtime.InteropServices;
using Avalonia;
using Microsoft.Extensions.Logging;
using Pylon.App.Services;
using Pylon.Broker;
using Pylon.Broker.Pipe;

namespace Pylon.App;

internal static class Program
{
    public const int ExitBadArguments = 2;
    public const int ExitPipeInUse = 3;
    public const int ExitBrokerFailed = 4;

    /// <summary>Set before Avalonia starts; the App reads it.</summary>
    public static AppArgs Args { get; internal set; } = null!;
    public static FileLoggerProvider Logs { get; internal set; } = null!;

    // Initialization code. Don't use any Avalonia, third-party APIs or any SynchronizationContext-
    // reliant code before AppMain is called: things aren't initialized yet and stuff will break.
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Args = AppArgs.Parse(args);
        }
        catch (ArgumentException ex)
        {
            AttachParentConsole();
            Console.Error.WriteLine(ex.Message);
            return ExitBadArguments;
        }

        var console = Args.Headless && AttachParentConsole();
        Logs = new FileLoggerProvider(Path.Combine(Args.DataDirectory, "logs"), console);
        var log = Logs.CreateLogger("Pylon.App");
        // Without this a crash leaves nothing in Pylon's own log, only in the Windows event log.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.LogCritical(e.ExceptionObject as Exception, "Pylon crashed");

        using var single = new Mutex(initiallyOwned: true, $@"Local\Pylon-{Args.PipeName}", out var first);
        if (!first)
        {
            log.LogInformation("Pylon is already running for pipe {Pipe}; exiting.", Args.PipeName);
            return 0;
        }

        if (Args.Headless)
            return RunHeadless(log).GetAwaiter().GetResult();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
    }

    /// <summary>The broker and monitors without a tray, for scripts and smoke tests.</summary>
    static async Task<int> RunHeadless(ILogger log)
    {
        var runtime = CreateRuntime();
        try
        {
            await runtime.StartAsync();
        }
        catch (PipeInUseException ex)
        {
            log.LogError("{Message}", ex.Message);
            return ExitPipeInUse;
        }

        log.LogInformation("Headless. Ctrl+C to stop.");
        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.TrySetResult();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
        await stop.Task;
        await runtime.DisposeAsync();
        return 0;
    }

    public static PylonRuntime CreateRuntime() => PylonRuntime.Create(
        new PylonOptions
        {
            PipeName = Args.PipeName,
            DataDirectory = Args.DataDirectory,
            PosternPipeName = Args.PosternPipeName,
            DevIngest = Args.DevIngest,
            // The same trust as dev ingest: both let the test bench stand in for GitHub.
            TestBench = Args.DevIngest ? PylonOptions.DefaultTestBench : null,
        },
        new PylonOverrides { Logging = builder => builder.AddProvider(Logs) });

    // WinExe has no console; when run headless from a terminal, borrow the parent's for output.
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AttachConsole(int processId);

    static bool AttachParentConsole()
    {
        if (!AttachConsole(-1))
            return false;
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        return true;
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
