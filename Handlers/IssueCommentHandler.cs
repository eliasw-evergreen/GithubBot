using System.Text.Json;
using GithubBot.Discord;
using GithubBot.Models;
using GithubBot.Services;

namespace GithubBot.Handlers;

public class IssueCommentHandler : IGitHubEventHandler
{
    public string EventType => "issue_comment";

    private readonly DiscordBotService _discord;
    private readonly PrMapService _prMap;
    private readonly UserMapService _userMap;
    private readonly PreferencesService _prefs;
    private readonly IConfiguration _config;
    private readonly ILogger<IssueCommentHandler> _logger;

    public IssueCommentHandler(
        DiscordBotService discord,
        PrMapService prMap,
        UserMapService userMap,
        PreferencesService prefs,
        IConfiguration config,
        ILogger<IssueCommentHandler> logger)
    {
        _discord = discord;
        _prMap = prMap;
        _userMap = userMap;
        _prefs = prefs;
        _config = config;
        _logger = logger;
    }

    public async Task HandleAsync(string action, JsonElement payload, CancellationToken ct = default)
    {
        if (action != "created" && action != "deleted") return;

        var issue = payload.GetProperty("issue");
        if (!issue.TryGetProperty("pull_request", out _)) return;

        var comment = payload.GetProperty("comment").Deserialize<IssueComment>()!;
        var pr = issue.Deserialize<PullRequest>()!;
        var repo = payload.GetProperty("repository").Deserialize<Repository>()!;
        var isDeleted = action == "deleted";

        var commentReaction = _prefs.ResolveReaction("comment", _config["Reactions:Comment"]);
        var embed = isDeleted
            ? EmbedBuilders.DeletedCommentEmbed(comment, pr, repo, false, _userMap, commentReaction)
            : EmbedBuilders.CommentEmbed(comment, pr, repo, false, _userMap, commentReaction);

        var mentionedPings = ExtractGithubMentions(comment.Body);

        var channel = await _discord.GetChannelAsync(ct);
        if (channel == null) return;

        var stored = _prMap.Get(pr.NodeId);
        var target = await _discord.GetTargetChannel(channel, stored, ct);
        await _discord.SendMessageAsync(target.Id, string.Join(' ', mentionedPings), embed, ct);

        if (stored != null && !isDeleted)
        {
            var reaction = _prefs.ResolveReaction("comment", _config["Reactions:Comment"]);
            if (!string.IsNullOrEmpty(reaction))
                await _discord.AddReactionAsync(channel.Id, stored.MessageId, reaction, ct);
        }
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
