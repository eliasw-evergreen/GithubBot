using System.Text.Json;
using GithubBot.Discord;
using GithubBot.Models;
using GithubBot.Services;

namespace GithubBot.Handlers;

public class PullRequestHandler : IGitHubEventHandler
{
    public string EventType => "pull_request";

    private readonly DiscordBotService _discord;
    private readonly PrMapService _prMap;
    private readonly UserMapService _userMap;
    private readonly PreferencesService _prefs;
    private readonly IConfiguration _config;
    private readonly ILogger<PullRequestHandler> _logger;

    public PullRequestHandler(
        DiscordBotService discord,
        PrMapService prMap,
        UserMapService userMap,
        PreferencesService prefs,
        IConfiguration config,
        ILogger<PullRequestHandler> logger)
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
        var pr = payload.GetProperty("pull_request").Deserialize<PullRequest>()!;
        var repo = payload.GetProperty("repository").Deserialize<Repository>()!;

        switch (action)
        {
            case "opened":
            case "reopened":
            case "ready_for_review":
            case "converted_to_draft":
                await HandleOpenActions(action, pr, repo, ct);
                break;

            case "closed":
                await HandleClosed(pr, repo, ct);
                break;

            case "review_requested":
                await HandleReviewRequested(pr, repo, payload, ct);
                break;

            case "assigned":
                await HandleAssigned(pr, repo, payload, ct);
                break;

            case "edited":
                await HandleEdited(pr, repo, ct);
                break;
        }
    }

    private async Task HandleOpenActions(string action, PullRequest pr, Repository repo, CancellationToken ct)
    {
        var embed = EmbedBuilders.PrEmbed(pr, repo, action, _userMap);
        var mention = _userMap.GitHubToDiscord(pr.User.Login) is string did ? $"<@{did}>" : $"**{pr.User.Login}**";
        var rolePing = _config["Roles:PrPing"];
        var rolePrefix = !string.IsNullOrEmpty(rolePing) ? $"<@&{rolePing}> " : "";

        var channel = await _discord.GetChannelAsync(ct);
        if (channel == null) return;

        var stored = _prMap.Get(pr.NodeId);

        if (action == "reopened" && stored != null)
        {
            if (stored.ClosedAt.HasValue)
            {
                stored.ClosedAt = null;
                _prMap.Save();
            }

            if (stored.MessageId != 0)
            {
                var originalMsg = await _discord.GetMessageAsync(channel.Id, stored.MessageId);
                if (originalMsg != null)
                {
                    await _discord.EditMessageAsync(channel.Id, stored.MessageId,
                        $"{rolePrefix}{mention} opened a PR in **{repo.Name}**", embed);

                    await _discord.ClearReactionsAsync(channel.Id, stored.MessageId,
                        _config["Reactions:Merged"], _config["Reactions:Closed"], ct);

                    if (stored.ThreadId == null || stored.ThreadId == 0)
                    {
                        var thread = await _discord.CreateThreadAsync(channel.Id, stored.MessageId,
                            $"PR #{pr.Number} — {pr.Title}", ct);
                        stored.ThreadId = thread;
                        _prMap.Save();
                    }
                }
            }

            var target = await _discord.GetTargetChannel(channel, stored, ct);
            var ping = !string.IsNullOrEmpty(rolePrefix) ? rolePrefix : null;
            await _discord.SendMessageAsync(target.Id, ping, embed, ct);
            return;
        }

        if ((action == "ready_for_review" || action == "converted_to_draft") && stored?.MessageId != 0)
        {
            var originalMsg = await _discord.GetMessageAsync(channel.Id, stored!.MessageId);
            if (originalMsg != null)
            {
                await _discord.EditMessageAsync(channel.Id, stored.MessageId, null, embed);
            }
            return;
        }

        var msg = await _discord.SendMessageAsync(channel.Id, $"{rolePrefix}{mention} opened a PR in **{repo.Name}**", embed, ct);
        if (msg != null)
        {
            var threadId = await _discord.CreateThreadAsync(channel.Id, msg.Id,
                $"PR #{pr.Number} — {pr.Title}", ct);
            _prMap.Set(pr.NodeId, new PrMapEntry { MessageId = msg.Id, ThreadId = threadId });
        }
    }

    private async Task HandleClosed(PullRequest pr, Repository repo, CancellationToken ct)
    {
        var merged = pr.Merged == true;
        var actionKey = merged ? "closed_merged" : "closed_unmerged";
        var embed = EmbedBuilders.PrEmbed(pr, repo, actionKey, _userMap);
        var channel = await _discord.GetChannelAsync(ct);
        if (channel == null) return;

        var stored = _prMap.Get(pr.NodeId);
        if (stored != null)
        {
            if (stored.MessageId != 0)
            {
                var originalMsg = await _discord.GetMessageAsync(channel.Id, stored.MessageId);
                if (originalMsg != null)
                {
                    var authorId = _userMap.GitHubToDiscord(pr.User.Login);
                    var reactionKey = merged ? "merged" : "closed";
                    var serverDefault = merged ? _config["Reactions:Merged"] : _config["Reactions:Closed"];
                    var reaction = _prefs.ResolveReaction(authorId, reactionKey, serverDefault);
                    if (!string.IsNullOrEmpty(reaction))
                        await _discord.AddReactionAsync(channel.Id, stored.MessageId, reaction, ct);

                    var cleanContent = System.Text.RegularExpressions.Regex.Replace(
                        originalMsg.Content, @"^\[(?:Closed|Merged)\] ", "");
                    var prefix = merged ? "[Merged] " : "[Closed] ";
                    await _discord.EditMessageAsync(channel.Id, stored.MessageId, $"{prefix}{cleanContent}", embed);
                }
            }

            if (stored.ThreadId != 0)
            {
                using var timeoutCt = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var thread = await _discord.GetThreadAsync(channel.Id, stored.ThreadId.Value, ct);
                if (thread != null)
                {
                    await _discord.SendMessageAsync(thread.Id, null, embed, ct);
                    await _discord.ArchiveThreadAsync(thread.Id, ct);
                }
            }

            stored.ClosedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _prMap.Save();
        }
        else
        {
            var msg = await _discord.SendMessageAsync(channel.Id, null, embed, ct);
            if (msg != null)
            {
                _prMap.Set(pr.NodeId, new PrMapEntry
                {
                    MessageId = msg.Id,
                    ThreadId = null,
                    ClosedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
            }
        }
    }

    private async Task HandleReviewRequested(PullRequest pr, Repository repo, JsonElement payload, CancellationToken ct)
    {
        var reviewers = pr.RequestedReviewers ?? [];
        if (reviewers.Count == 0) return;

        var sender = payload.GetProperty("sender").Deserialize<GitHubUser>()!;
        var embed = EmbedBuilders.ReviewRequestEmbed(pr, repo, reviewers, sender, _userMap);
        var pings = reviewers
            .Select(r => _userMap.GitHubToDiscord(r.Login))
            .Where(id => id != null)
            .Select(id => $"<@{id}>");

        var channel = await _discord.GetChannelAsync(ct);
        if (channel == null) return;
        var stored = _prMap.Get(pr.NodeId);
        var target = await _discord.GetTargetChannel(channel, stored, ct);
        var content = string.Join(' ', pings);
        await _discord.SendMessageAsync(target.Id, string.IsNullOrEmpty(content) ? null : content, embed, ct);
    }

    private async Task HandleAssigned(PullRequest pr, Repository repo, JsonElement payload, CancellationToken ct)
    {
        var assignee = payload.TryGetProperty("assignee", out var a) ? a.Deserialize<GitHubUser>() : pr.Assignee;
        if (assignee == null) return;

        var embed = EmbedBuilders.AssignedEmbed(pr, repo, assignee, _userMap);
        var ping = _userMap.GitHubToDiscord(assignee.Login);
        var content = ping != null ? $"<@{ping}>" : null;

        var channel = await _discord.GetChannelAsync(ct);
        if (channel == null) return;
        var stored = _prMap.Get(pr.NodeId);
        var target = await _discord.GetTargetChannel(channel, stored, ct);
        await _discord.SendMessageAsync(target.Id, content, embed, ct);
    }

    private async Task HandleEdited(PullRequest pr, Repository repo, CancellationToken ct)
    {
        var stored = _prMap.Get(pr.NodeId);
        if (stored?.MessageId == null || stored.MessageId == 0) return;

        var channel = await _discord.GetChannelAsync(ct);
        if (channel == null) return;

        var originalMsg = await _discord.GetMessageAsync(channel.Id, stored.MessageId);
        if (originalMsg == null) return;

        var content = originalMsg.Content;
        if (!content.StartsWith("[Closed]") && !content.StartsWith("[Merged]"))
        {
            var updatedEmbed = EmbedBuilders.PrEmbed(pr, repo,
                pr.Draft ? "converted_to_draft" : "opened", _userMap);
            await _discord.EditMessageAsync(channel.Id, stored.MessageId, null, updatedEmbed);
        }
    }
}
