namespace Glide.App;

/// <summary>
/// A small log in %LOCALAPPDATA%\Glide\glide.log for the things a windowed
/// process cannot print: hook and integration decisions and failures. It is
/// cut back when it passes 256 KB, and writing it never throws.
/// </summary>
internal static class AppLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Glide",
        "glide.log");

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

                var file = new FileInfo(Path);
                if (file.Exists && file.Length > MaxBytes)
                {
                    file.Delete();
                }

                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging is best effort.
            }
        }
    }
}
