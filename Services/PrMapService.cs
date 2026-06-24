using System.Text.Json;

namespace GithubBot.Services;

public class PrMapEntry
{
    public ulong MessageId { get; set; }
    public ulong? ThreadId { get; set; }
    public long? ClosedAt { get; set; }
    public int? PrNumber { get; set; }
    public string? PrTitle { get; set; }
}


public class PrMapService
{
    private readonly string _filePath;
    private Dictionary<string, PrMapEntry> _map = [];

    public PrMapService(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    private void Load()
    {
        try
        {
            var json = File.ReadAllText(_filePath);
            _map = JsonSerializer.Deserialize<Dictionary<string, PrMapEntry>>(json) ?? [];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _map = [];
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PrMapService] Failed to load {_filePath}: {ex.Message}");
            _map = [];
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    public PrMapEntry? Get(string nodeId)
    {
        _map.TryGetValue(nodeId, out var entry);
        return entry;
    }

    public void Set(string nodeId, PrMapEntry entry)
    {
        _map[nodeId] = entry;
        Save();
    }

    public void Remove(string nodeId)
    {
        _map.Remove(nodeId);
        Save();
    }

    public PrMapEntry? GetByPrNumber(int prNumber)
        => _map.Values.FirstOrDefault(e => e.PrNumber == prNumber);

    public IReadOnlyDictionary<string, PrMapEntry> GetAll() => _map;

    public void Prune(int days)
    {
        if (days <= 0) return;
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - days * 86400000L;
        var changed = false;
        var toRemove = new List<string>();
        foreach (var (nodeId, entry) in _map)
        {
            if (entry.ClosedAt.HasValue && entry.ClosedAt.Value < cutoff)
                toRemove.Add(nodeId);
        }
        foreach (var nodeId in toRemove)
        {
            _map.Remove(nodeId);
            changed = true;
        }
        if (changed) Save();
    }
}
