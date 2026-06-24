using System.Text.Json;
using System.Text.RegularExpressions;

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
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _cache = [];
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[UserMapService] Failed to load {_filePath}: {ex.Message}");
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
        foreach (var (discordId, entries) in map)
        {
            if (entries.Any(n => !n.StartsWith("ado:") && n.Equals(login, StringComparison.OrdinalIgnoreCase)))
                return discordId;
        }
        return null;
    }

    public string? AdoToDiscord(string email)
    {
        var key = $"ado:{email.ToLowerInvariant()}";
        var map = Load();
        foreach (var (discordId, entries) in map)
        {
            if (entries.Any(n => n.Equals(key, StringComparison.OrdinalIgnoreCase)))
                return discordId;
        }
        return null;
    }

    public string? AdoGuidToDiscord(string guid)
    {
        var key = $"ado-guid:{guid.ToLowerInvariant()}";
        var map = Load();
        foreach (var (discordId, entries) in map)
        {
            if (entries.Any(n => n.Equals(key, StringComparison.OrdinalIgnoreCase)))
                return discordId;
        }
        return null;
    }

    // Auto-register an ADO GUID→Discord mapping derived from an already-resolved email
    public void RegisterAdoGuid(string guid, string discordId)
    {
        var key = $"ado-guid:{guid.ToLowerInvariant()}";
        var map = Load();
        if (!map.TryGetValue(discordId, out var entries)) return; // only for already-mapped users
        if (entries.Contains(key)) return;
        entries.Add(key);
        Save(map);
    }

    // Encode a raw value (email or GitHub username) into its storage form
    public static string Encode(string value)
        => value.Contains('@') ? $"ado:{value.ToLowerInvariant()}" : value;

    // Human-readable label for display in Discord
    public static string Label(string stored)
        => stored.StartsWith("ado:") ? $"`{stored[4..]}` (DevOps)" : $"**[{stored}](https://github.com/{stored})**";

    public Dictionary<string, List<string>> GetAll() => Load();

    // Returns distinct Discord IDs for all @mentions in text that have a mapping
    public IEnumerable<string> DiscordIdsFromMentions(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var matches = Regex.Matches(text, @"@([a-zA-Z0-9](?:[a-zA-Z0-9\-]*[a-zA-Z0-9])?)");
        return matches
            .Select(m => GitHubToDiscord(m.Groups[1].Value))
            .Where(id => id != null)
            .Select(id => id!)
            .Distinct();
    }
}
