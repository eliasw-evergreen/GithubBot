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

        var channel = await _discord.GetChannelAsync(ct);
        if (channel == null) return;

        var stored = _prMap.Get(pr.NodeId);
        var target = await _discord.GetTargetChannel(channel, stored, ct);
        var commentReaction = _prefs.ResolveReaction("comment", _config["Reactions:Comment"]);

        if (action == "deleted")
        {
            var entry = _commentMap.Get(comment.Id);
            if (entry != null)
            {
                var original = await _discord.GetMessageAsync(entry.ChannelId, entry.MessageId);
                if (original?.Embeds.FirstOrDefault() is IEmbed existingEmbed)
                    await _discord.EditMessageAsync(entry.ChannelId, entry.MessageId, null,
                        EmbedBuilders.MarkCommentDeleted(existingEmbed));
                _commentMap.Remove(comment.Id);
            }
            return;
        }

        var embed = EmbedBuilders.CommentEmbed(comment, pr, repo, false, _userMap, commentReaction);
        var mentionedPings = ExtractGithubMentions(comment.Body);

        if (action == "edited")
        {
            var entry = _commentMap.Get(comment.Id);
            if (entry != null)
            {
                await _discord.EditMessageAsync(entry.ChannelId, entry.MessageId, null, embed);
                return;
            }
            // Fall through to post as new if we don't have a record
        }

        var msg = await _discord.SendMessageAsync(target.Id, string.Join(' ', mentionedPings), embed, ct);
        if (msg != null)
        {
            _commentMap.Set(comment.Id, new CommentMapEntry { MessageId = msg.Id, ChannelId = target.Id });
            if (_userMap.GitHubToDiscord(comment.User.Login) is string commenterId)
            {
                _scores.Award(commenterId, ScoreCategory.Comment);
                if (_roulette.TryCollect(pr.NodeId, commenterId))
                    _scores.AwardBonus(commenterId, ScoreService.PointsComment);
            }
        }

        if (stored != null && !string.IsNullOrEmpty(commentReaction))
            await _discord.AddReactionAsync(channel.Id, stored.MessageId, commentReaction, ct);
    }

    private List<string> ExtractGithubMentions(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var pings = new List<string>();
        var matches = System.Text.RegularExpressions.Regex.Matches(text, @"@([a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?)");
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var discordId = _userMap.GitHubToDiscord(match.Groups[1].Value);
            if (discordId != null)
            {
                var ping = $"<@{discordId}>";
                if (!pings.Contains(ping))
                    pings.Add(ping);
            }
        }
        return pings;
    }
}
