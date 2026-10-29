using System.Diagnostics;

namespace Glide.Service;

/// <summary>
/// Registers the service through <c>sc.exe</c>. The SCM APIs would work too, but
/// sc.exe reports a readable error for the two cases that actually happen:
/// not elevated, and the service already exists.
/// </summary>
internal static class ServiceInstaller
{
    public const string ServiceName = "GlideIndex";
    public const string DisplayName = "Glide Index";

    public static int Install(string executablePath)
    {
        int created = Run(
            "create",
            ServiceName,
            $"binPath= \"{executablePath}\"",
            "start=",
            "auto",
            "DisplayName=",
            $"\"{DisplayName}\"");

        if (created != 0)
        {
            return created;
        }

        int started = Run("start", ServiceName);

        if (started != 0)
        {
            Console.Error.WriteLine("The service was created but did not start.");
            Console.Error.WriteLine($"Log: {Path.Combine(IndexWorker.IndexDirectory, "service.log")}");
            return started;
        }

        Console.WriteLine($"Installed {DisplayName} ({ServiceName}) and started it.");
        Console.WriteLine($"Index directory: {IndexWorker.IndexDirectory}");
        return 0;
    }

    public static int Uninstall()
    {
        // Stopping a service that is already stopped exits non-zero; only the
        // delete matters.
        Run("stop", ServiceName);
        int deleted = Run("delete", ServiceName);

        if (deleted == 0)
        {
            Console.WriteLine($"Removed {DisplayName} ({ServiceName}).");
            Console.WriteLine("Index files were left in place; delete them by hand if you want them gone.");
        }

        return deleted;
    }

    private static int Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("sc.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("sc.exe did not start.");

        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine(output.Trim());
        }

        return process.ExitCode;
    }
}
