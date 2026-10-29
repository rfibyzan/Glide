using System.Diagnostics;
using Glide.Core.Index;
using Glide.Core.Index.Usn;

namespace Glide.Service;

/// <summary>
/// Keeps one index file per fixed NTFS volume up to date.
///
/// v0.4.0 rebuilds the whole file when the journal moves instead of applying the
/// change records one by one: a rebuild is an enumeration of the journal plus a
/// single sequential write, which measured 83 ms for a million entries, and it
/// cannot drift. Incremental updates are the next step.
/// </summary>
internal sealed class IndexWorker
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    public static string IndexDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Glide");

    public static string IndexPathFor(string letter) =>
        Path.Combine(IndexDirectory, $"index-{letter.ToLowerInvariant()}.gidx");

    public void Run(CancellationToken token)
    {
        Directory.CreateDirectory(IndexDirectory);
        Log.Write($"starting; index directory {IndexDirectory}");

        var volumes = new List<VolumeState>();

        foreach (string letter in FixedNtfsVolumes())
        {
            try
            {
                volumes.Add(new VolumeState(letter, UsnVolume.Open(letter)));
            }
            catch (Exception ex)
            {
                Log.Write($"volume {letter}: cannot read the USN journal: {ex.Message}");
            }
        }

        if (volumes.Count == 0)
        {
            Log.Write("no volume could be opened; the service has nothing to index");
            return;
        }

        try
        {
            foreach (VolumeState volume in volumes)
            {
                Rebuild(volume);
            }

            while (!token.IsCancellationRequested)
            {
                if (token.WaitHandle.WaitOne(PollInterval))
                {
                    break;
                }

                foreach (VolumeState volume in volumes)
                {
                    Poll(volume, token);
                }
            }
        }
        finally
        {
            foreach (VolumeState volume in volumes)
            {
                volume.Volume.Dispose();
            }

            Log.Write("stopped");
        }
    }

    private static void Poll(VolumeState volume, CancellationToken token)
    {
        if (Stopwatch.GetTimestamp() < volume.NextAttempt)
        {
            return;
        }

        try
        {
            UsnJournalState next = volume.Volume.ReadChanges(volume.Journal);
            if (next.NextUsn > volume.Journal.NextUsn)
            {
                volume.Journal = next;
                volume.LastChange = Stopwatch.GetTimestamp();
                Log.Write($"{volume.Letter}: journal moved to USN {next.NextUsn}");
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"{volume.Letter}: reading the journal failed: {ex.Message}");
            volume.LastChange ??= Stopwatch.GetTimestamp();
            volume.NextAttempt = Stopwatch.GetTimestamp() + ToTicks(RetryDelay);
            return;
        }

        if (volume.LastChange is not long last || token.IsCancellationRequested)
        {
            return;
        }

        if (Stopwatch.GetElapsedTime(last) < QuietPeriod)
        {
            return;
        }

        Rebuild(volume);
    }

    private static void Rebuild(VolumeState volume)
    {
        volume.LastChange = null;

        string indexPath = IndexPathFor(volume.Letter);

        try
        {
            long start = Stopwatch.GetTimestamp();
            IndexBuildResult result = IndexBuilder.Build(volume.Volume, indexPath);
            volume.Journal = result.Journal;

            Log.Write(
                $"{volume.Letter}: indexed {result.IndexedEntries} entries " +
                $"({result.EnumeratedRecords} journal records, {result.ResolvedCandidates} resolved) " +
                $"in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:0} ms");
        }
        catch (Exception ex)
        {
            volume.NextAttempt = Stopwatch.GetTimestamp() + ToTicks(RetryDelay);
            volume.LastChange = Stopwatch.GetTimestamp();
            Log.Write($"{volume.Letter}: rebuild failed: {ex}");
        }
    }

    private static long ToTicks(TimeSpan span)
    {
        return (long)(span.TotalSeconds * Stopwatch.Frequency);
    }

    private static IEnumerable<string> FixedNtfsVolumes()
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? letter = drive.Name.TrimEnd(':', '\\');
            if (letter.Length == 1)
            {
                yield return letter;
            }
        }
    }

    private sealed class VolumeState
    {
        internal VolumeState(string letter, UsnVolume volume)
        {
            Letter = letter;
            Volume = volume;
        }

        internal string Letter { get; }

        internal UsnVolume Volume { get; }

        internal UsnJournalState Journal { get; set; }

        internal long? LastChange { get; set; }

        internal long NextAttempt { get; set; }
    }
}
