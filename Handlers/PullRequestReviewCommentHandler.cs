using System.Text.Json;
using Discord;
using GithubBot.Discord;
using GithubBot.Models;
using GithubBot.Services;

namespace GithubBot.Handlers;

public class PullRequestReviewCommentHandler : IGitHubEventHandler
{
    public string EventType => "pull_request_review_comment";

    private readonly DiscordBotService _discord;
    private readonly PrMapService _prMap;
    private readonly CommentMapService _commentMap;
    private readonly UserMapService _userMap;
    private readonly PreferencesService _prefs;
    private readonly ScoreService _scores;
    private readonly IConfiguration _config;
    private readonly ILogger<PullRequestReviewCommentHandler> _logger;

    public PullRequestReviewCommentHandler(
        DiscordBotService discord,
        PrMapService prMap,
        CommentMapService commentMap,
        UserMapService userMap,
        PreferencesService prefs,
        ScoreService scores,
        IConfiguration config,
        ILogger<PullRequestReviewCommentHandler> logger)
    {
        _discord = discord;
        _prMap = prMap;
        _commentMap = commentMap;
        _userMap = userMap;
        _prefs = prefs;
        _scores = scores;
        _config = config;
        _logger = logger;
    }

    public async Task HandleAsync(string action, JsonElement payload, CancellationToken ct = default)
    {
        if (action != "created" && action != "edited" && action != "deleted") return;

        var comment = payload.GetProperty("comment").Deserialize<IssueComment>()!;
        var pr = payload.GetProperty("pull_request").Deserialize<PullRequest>()!;
        var repo = payload.GetProperty("repository").Deserialize<Repository>()!;

        var channel = await _discord.GetChannelAsync(ct);
        if (channel == null) return;

        var stored = _prMap.Get(pr.NodeId);
        var target = await _discord.ResolveOrCreatePrThreadAsync(channel, stored, _prMap, pr.NodeId, pr.Number, pr.Title, pr.HtmlUrl, ct);
        var commentReaction = _prefs.ResolveReaction("comment", _config["Reactions:Comment"]);

        var embed = EmbedBuilders.CommentEmbed(comment, pr, repo, true, _userMap, commentReaction);
        if (await CommentHandlerHelper.HandleCommentDeleteOrEdit(action, comment.Id, embed, _discord, _commentMap))
            return;

        var mentionedPings = _userMap.ExtractDiscordPings(comment.Body);

        ulong? replyTo = comment.InReplyToId.HasValue
            ? _commentMap.Get(comment.InReplyToId.Value)?.MessageId
            : null;

        var msg = await _discord.SendMessageAsync(target.Id, string.Join(' ', mentionedPings), embed, ct, replyToMessageId: replyTo);
        if (msg != null)
        {
            _commentMap.Set(comment.Id, new CommentMapEntry { MessageId = msg.Id, ChannelId = target.Id });
            if (_userMap.GitHubToDiscord(comment.User.Login) is string commenterId)
                _scores.Award(commenterId, ScoreCategory.Comment);
        }

        if (stored != null && !string.IsNullOrEmpty(commentReaction))
            await _discord.AddReactionAsync(channel.Id, stored.MessageId, commentReaction, ct);
    }

}
