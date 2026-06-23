using System.Text.Json;

namespace GithubBot.Services;

public class UserPreferences
{
    public Dictionary<string, string> Reactions { get; set; } = [];
}

public class PreferencesService
{
    private readonly string _filePath;
    private Dictionary<string, UserPreferences> _prefs = [];

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
            _prefs = JsonSerializer.Deserialize<Dictionary<string, UserPreferences>>(json) ?? [];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _prefs = [];
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PreferencesService] Failed to load {_filePath}: {ex.Message}");
            _prefs = [];
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_prefs, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    public string? GetReaction(string discordId, string eventKey)
    {
        if (!_prefs.TryGetValue(discordId, out var prefs)) return null;
        prefs.Reactions.TryGetValue(eventKey, out var reaction);
        return string.IsNullOrEmpty(reaction) ? null : reaction;
    }

    public void SetReaction(string discordId, string eventKey, string reaction)
    {
        if (!_prefs.TryGetValue(discordId, out var prefs))
            _prefs[discordId] = prefs = new UserPreferences();
        prefs.Reactions[eventKey] = reaction;
        Save();
    }

    public void ClearReaction(string discordId, string eventKey)
    {
        if (!_prefs.TryGetValue(discordId, out var prefs)) return;
        prefs.Reactions.Remove(eventKey);
        if (prefs.Reactions.Count == 0)
            _prefs.Remove(discordId);
        Save();
    }

    public UserPreferences? GetAll(string discordId)
    {
        _prefs.TryGetValue(discordId, out var prefs);
        return prefs;
    }
}
