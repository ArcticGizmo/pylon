namespace Pylon.Broker;

/// <summary>
/// Debug builds run as "Pylon (Dev)", the same split as Postern (Dev): their own pipe and data folder, and they call
/// out through Postern (Dev), so a dev build can run next to an installed Pylon and never touches its data or its
/// Postern approval. <c>PYLON_DEV</c> forces it either way (<c>0</c>/<c>false</c> runs Debug as the real Pylon).
/// </summary>
public static class Profile
{
    public const string DefaultPipeName = "pylon";
    public const string DevPipeName = "pylon-dev";
    public const string EnvironmentVariable = "PYLON_DEV";

    public static bool IsDev { get; } = ComputeIsDev();

    public static string PipeName => IsDev ? DevPipeName : DefaultPipeName;

    public static string PosternPipeName => IsDev ? "postern-dev" : "postern";

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), IsDev ? "Pylon.Dev" : "Pylon.Data");

    /// <summary>"" or " (Dev)", for the tray and window titles.</summary>
    public static string DisplaySuffix => IsDev ? " (Dev)" : "";

    static bool ComputeIsDev()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrEmpty(value))
            return !(value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase));
#if DEBUG
        return true;
#else
        return false;
#endif
    }
}
