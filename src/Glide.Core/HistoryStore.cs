using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glide.Core;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string[]))]
public partial class HistoryJsonContext : JsonSerializerContext;

/// <summary>
/// Recently opened paths, most recent first. Like the bookmarks, the list is
/// cached in memory: it is read every time the query becomes empty.
/// </summary>
public sealed class HistoryStore
{
    public const int MaxEntries = 50;

    private readonly object _gate = new();
    private readonly string _filePath;
    private string[]? _items;

    public HistoryStore(string filePath) => _filePath = filePath;

    public static HistoryStore Default { get; } = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Glide",
            "history.json"));

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

                if (!string.Equals(item, path, StringComparison.OrdinalIgnoreCase))
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
            File.WriteAllText(_filePath, JsonSerializer.Serialize(items, HistoryJsonContext.Default.StringArray));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Glide: error saving history: {ex.Message}");
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
            return JsonSerializer.Deserialize(json, HistoryJsonContext.Default.StringArray) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Glide: error loading history: {ex.Message}");
            return [];
        }
    }
}
