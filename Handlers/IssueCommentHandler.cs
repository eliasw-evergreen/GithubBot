using System.Text.Json;
using Discord;
using GithubBot.Discord;
using GithubBot.Models;
using GithubBot.Services;

namespace GithubBot.Handlers;

public class IssueCommentHandler : IGitHubEventHandler
{
    public string EventType => "issue_comment";

    private readonly DiscordBotService _discord;
    private readonly PrMapService _prMap;
    private readonly CommentMapService _commentMap;
    private readonly UserMapService _userMap;
    private readonly PreferencesService _prefs;
    private readonly ScoreService _scores;
    private readonly RouletteService _roulette;
    private readonly IConfiguration _config;
    private readonly ILogger<IssueCommentHandler> _logger;

    public IssueCommentHandler(
        DiscordBotService discord,
        PrMapService prMap,
        CommentMapService commentMap,
        UserMapService userMap,
        PreferencesService prefs,
        ScoreService scores,
        RouletteService roulette,
        IConfiguration config,
        ILogger<IssueCommentHandler> logger)
    {
        _discord = discord;
        _prMap = prMap;
        _commentMap = commentMap;
        _userMap = userMap;
        _prefs = prefs;
        _scores = scores;
        _roulette = roulette;
        _config = config;
        _logger = logger;
    }

    public async Task HandleAsync(string action, JsonElement payload, CancellationToken ct = default)
    {
        if (action != "created" && action != "edited" && action != "deleted") return;

        var issue = payload.GetProperty("issue");
        if (!issue.TryGetProperty("pull_request", out _)) return;

        var comment = payload.GetProperty("comment").Deserialize<IssueComment>()!;
        var pr = issue.Deserialize<PullRequest>()!;
        var repo = payload.GetProperty("repository").Deserialize<Repository>()!;

        // issue.html_url is the /issues/ URL; get the real PR URL from the pull_request link object
        var prHtmlUrl = issue.TryGetProperty("pull_request", out var prLink) &&
                        prLink.TryGetProperty("html_url", out var urlEl)
            ? urlEl.GetString() ?? pr.HtmlUrl
            : pr.HtmlUrl;

        var channel = await _discord.GetChannelAsync(ct);
        if (channel == null) return;

        // issue.node_id is the issue node ID, not the PR node ID — fall back to lookup by PR number
        var stored = _prMap.Get(pr.NodeId) ?? _prMap.GetByPrNumber(pr.Number);
        var target = await _discord.ResolveOrCreatePrThreadAsync(channel, stored, _prMap, pr.NodeId, pr.Number, pr.Title, prHtmlUrl, ct);
        var commentReaction = _prefs.ResolveReaction("comment", _config["Reactions:Comment"]);

        var embed = EmbedBuilders.CommentEmbed(comment, pr, repo, false, _userMap, commentReaction);
        if (await CommentHandlerHelper.HandleCommentDeleteOrEdit(action, comment.Id, embed, _discord, _commentMap))
            return;

        var mentionedPings = _userMap.ExtractDiscordPings(comment.Body);

        var msg = await _discord.SendMessageAsync(target.Id, string.Join(' ', mentionedPings), embed, ct);
        if (msg != null)
        {
            _commentMap.Set(comment.Id, new CommentMapEntry { MessageId = msg.Id, ChannelId = target.Id });
            if (_userMap.GitHubToDiscord(comment.User.Login) is string commenterId)
            {
                _scores.Award(commenterId, ScoreCategory.Comment);
                if (_roulette.TryCollect(pr.NodeId, commenterId))
                    _scores.AwardBonus(commenterId, _scores.PointsComment);
            }
        }

        var storedForReaction = _prMap.Get(pr.NodeId) ?? _prMap.GetByPrNumber(pr.Number);
        if (storedForReaction != null && !string.IsNullOrEmpty(commentReaction))
            await _discord.AddReactionAsync(channel.Id, storedForReaction.MessageId, commentReaction, ct);
    }

}
