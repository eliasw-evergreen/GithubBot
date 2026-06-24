using System.Text.Json;

namespace GithubBot.Services;

public class RouletteEntry
{
    public List<string> Assignees { get; set; } = [];
    public List<string> Collected { get; set; } = [];
}

public class RouletteService
{
    private readonly string _filePath;
    private Dictionary<string, RouletteEntry> _data = [];

    public RouletteService(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    private void Load()
    {
        try
        {
            var json = File.ReadAllText(_filePath);
            _data = JsonSerializer.Deserialize<Dictionary<string, RouletteEntry>>(json) ?? [];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _data = [];
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RouletteService] Failed to load {_filePath}: {ex.Message}");
            _data = [];
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    public void Assign(string prNodeId, IEnumerable<string> discordIds)
    {
        if (!_data.TryGetValue(prNodeId, out var entry))
            entry = new RouletteEntry();
        foreach (var id in discordIds)
            if (!entry.Assignees.Contains(id))
                entry.Assignees.Add(id);
        _data[prNodeId] = entry;
        Save();
    }

    public bool IsAssigned(string prNodeId, string discordId)
    {
        return _data.TryGetValue(prNodeId, out var entry) && entry.Assignees.Contains(discordId);
    }

    /// <summary>
    /// Marks a user as having collected their bonus for a PR. Returns true if they were an
    /// unmatched assignee (first interaction), false if already collected or not assigned.
    /// </summary>
    public bool TryCollect(string prNodeId, string discordId)
    {
        if (!_data.TryGetValue(prNodeId, out var entry)) return false;
        if (!entry.Assignees.Contains(discordId)) return false;
        if (entry.Collected.Contains(discordId)) return false;
        entry.Collected.Add(discordId);
        Save();
        return true;
    }

    public RouletteEntry? Get(string prNodeId)
    {
        _data.TryGetValue(prNodeId, out var entry);
        return entry;
    }

    public void Remove(string prNodeId)
    {
        _data.Remove(prNodeId);
        Save();
    }
}
