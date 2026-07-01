using System.Text.Json;

namespace GithubBot.Services;

public class DeferredThreadEntry
{
    public ulong ChannelId { get; set; }
    public HashSet<string> Pings { get; set; } = [];
    public List<string> Labels { get; set; } = [];
    public long CreatedAtMs { get; set; }
}

public class DeferredPingService
{
    private readonly string _filePath;
    private Dictionary<string, DeferredThreadEntry> _data = [];
    private readonly object _lock = new();

    public DeferredPingService(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    private void Load()
    {
        try
        {
            var json = File.ReadAllText(_filePath);
            _data = JsonSerializer.Deserialize<Dictionary<string, DeferredThreadEntry>>(json) ?? [];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _data = [];
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DeferredPingService] Failed to load {_filePath}: {ex.Message}");
            _data = [];
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    public void Defer(ulong channelId, IEnumerable<string> pings, string label)
    {
        lock (_lock)
        {
            var key = channelId.ToString();
            if (!_data.TryGetValue(key, out var entry))
            {
                entry = new DeferredThreadEntry
                {
                    ChannelId = channelId,
                    CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                _data[key] = entry;
            }
            foreach (var ping in pings)
                entry.Pings.Add(ping);
            if (!string.IsNullOrEmpty(label))
                entry.Labels.Add(label);
            Save();
        }
    }

    public IReadOnlyList<DeferredThreadEntry> GetPending()
    {
        lock (_lock) return _data.Values.ToList();
    }

    public void Clear()
    {
        lock (_lock) { _data.Clear(); Save(); }
    }

    public void Prune(int days)
    {
        lock (_lock)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();
            var stale = _data.Where(kv => kv.Value.CreatedAtMs < cutoff).Select(kv => kv.Key).ToList();
            foreach (var k in stale) _data.Remove(k);
            if (stale.Count > 0) Save();
        }
    }
}
