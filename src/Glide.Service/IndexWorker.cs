using System.Diagnostics;
using Glide.Core.Index;
using Glide.Core.Index.Usn;
using Glide.Core.Snapshot;

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
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MinRebuildInterval = TimeSpan.FromSeconds(10);
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

            MemoryTrimmer.Trim();

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
        if (token.IsCancellationRequested)
        {
            return;
        }

        if (Stopwatch.GetTimestamp() < volume.NextAttempt)
        {
            return;
        }

        if (volume.JournalBroken)
        {
            // The last read failed (journal recreated, or the position was
            // purged). Reading again from the same position fails the same way;
            // only a rebuild re-anchors the journal state.
            volume.JournalBroken = false;
            Rebuild(volume);
            return;
        }

        try
        {
            // Only creates, deletes and renames are requested, so data writes to
            // logs and databases never reach this process. The filter then drops
            // records from quiet folders, including this service's own index
            // and log writes, which would otherwise schedule the next rebuild.
            bool relevant = false;
            ChangeFilter filter = volume.Changes;
            volume.Journal = volume.Volume.ReadChanges(
                volume.Journal,
                UsnVolume.NameChangeReasons,
                volume.Buffer,
                entry => relevant |= filter.IsRelevant(entry));

            if (relevant)
            {
                long now = Stopwatch.GetTimestamp();
                volume.FirstChange ??= now;
                volume.LastChange = now;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"{volume.Letter}: reading the journal failed: {ex.Message}");
            volume.JournalBroken = true;
            volume.NextAttempt = Stopwatch.GetTimestamp() + ToTicks(RetryDelay);
            return;
        }

        if (volume.LastChange is not long lastChange || volume.FirstChange is not long firstChange)
        {
            return;
        }

        // Throttle rebuilds: avoid back-to-back rebuilds
        if (volume.LastRebuild > 0 && Stopwatch.GetElapsedTime(volume.LastRebuild) < MinRebuildInterval)
        {
            return;
        }

        // Trigger if quiet period reached, OR if max delay elapsed (prevents starvation on busy drives)
        bool quietReached = Stopwatch.GetElapsedTime(lastChange) >= QuietPeriod;
        bool maxDelayReached = Stopwatch.GetElapsedTime(firstChange) >= MaxDelay;

        if (!quietReached && !maxDelayReached)
        {
            return;
        }

        Rebuild(volume);
    }

    private static void Rebuild(VolumeState volume)
    {
        volume.FirstChange = null;
        volume.LastChange = null;
        volume.LastRebuild = Stopwatch.GetTimestamp();

        string indexPath = IndexPathFor(volume.Letter);

        try
        {
            long start = Stopwatch.GetTimestamp();
            IndexBuildResult result = IndexBuilder.Build(volume.Volume, indexPath, quietIndexDirectory: IndexDirectory);
            volume.Journal = result.Journal;
            volume.Changes = result.Changes;

            if (result.IndexedEntries == 0)
            {
                Log.Write($"{volume.Letter}: USN journal returned 0 entries; falling back to full directory scan...");
                int scannedEntries = IndexWriter.Write(indexPath, DirectoryScanner.Enumerate(volume.Root, 200_000));
                Log.Write(
                    $"{volume.Letter}: indexed {scannedEntries} entries (full scan) " +
                    $"in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:0} ms");
            }
            else
            {
                Log.Write(
                    $"{volume.Letter}: indexed {result.IndexedEntries} entries " +
                    $"({result.EnumeratedRecords} journal records, {result.ResolvedCandidates} directories, " +
                    $"{result.Changes.QuietCount} quiet) " +
                    $"in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:0} ms");
            }
        }
        catch (Exception ex)
        {
            volume.NextAttempt = Stopwatch.GetTimestamp() + ToTicks(RetryDelay);
            volume.LastChange = Stopwatch.GetTimestamp();
            Log.Write($"{volume.Letter}: rebuild failed: {ex}");
        }
        finally
        {
            MemoryTrimmer.Trim();
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
            Root = volume.Root;
        }

        internal string Letter { get; }

        internal string Root { get; }

        internal UsnVolume Volume { get; }

        internal UsnJournalState Journal { get; set; }

        internal long? FirstChange { get; set; }

        internal long? LastChange { get; set; }

        internal long LastRebuild { get; set; }

        internal long NextAttempt { get; set; }

        internal bool JournalBroken { get; set; }

        internal ChangeFilter Changes { get; set; } = ChangeFilter.Empty;

        /// <summary>Reused by every poll so the loop allocates nothing per tick.</summary>
        internal byte[] Buffer { get; } = new byte[UsnVolume.ChangeBufferSize];
    }
}
