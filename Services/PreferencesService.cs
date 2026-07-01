using System.Text.Json;

namespace GithubBot.Services;

public class PreferencesService
{
    private readonly string _filePath;
    private PreferencesData _data = new();

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
            _data = JsonSerializer.Deserialize<PreferencesData>(json) ?? new();
            _data.Reactions ??= [];
            _data.Channels ??= [];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _data = new();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PreferencesService] Failed to load {_filePath}: {ex.Message}");
            _data = new();
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    // ── Reactions ────────────────────────────────────────────────────────────

    public string? GetReaction(string eventKey)
    {
        _data.Reactions.TryGetValue(eventKey, out var reaction);
        return string.IsNullOrEmpty(reaction) ? null : reaction;
    }

    public void SetReaction(string eventKey, string reaction) { _data.Reactions[eventKey] = reaction; Save(); }
    public void ClearReaction(string eventKey) { _data.Reactions.Remove(eventKey); Save(); }

    public string? ResolveReaction(string eventKey, string? envDefault)
        => GetReaction(eventKey) ?? (string.IsNullOrEmpty(envDefault) ? null : envDefault);

    // ── Ping role ─────────────────────────────────────────────────────────────

    public string? GetPingRole() => _data.PingRole;
    public void SetPingRole(string roleId) { _data.PingRole = StripDigits(roleId); Save(); }
    public void ClearPingRole() { _data.PingRole = null; Save(); }
    public string? ResolvePingRole(string? envDefault)
    {
        var raw = _data.PingRole ?? (string.IsNullOrEmpty(envDefault) ? null : envDefault);
        return raw == null ? null : StripDigits(raw);
    }

    // ── Config role (configui) ────────────────────────────────────────────────

    public string? GetConfigRole() => _data.ConfigRole;
    public void SetConfigRole(string roleId) { _data.ConfigRole = StripDigits(roleId); Save(); }
    public void ClearConfigRole() { _data.ConfigRole = null; Save(); }
    public string? ResolveConfigRole(string? envDefault)
    {
        var raw = _data.ConfigRole ?? (string.IsNullOrEmpty(envDefault) ? null : envDefault);
        return raw == null ? null : StripDigits(raw);
    }

    // ── Command role (/score, /leaderboard, /prroulette) ──────────────────────

    public string? GetCommandRole() => _data.CommandRole;
    public void SetCommandRole(string roleId) { _data.CommandRole = StripDigits(roleId); Save(); }
    public void ClearCommandRole() { _data.CommandRole = null; Save(); }
    public string? ResolveCommandRole(string? envDefault)
    {
        var raw = _data.CommandRole ?? (string.IsNullOrEmpty(envDefault) ? null : envDefault);
        return raw == null ? null : StripDigits(raw);
    }

    // ── Channels ──────────────────────────────────────────────────────────────

    public string? GetChannel(string key)
    {
        _data.Channels.TryGetValue(key, out var v);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    public void SetChannel(string key, string channelId) { _data.Channels[key] = channelId; Save(); }
    public void ClearChannel(string key) { _data.Channels.Remove(key); Save(); }

    public string? ResolveChannel(string key, string? envDefault)
        => GetChannel(key) ?? (string.IsNullOrEmpty(envDefault) ? null : envDefault);

    // ── Roulette exclusions ───────────────────────────────────────────────────

    public bool IsRouletteExcluded(string discordId) => _data.RouletteExclusions.Contains(discordId);
    public IReadOnlySet<string> GetRouletteExclusions() => _data.RouletteExclusions;
    public void SetRouletteExclusion(string discordId, bool excluded)
    {
        if (excluded) _data.RouletteExclusions.Add(discordId);
        else _data.RouletteExclusions.Remove(discordId);
        Save();
    }

    // ── PR description max lines ──────────────────────────────────────────────

    public int? GetPrDescMaxLines() => _data.PrDescMaxLines;
    public void SetPrDescMaxLines(int? v) { _data.PrDescMaxLines = v; Save(); }
    public int ResolvePrDescMaxLines() => _data.PrDescMaxLines ?? 10;

    // ── Score point values ────────────────────────────────────────────────────

    public int? GetPointValue(string key)
        => _data.PointValues.TryGetValue(key, out var v) ? v : null;

    public void SetPointValue(string key, int value) { _data.PointValues[key] = value; Save(); }
    public void ClearPointValue(string key) { _data.PointValues.Remove(key); Save(); }

    public int ResolvePointValue(string key, int defaultValue)
        => _data.PointValues.TryGetValue(key, out var v) ? v : defaultValue;

    // ── Commands version ──────────────────────────────────────────────────────

    public string? GetCommandsVersion() => _data.CommandsVersion;
    public void SetCommandsVersion(string v) { _data.CommandsVersion = v; Save(); }

    private static string StripDigits(string value)
        => new string(value.Where(char.IsDigit).ToArray());

    // ── Work hours ────────────────────────────────────────────────────────────

    public string? GetWorkHoursStart() => _data.WorkHoursStart;
    public void SetWorkHoursStart(string v) { _data.WorkHoursStart = v; Save(); }
    public void ClearWorkHoursStart() { _data.WorkHoursStart = null; Save(); }

    public string? GetWorkHoursEnd() => _data.WorkHoursEnd;
    public void SetWorkHoursEnd(string v) { _data.WorkHoursEnd = v; Save(); }
    public void ClearWorkHoursEnd() { _data.WorkHoursEnd = null; Save(); }

    public string? GetWorkHoursTimezone() => _data.WorkHoursTimezone;
    public void SetWorkHoursTimezone(string v) { _data.WorkHoursTimezone = v; Save(); }
    public void ClearWorkHoursTimezone() { _data.WorkHoursTimezone = null; Save(); }

    public string? GetWorkHoursDays() => _data.WorkHoursDays;
    public void SetWorkHoursDays(string v) { _data.WorkHoursDays = v; Save(); }
    public void ClearWorkHoursDays() { _data.WorkHoursDays = null; Save(); }

    public WorkHoursConfig ResolveWorkHours(IConfiguration config) => new(
        Start:    _data.WorkHoursStart    ?? config["WorkHours:Start"],
        End:      _data.WorkHoursEnd      ?? config["WorkHours:End"],
        Timezone: _data.WorkHoursTimezone ?? config["WorkHours:Timezone"],
        Days:     _data.WorkHoursDays     ?? config["WorkHours:Days"]);

    private class PreferencesData
    {
        public Dictionary<string, string> Reactions { get; set; } = [];
        public string? PingRole { get; set; }
        public Dictionary<string, string> Channels { get; set; } = [];
        public string? CommandsVersion { get; set; }
        public HashSet<string> RouletteExclusions { get; set; } = [];
        public string? ConfigRole { get; set; }
        public string? CommandRole { get; set; }
        public int? PrDescMaxLines { get; set; }
        public Dictionary<string, int> PointValues { get; set; } = [];
        public string? WorkHoursStart    { get; set; }
        public string? WorkHoursEnd      { get; set; }
        public string? WorkHoursTimezone { get; set; }
        public string? WorkHoursDays     { get; set; }
    }
}

public record WorkHoursConfig(string? Start, string? End, string? Timezone, string? Days);
