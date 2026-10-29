using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using Glide.App.Ui;
using Glide.Core.Index;
using Glide.Core.Snapshot;
using Microsoft.Win32.SafeHandles;

namespace Glide.App;

internal static class Program
{
    private const int ResultLimit = 50;
    private const string InstanceMutexName = @"Local\Glide.Singleton";

    private static int Main(string[] args)
    {
        try
        {
            if (HasFlag(args, "--bench"))
            {
                return RunBench(args);
            }

            if (HasFlag(args, "--index-build"))
            {
                return RunIndexBuild(args);
            }

            if (HasFlag(args, "--index-info"))
            {
                return RunIndexInfo(args);
            }

            if (HasFlag(args, "--index-bench"))
            {
                return RunIndexBench(args);
            }

            if (HasFlag(args, "--index-bench-io"))
            {
                return RunIndexIoBench(args);
            }

            if (GetValue(args, "--idle") is string idle)
            {
                return RunIdle(args, idle);
            }

            if (HasFlag(args, "--help") || HasFlag(args, "-h") || HasFlag(args, "/?"))
            {
                PrintUsage();
                return 0;
            }

            return RunUi(args);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunUi(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool isFirstInstance);

        if (!isFirstInstance)
        {
            SearchWindow.RequestShow();
            return 0;
        }

        return SearchWindow.Run(
            GetValue(args, "--snapshot"),
            GetValue(args, "--scan"),
            HasFlag(args, "--show"),
            GetInteger(args, "--max-entries", 75_000));
    }

    private static int RunIndexBuild(string[] args)
    {
        string snapshotPath = GetValue(args, "--snapshot")
            ?? throw new ArgumentException("--index-build requires --snapshot <file>");
        string indexPath = GetValue(args, "--index-build")
            ?? throw new ArgumentException("--index-build requires an output path");

        EntryStore store = EntryStore.FromFile(snapshotPath);
        long start = Stopwatch.GetTimestamp();
        int written = IndexWriter.Write(indexPath, store.Entries);
        long end = Stopwatch.GetTimestamp();

        var file = new FileInfo(indexPath);
        Console.WriteLine($"entries: {written}");
        Console.WriteLine($"index bytes: {file.Length}");
        Console.WriteLine($"bytes per entry: {(double)file.Length / Math.Max(1, written):0.0}");
        Console.WriteLine($"build ms: {(end - start) * 1000.0 / Stopwatch.Frequency:0.0}");
        return 0;
    }

    private static int RunIndexInfo(string[] args)
    {
        string indexPath = GetValue(args, "--index-info")
            ?? throw new ArgumentException("--index-info requires an index path");

        using var handle = IndexFile.OpenHandle(indexPath);
        IndexHeader header = IndexFile.ReadHeader(handle);
        long bytes = new FileInfo(indexPath).Length;

        Console.WriteLine($"entries: {header.Count}");
        Console.WriteLine($"records offset: {header.RecordsOffset}");
        Console.WriteLine($"records length: {header.RecordsLength}");
        Console.WriteLine($"file bytes: {bytes}");
        return 0;
    }

    /// <summary>
    /// Diagnostic that reads every record without scoring, so the latency budget
    /// can be split into "reading the file" and "matching against it".
    /// </summary>
    private static int RunIndexIoBench(string[] args)
    {
        string indexPath = GetValue(args, "--index-bench-io")
            ?? throw new ArgumentException("--index-bench-io requires an index path");

        int iterations = GetInteger(args, "--iterations", 20);
        int warmup = GetInteger(args, "--warmup", 5);

        using SafeFileHandle handle = IndexFile.OpenHandle(indexPath);
        IndexHeader header = IndexFile.ReadHeader(handle);

        for (int i = 0; i < warmup; i++)
        {
            _ = CountRecords(handle, header);
        }

        var samples = new double[iterations];

        for (int i = 0; i < iterations; i++)
        {
            long start = Stopwatch.GetTimestamp();
            _ = CountRecords(handle, header);
            long end = Stopwatch.GetTimestamp();
            samples[i] = (end - start) * 1000.0 / Stopwatch.Frequency;
        }

        Array.Sort(samples);
        double megabytes = header.RecordsLength / (1024.0 * 1024.0);

        Console.WriteLine($"records bytes: {header.RecordsLength}");
        Print("p50 ms", Percentile(samples, 0.50));
        Print("p95 ms", Percentile(samples, 0.95));
        Console.WriteLine($"p50 GB/s: {megabytes / 1024.0 / (Percentile(samples, 0.50) / 1000.0):0.00}");
        return 0;
    }

    private static int CountRecords(SafeFileHandle handle, IndexHeader header)
    {
        int workers = Math.Min(8, Environment.ProcessorCount);
        long chunk = (header.RecordsLength + workers - 1) / workers;
        int total = 0;

        Parallel.For(0, workers, worker =>
        {
            long position = header.RecordsOffset + (worker * chunk);
            long end = Math.Min(header.RecordsOffset + header.RecordsLength, position + chunk);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(512 * 1024);
            int count = 0;

            try
            {
                while (position < end)
                {
                    int wanted = (int)Math.Min(buffer.Length, end - position);
                    int read = RandomAccess.Read(handle, buffer.AsSpan(0, wanted), position);

                    if (read <= 0)
                    {
                        break;
                    }

                    int index = 0;

                    while (index < read)
                    {
                        int nul = buffer.AsSpan(index, read - index).IndexOf((byte)0);
                        if (nul < 0)
                        {
                            break;
                        }

                        index += nul + 1;
                        count++;
                    }

                    position += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            Interlocked.Add(ref total, count);
        });

        return total;
    }

    private static int RunIndexBench(string[] args)
    {
        string indexPath = GetValue(args, "--index-bench")
            ?? throw new ArgumentException("--index-bench requires an index path");

        string query = GetValue(args, "--query") ?? "glide";
        int iterations = GetInteger(args, "--iterations", 50);
        int warmup = GetInteger(args, "--warmup", 5);
        int repeats = GetInteger(args, "--repeats", 1);
        double budgetMs = GetDouble(args, "--budget-ms", 0);

        int results = 0;
        var p50s = new double[repeats];
        var p95s = new double[repeats];
        var p99s = new double[repeats];
        var maxes = new double[repeats];
        var samples = new double[iterations];

        for (int repeat = 0; repeat < repeats; repeat++)
        {
            for (int i = 0; i < warmup; i++)
            {
                _ = IndexSearchSource.Search(indexPath, query, ResultLimit);
            }

            for (int i = 0; i < iterations; i++)
            {
                long start = Stopwatch.GetTimestamp();
                results = IndexSearchSource.Search(indexPath, query, ResultLimit).Count;
                long end = Stopwatch.GetTimestamp();
                samples[i] = (end - start) * 1000.0 / Stopwatch.Frequency;
            }

            Array.Sort(samples);
            p50s[repeat] = Percentile(samples, 0.50);
            p95s[repeat] = Percentile(samples, 0.95);
            p99s[repeat] = Percentile(samples, 0.99);
            maxes[repeat] = samples[^1];

            if (repeats > 1)
            {
                Console.WriteLine($"run {repeat + 1}: p50 {p50s[repeat]:0.00} p95 {p95s[repeat]:0.00} max {maxes[repeat]:0.00}");
            }
        }

        Array.Sort(p50s);
        Array.Sort(p95s);
        Array.Sort(p99s);
        Array.Sort(maxes);

        double Median(double[] sorted) => repeats == 1 ? sorted[0] : Percentile(sorted, 0.50);

        Console.WriteLine($"index entries: {IndexSearchSource.Count(indexPath)}");
        Console.WriteLine($"query: {query}");
        Console.WriteLine($"results: {results}");
        Console.WriteLine($"repeats: {repeats}");
        Print("p50 ms", Median(p50s));
        Print("p95 ms", Median(p95s));
        Print("p99 ms", Median(p99s));
        Print("max ms", Median(maxes));

        double p95 = Median(p95s);
        if (budgetMs > 0 && p95 > budgetMs)
        {
            Console.Error.WriteLine($"Index latency gate FAILED: p95 {p95:0.00} ms exceeds {budgetMs:0.00} ms");
            return 3;
        }

        return 0;
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

    private static void PrintUsage()
    {
        Console.WriteLine("Glide 0.3.0");
        Console.WriteLine("  glide                          run the launcher (Ctrl+Alt+Space)");
        Console.WriteLine("  glide --show                   open the window, starting the app if needed");
        Console.WriteLine("  glide --scan <folder>          index a folder instead of the user profile");
        Console.WriteLine("  glide --max-entries <n>        cap the scanned entries (default 75000)");
        Console.WriteLine("  glide --snapshot <file>        load a snapshot file instead of scanning");
        Console.WriteLine("  glide --bench --snapshot <f> [--query <text>] [--iterations <n>] [--budget-ms <n>]");
        Console.WriteLine("  glide --snapshot <file> --idle <seconds>");
        Console.WriteLine("  glide --index-build <index> --snapshot <file>");
        Console.WriteLine("  glide --index-info <index>");
        Console.WriteLine("  glide --index-bench <index> [--query <text>] [--iterations <n>] [--warmup <n>] [--repeats <n>] [--budget-ms <n>]");
        Console.WriteLine("  glide --index-bench-io <index> [--iterations <n>]");
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
