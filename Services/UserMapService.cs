using System.Text.Json;

namespace GithubBot.Services;

public class UserMapService
{
    private readonly string _filePath;
    private Dictionary<string, List<string>>? _cache;

    public UserMapService(string filePath)
    {
        _filePath = filePath;
    }

    private Dictionary<string, List<string>> Load()
    {
        if (_cache != null) return _cache;
        try
        {
            var json = File.ReadAllText(_filePath);
            _cache = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json) ?? [];
        }
        catch
        {
            _cache = [];
        }
        return _cache;
    }

    public void Save(Dictionary<string, List<string>> map)
    {
        _cache = map;
        var json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    public string? GitHubToDiscord(string githubLogin)
    {
        var login = githubLogin.ToLowerInvariant();
        var map = Load();
        foreach (var (discordId, usernames) in map)
        {
            if (usernames.Any(n => n.Equals(login, StringComparison.OrdinalIgnoreCase)))
                return discordId;
        }
        return null;
    }

    public Dictionary<string, List<string>> GetAll() => Load();
}
