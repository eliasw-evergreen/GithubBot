using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GithubBot.Services;

public class UserMapEntry
{
    [JsonPropertyName("gh")]
    public List<string> Gh { get; set; } = [];

    [JsonPropertyName("ado")]
    public List<string> Ado { get; set; } = [];
}

public class UserMapService
{
    private readonly string _filePath;
    private Dictionary<string, UserMapEntry>? _cache;

    public UserMapService(string filePath)
    {
        _filePath = filePath;
    }

    private Dictionary<string, UserMapEntry> Load()
    {
        if (_cache != null) return _cache;
        try
        {
            var json = File.ReadAllText(_filePath);
            using var doc = JsonDocument.Parse(json);

            var result = new Dictionary<string, UserMapEntry>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    // New format
                    result[prop.Name] = JsonSerializer.Deserialize<UserMapEntry>(prop.Value.GetRawText()) ?? new();
                }
                else if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    // Migrate from old flat list format
                    var entry = new UserMapEntry();
                    foreach (var el in prop.Value.EnumerateArray())
                    {
                        var s = el.GetString() ?? "";
                        if (s.StartsWith("ado-name:", StringComparison.OrdinalIgnoreCase))
                            entry.Ado.Add(s[9..]);
                        else if (s.StartsWith("ado:", StringComparison.OrdinalIgnoreCase))
                            entry.Ado.Add(s[4..]);
                        else if (!string.IsNullOrEmpty(s))
                            entry.Gh.Add(s);
                    }
                    result[prop.Name] = entry;
                }
            }
            _cache = result;
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

    public void Save(Dictionary<string, UserMapEntry> map)
    {
        _cache = map;
        var json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    public Dictionary<string, UserMapEntry> GetAll() => Load();

    public string? GitHubToDiscord(string githubLogin)
    {
        var login = githubLogin.ToLowerInvariant();
        foreach (var (discordId, entry) in Load())
            if (entry.Gh.Any(g => g.Equals(login, StringComparison.OrdinalIgnoreCase)))
                return discordId;
        return null;
    }

    public string? AdoToDiscord(string email)
    {
        foreach (var (discordId, entry) in Load())
            if (entry.Ado.Any(a => a.Contains('@') && a.Equals(email, StringComparison.OrdinalIgnoreCase)))
                return discordId;
        return null;
    }

    public string? AdoDisplayNameToDiscord(string displayName)
    {
        foreach (var (discordId, entry) in Load())
            if (entry.Ado.Any(a => !a.Contains('@') && a.Equals(displayName.Trim(), StringComparison.OrdinalIgnoreCase)))
                return discordId;
        return null;
    }

    public void RegisterAdoDisplayName(string displayName, string email)
    {
        var discordId = AdoToDiscord(email);
        if (discordId == null) return;
        var map = Load();
        if (!map.TryGetValue(discordId, out var entry)) return;
        var name = displayName.Trim();
        if (entry.Ado.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
        entry.Ado.Add(name);
        Save(map);
    }

    // Returns distinct "<@discordId>" pings for all @GitHub mentions in text that have a mapping
    public List<string> ExtractDiscordPings(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var pings = new List<string>();
        foreach (Match m in Regex.Matches(text, @"@([a-zA-Z0-9](?:[a-zA-Z0-9\-]*[a-zA-Z0-9])?)"))
        {
            var id = GitHubToDiscord(m.Groups[1].Value);
            if (id != null && !pings.Contains($"<@{id}>")) pings.Add($"<@{id}>");
        }
        return pings;
    }

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
