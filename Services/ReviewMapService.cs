using System.Text.Json;

namespace GithubBot.Services;

public class ReviewMapEntry
{
    public ulong MessageId { get; set; }
    public ulong ChannelId { get; set; }
}

public class ReviewMapService
{
    private readonly string _filePath;
    private Dictionary<string, ReviewMapEntry> _map = [];

    public ReviewMapService(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    private void Load()
    {
        try
        {
            var json = File.ReadAllText(_filePath);
            _map = JsonSerializer.Deserialize<Dictionary<string, ReviewMapEntry>>(json) ?? [];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _map = [];
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ReviewMapService] Failed to load {_filePath}: {ex.Message}");
            _map = [];
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    public ReviewMapEntry? Get(long reviewId) =>
        _map.TryGetValue(reviewId.ToString(), out var entry) ? entry : null;

    public void Set(long reviewId, ReviewMapEntry entry)
    {
        _map[reviewId.ToString()] = entry;
        Save();
    }

    public void Remove(long reviewId)
    {
        _map.Remove(reviewId.ToString());
        Save();
    }
}
