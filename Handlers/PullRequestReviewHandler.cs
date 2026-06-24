using System.Text.Json;
using GithubBot.Discord;
using GithubBot.Models;
using GithubBot.Services;

namespace GithubBot.Handlers;

public class PullRequestReviewHandler : IGitHubEventHandler
{
    public string EventType => "pull_request_review";

    private readonly DiscordBotService _discord;
    private readonly PrMapService _prMap;
    private readonly ReviewMapService _reviewMap;
    private readonly UserMapService _userMap;
    private readonly PreferencesService _prefs;
    private readonly ScoreService _scores;
    private readonly RouletteService _roulette;
    private readonly IConfiguration _config;
    private readonly ILogger<PullRequestReviewHandler> _logger;

    public PullRequestReviewHandler(
        DiscordBotService discord,
        PrMapService prMap,
        ReviewMapService reviewMap,
        UserMapService userMap,
        PreferencesService prefs,
        ScoreService scores,
        RouletteService roulette,
        IConfiguration config,
        ILogger<PullRequestReviewHandler> logger)
    {
        _discord = discord;
        _prMap = prMap;
        _reviewMap = reviewMap;
        _userMap = userMap;
        _prefs = prefs;
        _scores = scores;
        _roulette = roulette;
        _config = config;
        _logger = logger;
    }

    public async Task HandleAsync(string action, JsonElement payload, CancellationToken ct = default)
    {
        if (action != "submitted" && action != "edited") return;

        var review = payload.GetProperty("review").Deserialize<Review>()!;
        var pr = payload.GetProperty("pull_request").Deserialize<PullRequest>()!;
        var repo = payload.GetProperty("repository").Deserialize<Repository>()!;

        // A "commented" review with no body means the user only left inline comments;
        // those are handled by PullRequestReviewCommentHandler — skip to avoid double-posting.
        if (action == "submitted" && review.State == "commented" && string.IsNullOrWhiteSpace(review.Body))
            return;

        var embed = EmbedBuilders.ReviewSubmittedEmbed(review, pr, repo, _userMap,
            approvedReaction: _prefs.ResolveReaction("approved", _config["Reactions:Approved"]),
            changesReaction:  _prefs.ResolveReaction("changes_requested", _config["Reactions:ChangesRequested"]));

        // Edit in-place if we have a record of this review
        if (action == "edited")
        {
            var existing = _reviewMap.Get(review.Id);
            if (existing != null)
            {
                await _discord.EditMessageAsync(existing.ChannelId, existing.MessageId, null, embed);
                return;
            }
            // Fall through to post as new if not tracked
        }

        var pings = new List<string>();
        if (review.State == "changes_requested" || review.State == "approved")
        {
            var authorPing = _userMap.GitHubToDiscord(pr.User.Login);
            if (authorPing != null)
                pings.Add($"<@{authorPing}>");
        }

        var channel = await _discord.GetChannelAsync(ct);
        if (channel == null) return;

        var stored = _prMap.Get(pr.NodeId);
        var target = await _discord.ResolveOrCreatePrThreadAsync(channel, stored, _prMap, pr.NodeId, pr.Number, pr.Title, pr.HtmlUrl, ct);
        var msg = await _discord.SendMessageAsync(target.Id, pings.Count > 0 ? string.Join(' ', pings) : null, embed, ct);
        if (msg != null)
            _reviewMap.Set(review.Id, new ReviewMapEntry { MessageId = msg.Id, ChannelId = target.Id });

        if (action == "submitted" && _userMap.GitHubToDiscord(review.User.Login) is string reviewerId)
        {
            _scores.Award(reviewerId, ScoreCategory.ReviewSubmitted);
            if (_roulette.TryCollect(pr.NodeId, reviewerId))
                _scores.AwardBonus(reviewerId, ScoreService.PointsReview);
        }

        if (stored != null)
        {
            var reaction = review.State switch
            {
                "changes_requested" => _prefs.ResolveReaction("changes_requested", _config["Reactions:ChangesRequested"]),
                "approved"          => _prefs.ResolveReaction("approved", _config["Reactions:Approved"]),
                _                   => null,
            };
            if (!string.IsNullOrEmpty(reaction))
                await _discord.AddReactionAsync(channel.Id, stored.MessageId, reaction, ct);
        }
    }
}
