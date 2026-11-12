using System.Diagnostics;

namespace Glide.Service;

/// <summary>
/// Registers the service through <c>sc.exe</c>. The SCM APIs would work too, but
/// sc.exe reports a readable error for the cases that actually happen: not
/// elevated, and the service already existing (from an older install, possibly
/// pointing at the retired glide-service.exe), which is repointed in place.
/// </summary>
internal static class ServiceInstaller
{
    public const string ServiceName = "GlideIndex";
    public const string DisplayName = "Glide Index";

    private const int ErrorServiceExists = 1073;
    private const int ErrorServiceNotActive = 1062;

    public static int Install(string executablePath, string serviceFlag)
    {
        string binaryPath = $"\"{executablePath}\" {serviceFlag}";

        (int created, string createOutput) = Run("create", ServiceName, "binPath=", binaryPath, "start=", "auto", "DisplayName=", DisplayName);

        if (created == ErrorServiceExists)
        {
            // Stop the old one, point it at this executable, start it again.
            (int stopped, string stopOutput) = Run("stop", ServiceName);
            if (stopped != 0 && stopped != ErrorServiceNotActive)
            {
                Console.Error.WriteLine(stopOutput);
                return stopped;
            }

            WaitUntilStopped();

            (int configured, string configOutput) = Run("config", ServiceName, "binPath=", binaryPath, "start=", "auto", "DisplayName=", DisplayName);
            if (configured != 0)
            {
                Console.Error.WriteLine(configOutput);
                return configured;
            }

            Console.WriteLine($"Moved the existing {DisplayName} service to {executablePath}.");
        }
        else if (created != 0)
        {
            Console.Error.WriteLine(createOutput);
            return created;
        }

        (int started, string startOutput) = Run("start", ServiceName);
        if (started != 0)
        {
            Console.Error.WriteLine(startOutput);
            Console.Error.WriteLine("The service is registered but did not start.");
            Console.Error.WriteLine($"Log: {Path.Combine(IndexWorker.IndexDirectory, "service.log")}");
            return started;
        }

        Console.WriteLine($"{DisplayName} ({ServiceName}) runs \"{executablePath}\" {serviceFlag} and is started.");
        Console.WriteLine($"Index directory: {IndexWorker.IndexDirectory}");
        return 0;
    }

    public static int Uninstall()
    {
        // Stopping a service that is already stopped exits non-zero; only the
        // delete matters.
        Run("stop", ServiceName);
        WaitUntilStopped();

        (int deleted, string output) = Run("delete", ServiceName);
        if (deleted != 0)
        {
            Console.Error.WriteLine(output);
            return deleted;
        }

        Console.WriteLine($"Removed {DisplayName} ({ServiceName}).");
        Console.WriteLine("Index files were left in place; delete them by hand if you want them gone.");
        return 0;
    }

    private static void WaitUntilStopped()
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            (int code, string output) = Run("query", ServiceName);
            if (code != 0 || output.Contains("STOPPED", StringComparison.Ordinal))
            {
                return;
            }

            Thread.Sleep(250);
        }
    }

    private static (int ExitCode, string Output) Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("sc.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("sc.exe did not start.");

        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Trim());
    }
}
