using Glide.Core.Index;
using Glide.Service;

if (args.Length > 0)
{
    switch (args[0])
    {
        case "--install":
            return ServiceInstaller.Install(Environment.ProcessPath ?? "glide-service.exe");

        case "--uninstall":
            return ServiceInstaller.Uninstall();

        case "--build":
            return BuildOnce();

        case "--console":
            return RunConsole();

        default:
            PrintUsage();
            return 1;
    }
}

return ServiceHost.Run(ServiceInstaller.ServiceName, new IndexWorker().Run);

static int BuildOnce()
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
            Console.WriteLine(
                $"{letter}: {result.IndexedEntries} entries from {result.EnumeratedRecords} records " +
                $"in {new FileInfo(indexPath).Length / (1024 * 1024.0):0.0} MB");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"{letter}: {ex.Message}");
            failures++;
        }
    }

    if (failures > 0)
    {
        Console.Error.WriteLine("Run this from an elevated prompt: reading a volume's USN journal needs administrator rights.");
    }

    return failures == 0 ? 0 : 2;
}

static int RunConsole()
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

static void PrintUsage()
{
    Console.WriteLine("Glide index service");
    Console.WriteLine("  glide-service                 run under the service control manager");
    Console.WriteLine("  glide-service --install       register and start the service (elevated)");
    Console.WriteLine("  glide-service --uninstall     stop and remove the service (elevated)");
    Console.WriteLine("  glide-service --build         rebuild the indexes once and exit (elevated)");
    Console.WriteLine("  glide-service --console       run the indexing loop in this window (elevated)");
}
