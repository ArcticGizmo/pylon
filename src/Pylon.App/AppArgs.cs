using Pylon.Broker;

namespace Pylon.App;

/// <summary>
/// Command line. Everything is optional; a plain launch is the tray app on the default pipe.
/// <code>
/// Pylon.exe [--headless] [--pipe NAME] [--data-dir DIR] [--postern-pipe NAME] [--dev-ingest]
/// </code>
/// Dev ingest is always on in a Debug build (Pylon (Dev)), and only on in Release when asked for.
/// </summary>
internal sealed record AppArgs(bool Headless, string PipeName, string DataDirectory, string PosternPipeName, bool DevIngest)
{
    public static AppArgs Parse(string[] args)
    {
        var headless = false;
        var pipe = Profile.PipeName;
        var dataDir = Profile.DataDirectory;
        var postern = Profile.PosternPipeName;
        var devIngest = Profile.IsDev;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--headless": headless = true; break;
                case "--pipe": pipe = Value(args, ref i); break;
                case "--data-dir": dataDir = Path.GetFullPath(Value(args, ref i)); break;
                case "--postern-pipe": postern = Value(args, ref i); break;
                case "--dev-ingest": devIngest = true; break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }
        return new(headless, pipe, dataDir, postern, devIngest);
    }

    static string Value(string[] args, ref int i) =>
        ++i < args.Length ? args[i] : throw new ArgumentException($"'{args[i - 1]}' needs a value.");
}
