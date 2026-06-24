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

    private string? _pingRole;

    private void Load()
    {
        try
        {
            var json = File.ReadAllText(_filePath);
            var data = JsonSerializer.Deserialize<PreferencesData>(json);
            _reactions = data?.Reactions ?? [];
            _pingRole = data?.PingRole;
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
        var json = JsonSerializer.Serialize(new PreferencesData { Reactions = _reactions, PingRole = _pingRole },
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

    public string? GetPingRole() => _pingRole;
    public void SetPingRole(string roleId) { _pingRole = StripRoleFormatting(roleId); Save(); }
    public void ClearPingRole() { _pingRole = null; Save(); }
    public string? ResolvePingRole(string? envDefault)
    {
        var raw = _pingRole ?? (string.IsNullOrEmpty(envDefault) ? null : envDefault);
        return raw == null ? null : StripRoleFormatting(raw);
    }

    // Extract just the numeric snowflake ID from any format: <@&123>, @&123, @123, or 123
    private static string StripRoleFormatting(string value)
        => new string(value.Where(char.IsDigit).ToArray());

    public string? ResolveReaction(string eventKey, string? envDefault)
    {
        var pref = GetReaction(eventKey);
        if (pref != null) return pref;
        return string.IsNullOrEmpty(envDefault) ? null : envDefault;
    }

    private class PreferencesData
    {
        public Dictionary<string, string> Reactions { get; set; } = [];
        public string? PingRole { get; set; }
    }
}
