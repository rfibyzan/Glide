using System.Diagnostics;
using System.Globalization;
using Glide.Core.Snapshot;

namespace Glide.App;

/// <summary>
/// Command line for v0.2.0. The search window arrives in v0.3.0; until then the
/// app exists to load snapshots and to be measured, so the memory and latency
/// gates are real numbers instead of promises.
/// </summary>
internal static class Program
{
    private const int ResultLimit = 50;

    private static int Main(string[] args)
    {
        try
        {
            if (HasFlag(args, "--bench"))
            {
                return RunBench(args);
            }

            if (GetValue(args, "--idle") is string idle)
            {
                return RunIdle(args, idle);
            }

            Console.WriteLine("Glide 0.2.0");
            Console.WriteLine("  --bench --snapshot <file> [--query <text>] [--iterations <n>] [--budget-ms <n>]");
            Console.WriteLine("  --snapshot <file> --idle <seconds>");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or FormatException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunBench(string[] args)
    {
        string snapshotPath = GetValue(args, "--snapshot")
            ?? throw new ArgumentException("--bench requires --snapshot <file>");

        string query = GetValue(args, "--query") ?? "glide";
        int iterations = GetInteger(args, "--iterations", 50);
        int warmup = GetInteger(args, "--warmup", 5);
        double budgetMs = GetDouble(args, "--budget-ms", 0);

        EntryStore store = EntryStore.FromFile(snapshotPath);

        for (int i = 0; i < warmup; i++)
        {
            _ = store.Search(query, ResultLimit);
        }

        var samples = new double[iterations];
        int results = 0;

        for (int i = 0; i < iterations; i++)
        {
            long start = Stopwatch.GetTimestamp();
            results = store.Search(query, ResultLimit).Count;
            long end = Stopwatch.GetTimestamp();
            samples[i] = (end - start) * 1000.0 / Stopwatch.Frequency;
        }

        Array.Sort(samples);

        double p50 = Percentile(samples, 0.50);
        double p95 = Percentile(samples, 0.95);
        double p99 = Percentile(samples, 0.99);
        double max = samples[^1];

        Console.WriteLine($"snapshot entries: {store.Count}");
        Console.WriteLine($"query: {query}");
        Console.WriteLine($"iterations: {iterations}");
        Console.WriteLine($"results: {results}");
        Print("p50 ms", p50);
        Print("p95 ms", p95);
        Print("p99 ms", p99);
        Print("max ms", max);

        if (budgetMs > 0 && p95 > budgetMs)
        {
            Console.Error.WriteLine($"Latency gate FAILED: p95 {p95:0.00} ms exceeds {budgetMs:0.00} ms");
            return 3;
        }

        return 0;
    }

    private static int RunIdle(string[] args, string idle)
    {
        if (!int.TryParse(idle, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) || seconds < 0)
        {
            throw new ArgumentException("--idle expects a number of seconds");
        }

        if (GetValue(args, "--snapshot") is string snapshotPath)
        {
            EntryStore store = EntryStore.FromFile(snapshotPath);
            Console.WriteLine($"snapshot entries: {store.Count}");
        }

        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        return 0;
    }

    private static double Percentile(double[] sorted, double fraction)
    {
        int index = (int)Math.Ceiling(sorted.Length * fraction) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static void Print(string label, double milliseconds)
    {
        Console.WriteLine($"{label}: {milliseconds.ToString("0.00", CultureInfo.InvariantCulture)}");
    }

    private static bool HasFlag(string[] args, string name)
    {
        return Array.IndexOf(args, name) >= 0;
    }

    private static string? GetValue(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length)
        {
            return null;
        }

        return args[index + 1];
    }

    private static int GetInteger(string[] args, string name, int fallback)
    {
        string? raw = GetValue(args, name);
        return raw is null ? fallback : int.Parse(raw, CultureInfo.InvariantCulture);
    }

    private static double GetDouble(string[] args, string name, double fallback)
    {
        string? raw = GetValue(args, name);
        return raw is null ? fallback : double.Parse(raw, CultureInfo.InvariantCulture);
    }
}
