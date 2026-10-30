using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glide.Core;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string[]))]
public partial class BookmarksJsonContext : JsonSerializerContext;

/// <summary>
/// The bookmark list is a JSON array on disk and an array in memory. The query
/// path reads it on every keystroke, so it is loaded once and cached instead of
/// parsing the file again for every character typed.
/// </summary>
public sealed class BookmarkStore
{
    public const int MaxEntries = 50;

    private readonly object _gate = new();
    private readonly string _filePath;
    private string[]? _items;

    public BookmarkStore(string filePath) => _filePath = filePath;

    public static BookmarkStore Default { get; } = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Glide",
            "bookmarks.json"));

    public string FilePath => _filePath;

    /// <summary>Most recent first. Do not mutate the returned array.</summary>
    public string[] Items
    {
        get
        {
            lock (_gate)
            {
                return _items ??= Read();
            }
        }
    }

    public bool Contains(string path)
    {
        foreach (string item in Items)
        {
            if (Matches(item, path))
            {
                return true;
            }
        }

        return false;
    }

    public void Add(string path)
    {
        lock (_gate)
        {
            string[] current = _items ??= Read();
            var next = new List<string>(Math.Min(current.Length + 1, MaxEntries)) { path };

            foreach (string item in current)
            {
                if (next.Count == MaxEntries)
                {
                    break;
                }

                if (!Matches(item, path))
                {
                    next.Add(item);
                }
            }

            Save(next.ToArray());
        }
    }

    public void Remove(string path)
    {
        lock (_gate)
        {
            string[] current = _items ??= Read();
            var next = new List<string>(current.Length);

            foreach (string item in current)
            {
                if (!Matches(item, path))
                {
                    next.Add(item);
                }
            }

            Save(next.ToArray());
        }
    }

    private void Save(string[] items)
    {
        _items = items;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(items, BookmarksJsonContext.Default.StringArray));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Glide: error saving bookmarks: {ex.Message}");
        }
    }

    private string[] Read()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }

            string json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize(json, BookmarksJsonContext.Default.StringArray) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Glide: error loading bookmarks: {ex.Message}");
            return [];
        }
    }

    private static bool Matches(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
