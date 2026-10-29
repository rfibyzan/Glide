using System.Text;

namespace Glide.Service;

/// <summary>
/// Append-only log next to the index files. A service has nowhere to print to,
/// and the event log needs elevation to register a source, so this is the
/// channel an administrator can actually read.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 512 * 1024;

    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(IndexWorker.IndexDirectory, "service.log");

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(IndexWorker.IndexDirectory);

                var file = new FileInfo(Path);
                if (file.Exists && file.Length > MaxBytes)
                {
                    file.Delete();
                }

                File.AppendAllText(
                    Path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never take the service down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
