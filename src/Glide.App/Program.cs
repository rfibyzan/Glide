using System.Globalization;

namespace Glide.App;

/// <summary>
/// Placeholder entry point for v0.1.1. It exists so the memory gate can
/// measure the native-AOT runtime baseline before any UI is written.
/// The window, tray icon and hotkey arrive in v0.2.0.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--idle")
        {
            if (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
            {
                Console.Error.WriteLine("--idle expects a number of seconds");
                return 2;
            }

            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            return 0;
        }

        Console.WriteLine("Glide app shell. UI lands in v0.2.0. Use --idle <seconds> to measure.");
        return 0;
    }
}
