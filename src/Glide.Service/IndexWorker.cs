using System.Diagnostics;
using Glide.Core.Index;
using Glide.Core.Index.Usn;
using Glide.Core.Snapshot;

namespace Glide.Service;

/// <summary>
/// Keeps one index file per fixed NTFS volume up to date.
///
/// The journal is read every second. A file or folder that is created,
/// deleted or renamed goes into the volume's delta file right away, which
/// searches lay over the index, so it shows up within about a second. The
/// index itself is rebuilt only when the delta cannot say what changed (a
/// folder renamed or deleted moves everything below it, or a parent that can
/// no longer be found) or when the delta has grown large; the rebuild folds
/// the delta in and deletes it.
/// </summary>
internal sealed class IndexWorker
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private const int MaxDeltaEntries = 5000;
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
            // records from quiet folders, including this service's own index,
            // delta and log writes. What is left goes into the delta now.
            bool changed = false;
            ChangeFilter filter = volume.Changes;
            IndexDeltaRecorder recorder = volume.Recorder;
            volume.Journal = volume.Volume.ReadChanges(
                volume.Journal,
                UsnVolume.NameChangeReasons,
                volume.Buffer,
                entry =>
                {
                    if (filter.IsRelevant(entry))
                    {
                        changed |= recorder.Apply(entry);
                    }
                });

            if (changed)
            {
                SaveDelta(volume);
            }

            if (recorder.NeedsRebuild || recorder.Delta.Count > MaxDeltaEntries)
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

            // The new index holds everything up to the journal position the
            // build started from; changes after it are read again next poll.
            volume.Recorder.Reset();
            IndexDelta.Delete(IndexDelta.PathFor(indexPath));

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

    private static void SaveDelta(VolumeState volume)
    {
        try
        {
            volume.Recorder.Delta.Save(IndexDelta.PathFor(IndexPathFor(volume.Letter)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Searches keep the previous delta; a rebuild catches up.
            Log.Write($"{volume.Letter}: writing the delta failed: {ex.Message}");
            long now = Stopwatch.GetTimestamp();
            volume.FirstChange ??= now;
            volume.LastChange = now;
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
            Recorder = new IndexDeltaRecorder(id => volume.TryGetPathById(id, out string path) ? path : null);
        }

        internal IndexDeltaRecorder Recorder { get; }

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
