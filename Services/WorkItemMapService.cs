using System.Text.Json;

namespace GithubBot.Services;

public class WorkItemMapEntry
{
    public ulong MessageId { get; set; }
    public ulong? ThreadId { get; set; }
    public string? Title { get; set; }
    public string? WorkItemType { get; set; }
    public string? AssignedToEmail { get; set; }
}

public class WorkItemMapService
{
    private readonly string _filePath;
    private Dictionary<string, WorkItemMapEntry> _map = [];

    public WorkItemMapService(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    private void Load()
    {
        try
        {
            var json = File.ReadAllText(_filePath);
            _map = JsonSerializer.Deserialize<Dictionary<string, WorkItemMapEntry>>(json) ?? [];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _map = [];
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WorkItemMapService] Failed to load {_filePath}: {ex.Message}");
            _map = [];
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    public IReadOnlyDictionary<string, WorkItemMapEntry> GetAll() => _map;

    public WorkItemMapEntry? Get(int workItemId) =>
        _map.TryGetValue(workItemId.ToString(), out var entry) ? entry : null;

    public void Set(int workItemId, WorkItemMapEntry entry)
    {
        _map[workItemId.ToString()] = entry;
        Save();
    }

    public void Remove(int workItemId)
    {
        _map.Remove(workItemId.ToString());
        Save();
    }
}
