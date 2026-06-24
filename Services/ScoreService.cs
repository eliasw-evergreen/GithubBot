using System.Text.Json;

namespace GithubBot.Services;

public class ScoreEntry
{
    public int Total          { get; set; }
    public int PrOpened       { get; set; }
    public int PrMerged       { get; set; }
    public int ReviewSubmitted { get; set; }
    public int Comments       { get; set; }
    public int TicketCreated  { get; set; }
    public int TicketResolved { get; set; }
    public int TicketComments { get; set; }
    public int Bonus          { get; set; }
}

public enum ScoreCategory { PrOpened, PrMerged, ReviewSubmitted, Comment, TicketCreated, TicketResolved, TicketComment }

public class ScoreService
{
    private readonly string _filePath;
    private Dictionary<string, ScoreEntry> _scores = [];

    public const int PointsPrOpened        = 10;
    public const int PointsPrMerged        = 15;
    public const int PointsReview          = 10;
    public const int PointsComment         = 5;
    public const int PointsTicketCreated   = 10;
    public const int PointsTicketBug       = 10;
    public const int PointsTicketStory     = 15;
    public const int PointsTicketComment   = 5;

    public ScoreService(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    private void Load()
    {
        try
        {
            var json = File.ReadAllText(_filePath);
            _scores = JsonSerializer.Deserialize<Dictionary<string, ScoreEntry>>(json) ?? [];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _scores = [];
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ScoreService] Failed to load {_filePath}: {ex.Message}");
            _scores = [];
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_scores, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    public void Award(string discordId, ScoreCategory category)
    {
        if (!_scores.TryGetValue(discordId, out var entry))
            entry = new ScoreEntry();

        var points = category switch
        {
            ScoreCategory.PrOpened        => PointsPrOpened,
            ScoreCategory.PrMerged        => PointsPrMerged,
            ScoreCategory.ReviewSubmitted => PointsReview,
            ScoreCategory.Comment         => PointsComment,
            ScoreCategory.TicketCreated   => PointsTicketCreated,
            ScoreCategory.TicketComment   => PointsTicketComment,
            _                             => 0,
        };

        entry.Total += points;
        switch (category)
        {
            case ScoreCategory.PrOpened:        entry.PrOpened        += points; break;
            case ScoreCategory.PrMerged:        entry.PrMerged        += points; break;
            case ScoreCategory.ReviewSubmitted: entry.ReviewSubmitted += points; break;
            case ScoreCategory.Comment:         entry.Comments        += points; break;
            case ScoreCategory.TicketCreated:   entry.TicketCreated   += points; break;
            case ScoreCategory.TicketComment:   entry.TicketComments  += points; break;
        }

        _scores[discordId] = entry;
        Save();
    }

    public ScoreEntry? GetScore(string discordId)
        => _scores.TryGetValue(discordId, out var e) ? e : null;

    public void SetScore(string discordId, ScoreEntry entry) { _scores[discordId] = entry; Save(); }
    public void ResetScore(string discordId) { _scores.Remove(discordId); Save(); }

    public IReadOnlyDictionary<string, ScoreEntry> GetAll() => _scores;

    public void AwardTicketResolved(string discordId, string? workItemType)
    {
        var points = workItemType switch
        {
            "User Story" => PointsTicketStory,
            _            => PointsTicketBug,
        };
        if (!_scores.TryGetValue(discordId, out var entry)) entry = new ScoreEntry();
        entry.Total          += points;
        entry.TicketResolved += points;
        _scores[discordId] = entry;
        Save();
    }

    public void AwardBonus(string discordId, int points)
    {
        if (!_scores.TryGetValue(discordId, out var entry))
            entry = new ScoreEntry();
        entry.Total += points;
        entry.Bonus += points;
        _scores[discordId] = entry;
        Save();
    }

    public IEnumerable<(string DiscordId, ScoreEntry Entry)> GetLeaderboard()
        => _scores
            .OrderByDescending(kvp => kvp.Value.Total)
            .Select(kvp => (kvp.Key, kvp.Value));
}
