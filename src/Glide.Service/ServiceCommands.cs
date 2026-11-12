using Glide.Core.Index;
using Glide.Core.Snapshot;

namespace Glide.Service;

/// <summary>
/// The index service's entry points, called by glide.exe. The SCM starts
/// <c>glide.exe --service</c>; the other commands manage that registration or
/// run the indexer by hand. Everything here needs administrator rights.
/// </summary>
public static class ServiceCommands
{
    public const string ServiceFlag = "--service";

    /// <summary>
    /// Runs the command named by <paramref name="flag"/>, or returns null when
    /// the flag is not a service command.
    /// </summary>
    public static int? TryRun(string flag, string executablePath)
    {
        switch (flag)
        {
            case ServiceFlag:
                EnablePrivileges();
                return ServiceHost.Run(ServiceInstaller.ServiceName, new IndexWorker().Run);

            case "--service-install":
                return ServiceInstaller.Install(executablePath, ServiceFlag);

            case "--service-uninstall":
                return ServiceInstaller.Uninstall();

            case "--service-build":
                EnablePrivileges();
                return BuildOnce();

            case "--service-console":
                EnablePrivileges();
                return RunConsole();

            default:
                return null;
        }
    }

    public static void PrintUsage(TextWriter output)
    {
        output.WriteLine("Index service (elevated):");
        output.WriteLine("  --service-install    register the index service and start it; moves an existing one to this exe");
        output.WriteLine("  --service-uninstall  stop and remove the index service (index files are kept)");
        output.WriteLine("  --service-build      rebuild the indexes once and exit");
        output.WriteLine("  --service-console    run the indexing loop in this window");
    }

    // LocalSystem holds SeBackupPrivilege but it is disabled by default. Enable
    // it (best effort) so volume USN journals can be opened.
    private static void EnablePrivileges() => TokenPrivilege.EnableBackupPrivilege();

    private static int BuildOnce()
    {
        int failures = 0;

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            string letter = drive.Name.TrimEnd(':', '\\');
            if (letter.Length != 1)
            {
                continue;
            }

            try
            {
                string indexPath = IndexWorker.IndexPathFor(letter);
                Directory.CreateDirectory(IndexWorker.IndexDirectory);

                IndexBuildResult result = IndexBuilder.Build(letter, indexPath);
                if (result.IndexedEntries == 0 && result.EnumeratedRecords > 0)
                {
                    Console.WriteLine($"{letter}: USN journal returned 0 entries; falling back to full scan...");
                    int scanned = IndexWriter.Write(indexPath, DirectoryScanner.Enumerate(letter + @":\", 200_000));
                    Console.WriteLine($"{letter}: Scanned {scanned} entries.");
                }
                else
                {
                    Console.WriteLine(
                        $"{letter}: {result.IndexedEntries} entries from {result.EnumeratedRecords} records " +
                        $"in {new FileInfo(indexPath).Length / (1024 * 1024.0):0.0} MB");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"{letter}: {ex.Message}");
                failures++;
            }
            finally
            {
                MemoryTrimmer.Trim();
            }
        }

        if (failures > 0)
        {
            Console.Error.WriteLine("Run this from an elevated prompt: reading a volume's USN journal needs administrator rights.");
        }

        return failures == 0 ? 0 : 2;
    }

    private static int RunConsole()
    {
        using var source = new CancellationTokenSource();

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            source.Cancel();
            ServiceHost.Stop();
        };

        new IndexWorker().Run(source.Token);
        return 0;
    }
}
