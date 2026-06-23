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
    private readonly UserMapService _userMap;
    private readonly ILogger<PullRequestReviewHandler> _logger;

    public PullRequestReviewHandler(
        DiscordBotService discord,
        PrMapService prMap,
        UserMapService userMap,
        ILogger<PullRequestReviewHandler> logger)
    {
        _discord = discord;
        _prMap = prMap;
        _userMap = userMap;
        _logger = logger;
    }

    public async Task HandleAsync(string action, JsonElement payload, CancellationToken ct = default)
    {
        if (action != "submitted") return;

        var review = payload.GetProperty("review").Deserialize<Review>()!;
        var pr = payload.GetProperty("pull_request").Deserialize<PullRequest>()!;
        var repo = payload.GetProperty("repository").Deserialize<Repository>()!;

        var embed = EmbedBuilders.ReviewSubmittedEmbed(review, pr, repo, _userMap);

        var pings = new List<string>();
        if (review.State == "changes_requested")
        {
            var authorPing = _userMap.GitHubToDiscord(pr.User.Login);
            if (authorPing != null)
                pings.Add($"<@{authorPing}>");
        }

        var channel = await _discord.GetChannelAsync(ct);
        if (channel == null) return;

        var stored = _prMap.Get(pr.NodeId);
        var target = await _discord.GetTargetChannel(channel, stored, ct);
        await _discord.SendMessageAsync(target.Id, pings.Count > 0 ? string.Join(' ', pings) : null, embed, ct);
    }
}
