using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Pylon.FakeGitHub;

namespace Pylon.TestBench;

// pylon-testbench [--port 5610] [--pylon-pipe pylon-dev]
//
// Starts the fake GitHub on 127.0.0.1:<port> and opens the bench window. Nothing here calls real GitHub.

internal static class Program
{
    public const int DefaultPort = 5610;

    [STAThread]
    public static int Main(string[] args)
    {
        var port = DefaultPort;
        var pipe = "pylon-dev";
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): port = p; i++; break;
                case "--pylon-pipe" when i + 1 < args.Length: pipe = args[++i]; break;
                default:
                    AttachConsole(-1);
                    Console.Error.WriteLine("usage: pylon-testbench [--port 5610] [--pylon-pipe pylon-dev]");
                    return 2;
            }
        }

        var state = new FakeGitHubState();
        var log = new BenchLog();
        Uri address;
        try
        {
            (_, address) = FakeGitHubApi.StartAsync(state, port, r => log.Add(BenchLog.Kind.Fake, $"{r.Status} GET {r.Path}")).GetAwaiter().GetResult();
        }
        catch (IOException ex)
        {
            AttachConsole(-1);
            Console.Error.WriteLine($"Couldn't listen on 127.0.0.1:{port} ({ex.Message}). Pass --port to use another.");
            return 3;
        }

        return AppBuilder.Configure(() => new BenchApp(state, log, address, pipe))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime([]);
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AttachConsole(int processId);
}

internal sealed class BenchApp(FakeGitHubState state, BenchLog log, Uri address, string pipe) : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new BenchWindow(state, log, address, pipe);
        base.OnFrameworkInitializationCompleted();
    }
}
