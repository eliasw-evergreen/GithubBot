using System.Text.Json;

namespace GithubBot.Services;

public class PreferencesService
{
    private readonly string _filePath;
    private Dictionary<string, string> _reactions = [];

    public PreferencesService(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    private void Load()
    {
        try
        {
            var json = File.ReadAllText(_filePath);
            var data = JsonSerializer.Deserialize<PreferencesData>(json);
            _reactions = data?.Reactions ?? [];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _reactions = [];
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PreferencesService] Failed to load {_filePath}: {ex.Message}");
            _reactions = [];
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(new PreferencesData { Reactions = _reactions },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    public string? GetReaction(string eventKey)
    {
        _reactions.TryGetValue(eventKey, out var reaction);
        return string.IsNullOrEmpty(reaction) ? null : reaction;
    }

    public void SetReaction(string eventKey, string reaction)
    {
        _reactions[eventKey] = reaction;
        Save();
    }

    public void ClearReaction(string eventKey)
    {
        _reactions.Remove(eventKey);
        Save();
    }

    public string? ResolveReaction(string eventKey, string? envDefault)
    {
        var pref = GetReaction(eventKey);
        if (pref != null) return pref;
        return string.IsNullOrEmpty(envDefault) ? null : envDefault;
    }

    private class PreferencesData
    {
        public Dictionary<string, string> Reactions { get; set; } = [];
    }
}
