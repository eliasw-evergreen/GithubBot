using System.Text.Json;

namespace GithubBot.Services;

public class CommentMapEntry
{
    public ulong MessageId { get; set; }
    public ulong ChannelId { get; set; }
}

public class CommentMapService
{
    private readonly string _filePath;
    private Dictionary<string, CommentMapEntry> _map = [];

    public CommentMapService(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    private void Load()
    {
        try
        {
            var json = File.ReadAllText(_filePath);
            _map = JsonSerializer.Deserialize<Dictionary<string, CommentMapEntry>>(json) ?? [];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _map = [];
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CommentMapService] Failed to load {_filePath}: {ex.Message}");
            _map = [];
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    public CommentMapEntry? Get(long commentId) =>
        _map.TryGetValue(commentId.ToString(), out var entry) ? entry : null;

    public void Set(long commentId, CommentMapEntry entry)
    {
        _map[commentId.ToString()] = entry;
        Save();
    }

    public void Remove(long commentId)
    {
        _map.Remove(commentId.ToString());
        Save();
    }
}
