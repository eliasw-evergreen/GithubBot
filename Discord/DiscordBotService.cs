using Discord;
using Discord.WebSocket;
using GithubBot.Models;
using GithubBot.Services;

namespace GithubBot.Discord;

public class DiscordBotService : IHostedService
{
    private readonly DiscordSocketClient _client;
    private readonly IConfiguration _config;
    private readonly PreferencesService _prefs;
    private readonly UserMapService _userMap;
    private readonly PrMapService _prMap;
    private readonly GitHubApiService? _gitHub;
    private readonly PrSummaryService? _summary;
    private readonly SlashCommandHandler _slashHandler;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly WorkHoursService? _workHours;
    private readonly DeferredPingService? _deferred;

    public DiscordBotService(
        DiscordSocketClient client,
        IConfiguration config,
        PreferencesService prefs,
        UserMapService userMap,
        PrMapService prMap,
        SlashCommandHandler slashHandler,
        ILogger<DiscordBotService> logger,
        WorkHoursService? workHours = null,
        DeferredPingService? deferred = null,
        GitHubApiService? gitHub = null,
        PrSummaryService? summary = null)
    {
        _client = client;
        _config = config;
        _prefs = prefs;
        _userMap = userMap;
        _prMap = prMap;
        _gitHub = gitHub;
        _summary = summary;
        _slashHandler = slashHandler;
        _logger = logger;
        _workHours = workHours;
        _deferred = deferred;

        _client.Log += msg =>
        {
            _logger.LogInformation("[Discord] {Message}", msg.ToString());
            return Task.CompletedTask;
        };

        _client.Ready += OnReady;
        _client.InteractionCreated += _slashHandler.HandleInteractionAsync;
        _client.AutocompleteExecuted += _slashHandler.HandleAutocompleteAsync;
    }

    public DiscordSocketClient Client => _client;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var token = _config["Discord:BotToken"];
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Discord bot token is not configured");

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();
    }

    private ulong ResolveChannelId(string key, string configKey)
    {
        var raw = _prefs.ResolveChannel(key, _config[configKey]);
        return ulong.TryParse(raw, out var id) ? id : 0;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _client.StopAsync();
    }

    private Task OnReady()
    {
        _logger.LogInformation("Logged in as {User}", _client.CurrentUser?.Username ?? "unknown");
        _ = Task.Run(_slashHandler.RegisterAsync);
        _ = Task.Run(BackfillPrAuthorsAsync);

        return Task.CompletedTask;
    }

    private async Task BackfillPrAuthorsAsync()
    {
        var channelId = ResolveChannelId("pull", "Discord:ChannelId");
        if (channelId == 0) return;

        var channel = _client.GetChannel(channelId) as IMessageChannel;
        if (channel == null) return;

        var needsBackfill = _prMap.GetAll()
            .Where(kv => kv.Value.AuthorLogin == null && kv.Value.MessageId != 0)
            .ToList();

        if (needsBackfill.Count == 0) return;

        _logger.LogInformation("[Backfill] Scanning {Count} PR messages for author login", needsBackfill.Count);
        int filled = 0;

        foreach (var (nodeId, entry) in needsBackfill)
        {
            try
            {
                var msg = await channel.GetMessageAsync(entry.MessageId);
                var authorName = msg?.Embeds.FirstOrDefault()?.Author?.Name;
                if (string.IsNullOrEmpty(authorName)) continue;

                entry.AuthorLogin = authorName;
                _prMap.Set(nodeId, entry);
                filled++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Backfill] Could not fetch message {MsgId} for PR {NodeId}", entry.MessageId, nodeId);
            }
        }

        if (filled > 0)
            _logger.LogInformation("[Backfill] Filled AuthorLogin for {Count} PRs", filled);
    }


    public async Task BackfillPrCommentsAsync(ulong threadId, string repoFullName, int prNumber, string prTitle, string prHtmlUrl, CancellationToken ct = default)
    {
        if (_gitHub == null) return;

        try
        {
            var comments        = await _gitHub.GetPullRequestCommentsAsync(repoFullName, prNumber, ct);
            var reviewComments  = await _gitHub.GetPullRequestReviewCommentsAsync(repoFullName, prNumber, ct);
            var reviews         = await _gitHub.GetPullRequestReviewsAsync(repoFullName, prNumber, ct);

            // Normalize state to lowercase (REST API returns uppercase, webhook uses lowercase)
            foreach (var r in reviews) r.State = r.State.ToLowerInvariant();

            var meaningfulReviews = reviews
                .Where(r => r.State != "pending" &&
                            (!string.IsNullOrWhiteSpace(r.Body) || r.State is "approved" or "changes_requested"))
                .ToList();

            if (comments.Count == 0 && reviewComments.Count == 0 && meaningfulReviews.Count == 0) return;

            var items = new List<(DateTime At, object Item, bool IsReviewComment)>();
            foreach (var c in comments)
                items.Add((c.CreatedAt ?? DateTime.MinValue, c, false));
            foreach (var c in reviewComments)
                items.Add((c.CreatedAt ?? DateTime.MinValue, c, true));
            foreach (var r in meaningfulReviews)
                items.Add((r.SubmittedAt ?? DateTime.MinValue, r, false));
            items.Sort((a, b) => a.At.CompareTo(b.At));

            // Minimal models for the embed builders
            var pr = new PullRequest
            {
                Number  = prNumber,
                Title   = prTitle,
                HtmlUrl = prHtmlUrl,
                User    = new GitHubUser { Login = "" },
                Head    = new Branch { Ref = "" },
                Base    = new Branch { Ref = "" },
            };
            var repo = new Repository
            {
                FullName = repoFullName,
                Name     = repoFullName.Split('/').Last(),
                HtmlUrl  = $"https://github.com/{repoFullName}",
            };

            var commentReaction  = _prefs.ResolveReaction("comment",           _config["Reactions:Comment"]);
            var approvedReaction = _prefs.ResolveReaction("approved",          _config["Reactions:Approved"]);
            var changesReaction  = _prefs.ResolveReaction("changes_requested", _config["Reactions:ChangesRequested"]);

            _logger.LogInformation("[Backfill] Posting {Count} historical item(s) into thread {ThreadId} for PR #{Pr}", items.Count, threadId, prNumber);

            foreach (var (_, item, isReviewComment) in items)
            {
                global::Discord.Embed embed;
                if (item is Models.IssueComment comment)
                    embed = EmbedBuilders.CommentEmbed(comment, pr, repo, isReviewComment, _userMap, commentReaction);
                else if (item is Models.Review review)
                    embed = EmbedBuilders.ReviewSubmittedEmbed(review, pr, repo, _userMap, approvedReaction, changesReaction);
                else continue;

                await SendMessageAsync(threadId, null, embed, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Backfill] Failed to backfill comments for PR #{Pr}", prNumber);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex _githubPrUrlRegex =
        new(@"github\.com/([^/]+/[^/]+)/pull/(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private async Task<global::Discord.Embed?> FetchPrEmbedAsync(string prHtmlUrl, int prNumber, CancellationToken ct)
    {
        if (_gitHub == null) return null;

        var match = _githubPrUrlRegex.Match(prHtmlUrl);
        if (!match.Success) return null;
        var repoFullName = match.Groups[1].Value;

        try
        {
            var ghPr = await _gitHub.GetPullRequestAsync(repoFullName, prNumber, ct);
            if (ghPr == null) return null;

            var repoName = repoFullName.Split('/').Last();
            var pr = new PullRequest
            {
                NodeId  = ghPr.NodeId ?? $"gh_{repoFullName.Replace('/', '_')}_{prNumber}",
                Number  = ghPr.Number,
                Title   = ghPr.Title ?? $"PR #{prNumber}",
                HtmlUrl = ghPr.HtmlUrl ?? prHtmlUrl,
                User    = new GitHubUser { Login = ghPr.UserLogin ?? "unknown" },
                Draft   = ghPr.Draft,
                Body    = ghPr.Body,
                Head    = new Branch { Ref = ghPr.HeadRef ?? "" },
                Base    = new Branch { Ref = ghPr.BaseRef ?? "" },
            };
            var repo = new Repository
            {
                FullName = repoFullName,
                Name     = repoName,
                HtmlUrl  = $"https://github.com/{repoFullName}",
            };

            var action = ghPr.EmbedAction();
            string? descOverride = null;
            if (action is "opened" or "reopened" or "ready_for_review" && _summary != null)
            {
                var (status, text) = await _summary.SummarizeAsync(ghPr.Body, ct);
                if (status == PrSummaryService.SummarizeResult.Ok) descOverride = text;
            }

            return EmbedBuilders.PrEmbed(pr, repo, action, _userMap,
                openedReaction:           _prefs.ResolveReaction("opened",             _config["Reactions:Opened"]),
                reopenedReaction:         _prefs.ResolveReaction("reopened",           _config["Reactions:Reopened"]),
                readyForReviewReaction:   _prefs.ResolveReaction("ready_for_review",   _config["Reactions:ReadyForReview"]),
                convertedToDraftReaction: _prefs.ResolveReaction("converted_to_draft", _config["Reactions:ConvertedToDraft"]),
                mergedReaction:           _prefs.ResolveReaction("merged",             _config["Reactions:Merged"]),
                closedReaction:           _prefs.ResolveReaction("closed",             _config["Reactions:Closed"]),
                descMaxLines:             _prefs.ResolvePrDescMaxLines(),
                descriptionOverride:      descOverride);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PrResolve] Failed to fetch PR embed from GitHub API for {Url}", prHtmlUrl);
            return null;
        }
    }

    public Task<IMessageChannel?> GetChannelAsync(CancellationToken ct = default)
    {
        var channelId = ResolveChannelId("pull", "Discord:ChannelId");
        if (channelId == 0) return Task.FromResult<IMessageChannel?>(null);
        return Task.FromResult(_client.GetChannel(channelId) as IMessageChannel);
    }

    public Task<IMessageChannel?> GetTargetChannel(IMessageChannel channel, PrMapEntry? stored, CancellationToken ct = default)
    {
        if (stored?.ThreadId == null || stored.ThreadId == 0) return Task.FromResult<IMessageChannel?>(channel);

        var thread = _client.GetChannel(stored.ThreadId.Value) as IMessageChannel;
        return Task.FromResult<IMessageChannel?>(thread ?? channel);
    }

    private static bool IsPrEmbed(string? embedUrl, int prNumber, string prHtmlUrl)
    {
        if (string.IsNullOrEmpty(embedUrl)) return false;
        // Exact match first
        if (string.Equals(embedUrl.TrimEnd('/'), prHtmlUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return true;
        // Fallback: match any github.com URL ending in /pull/{prNumber}
        return embedUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase)
            && embedUrl.TrimEnd('/').EndsWith($"/pull/{prNumber}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the thread for a PR event. If the PR is tracked, returns its thread.
    /// If not tracked, searches recent channel messages for one whose embed URL matches
    /// the PR URL. If found, adopts it (creating a thread if needed) and registers it
    /// in the prmap. If not found, posts a stub message, creates a thread from it, and
    /// registers that.
    /// </summary>
    public async Task<IMessageChannel> ResolveOrCreatePrThreadAsync(
        IMessageChannel channel,
        PrMapEntry? stored,
        PrMapService prMap,
        string prNodeId,
        int prNumber,
        string prTitle,
        string prHtmlUrl,
        CancellationToken ct = default,
        Embed? stubEmbed = null)
    {
        // Happy path — already tracked with a thread
        if (stored?.ThreadId is ulong existingThread && existingThread != 0)
        {
            // Gateway cache first (fast)
            var t = _client.GetChannel(existingThread) as IMessageChannel;
            if (t != null) return t;

            // Archived threads aren't in the gateway cache — try REST
            try
            {
                var restThread = await _client.Rest.GetChannelAsync(existingThread) as global::Discord.Rest.RestThreadChannel;
                if (restThread != null)
                {
                    // Unarchive so the bot can post in it
                    if (restThread.IsArchived)
                        await restThread.ModifyAsync(p => p.Archived = false);
                    // Re-fetch from cache after unarchive
                    var unarchived = _client.GetChannel(existingThread) as IMessageChannel;
                    if (unarchived != null) return unarchived;
                    return restThread;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PrResolve] Thread {ThreadId} not accessible via REST, falling back to search", existingThread);
            }
        }

        if (channel is not ITextChannel textChannel)
            return channel;

        // Search recent messages for one whose embed URL matches the PR
        IMessage? found = null;
        ulong lastId = 0;
        for (var page = 0; page < 3 && found == null; page++)
        {
            var batch = lastId == 0
                ? await textChannel.GetMessagesAsync(100).FlattenAsync()
                : await textChannel.GetMessagesAsync(lastId, Direction.Before, 100).FlattenAsync();

            var messages = batch.ToList();
            if (messages.Count == 0) break;

            foreach (var msg in messages)
            {
                if (msg.Embeds.Any(e => IsPrEmbed(e.Url, prNumber, prHtmlUrl)))
                {
                    found = msg;
                    break;
                }
            }

            lastId = messages[^1].Id;
        }

        if (found != null)
        {
            _logger.LogInformation("[PrResolve] Found existing message {MsgId} for PR #{PrNumber}", found.Id, prNumber);

            ulong threadId;
            try
            {
                threadId = await CreateThreadAsync(textChannel.Id, found.Id, $"PR #{prNumber} — {prTitle}", ct);
            }
            catch
            {
                threadId = 0;
            }

            prMap.Set(prNodeId, new PrMapEntry
            {
                MessageId = found.Id,
                ThreadId = threadId != 0 ? threadId : null,
                PrNumber = prNumber,
                PrTitle = prTitle,
            });

            if (threadId != 0)
            {
                var repoFromUrl = _githubPrUrlRegex.Match(prHtmlUrl);
                if (repoFromUrl.Success)
                    _ = Task.Run(() => BackfillPrCommentsAsync(threadId, repoFromUrl.Groups[1].Value, prNumber, prTitle, prHtmlUrl));

                if (_client.GetChannel(threadId) is IMessageChannel threadCh)
                    return threadCh;
            }

            return channel;
        }

        // Not found — post a stub (or caller-supplied embed) and create a thread from it
        _logger.LogInformation("[PrResolve] No existing message found for PR #{PrNumber}, creating stub", prNumber);

        var resolvedEmbed = stubEmbed
            ?? await FetchPrEmbedAsync(prHtmlUrl, prNumber, ct)
            ?? new EmbedBuilder()
                .WithTitle($"PR #{prNumber} — {prTitle}")
                .WithUrl(prHtmlUrl)
                .WithColor(new Color(0x444466))
                .WithDescription("Activity was received for this PR before it was tracked.")
                .Build();

        var stub = await textChannel.SendMessageAsync(embed: resolvedEmbed);

        var newThreadId = await CreateThreadAsync(textChannel.Id, stub.Id, $"PR #{prNumber} — {prTitle}", ct);

        prMap.Set(prNodeId, new PrMapEntry
        {
            MessageId = stub.Id,
            ThreadId = newThreadId != 0 ? newThreadId : null,
            PrNumber = prNumber,
            PrTitle = prTitle,
        });

        if (newThreadId != 0)
        {
            var repoFromUrl = _githubPrUrlRegex.Match(prHtmlUrl);
            if (repoFromUrl.Success)
                _ = Task.Run(() => BackfillPrCommentsAsync(newThreadId, repoFromUrl.Groups[1].Value, prNumber, prTitle, prHtmlUrl));

            if (_client.GetChannel(newThreadId) is IMessageChannel newThread)
                return newThread;
        }

        return channel;
    }

    private static readonly System.Text.RegularExpressions.Regex _mentionRegex =
        new(@"<@[^>]+>", System.Text.RegularExpressions.RegexOptions.Compiled);

    public async Task<IUserMessage?> SendMessageAsync(
        ulong channelId, string? content, Embed? embed,
        CancellationToken ct = default, ulong? replyToMessageId = null,
        string? pingLabel = null, string? immediateContent = null)
    {
        var channel = _client.GetChannel(channelId) as IMessageChannel;
        if (channel == null) return null;

        string? finalContent;
        if (!string.IsNullOrEmpty(content) && content.Contains("<@")
            && _workHours != null && _deferred != null && !_workHours.IsWorkTime())
        {
            var pings = _mentionRegex.Matches(content).Select(m => m.Value).Distinct().ToList();
            var label = pingLabel ?? embed?.Title ?? "notification";
            _deferred.Defer(channelId, pings, label);
            finalContent = string.IsNullOrEmpty(immediateContent) ? null : immediateContent;
        }
        else
        {
            finalContent = string.IsNullOrEmpty(immediateContent)
                ? content
                : string.IsNullOrEmpty(content) ? immediateContent : $"{content} {immediateContent}";
        }

        var reference = replyToMessageId.HasValue
            ? new MessageReference(replyToMessageId.Value)
            : null;

        return await channel.SendMessageAsync(
            string.IsNullOrEmpty(finalContent) ? null : finalContent,
            embed: embed,
            messageReference: reference);
    }

    public async Task EditMessageAsync(ulong channelId, ulong messageId, string? newContent, Embed embed)
    {
        var channel = _client.GetChannel(channelId) as IMessageChannel;
        if (channel == null) return;

        var msg = await channel.GetMessageAsync(messageId) as IUserMessage;
        if (msg == null) return;

        await msg.ModifyAsync(props =>
        {
            if (newContent != null)
                props.Content = newContent;
            props.Embeds = new[] { embed };
        });
    }

    /// <summary>
    /// Patches a work item embed in place. ADO values override matching fields; fields not present
    /// in the ADO data are preserved from the existing embed. New fields are appended.
    /// </summary>
    public async Task PatchWorkItemEmbedAsync(ulong channelId, ulong messageId,
        Color color, string? state, string? area, string? assignedTo, string? createdBy,
        string? description, string? reproSteps, string? expectedOutcome, string? actualOutcome)
    {
        var channel = _client.GetChannel(channelId) as IMessageChannel;
        if (channel == null) return;

        var msg = await channel.GetMessageAsync(messageId) as IUserMessage;
        if (msg == null) return;

        var src = msg.Embeds.FirstOrDefault();
        if (src == null) return;

        // Build overrides map — only non-null ADO values replace existing embed fields
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (state           != null) overrides["State"]              = state;
        if (area            != null) overrides["Area"]               = area;
        if (assignedTo      != null) overrides["Assigned To"]        = assignedTo;
        if (createdBy       != null) overrides["Created By"]         = createdBy;
        if (reproSteps      != null) overrides["Steps to Reproduce"] = reproSteps;
        if (expectedOutcome != null) overrides["Expected Outcome"]   = expectedOutcome;
        if (actualOutcome   != null) overrides["Actual Outcome"]     = actualOutcome;

        var builder = new EmbedBuilder()
            .WithColor(color)
            .WithTitle(src.Title)
            .WithUrl(src.Url)
            .WithDescription(description ?? src.Description)
            .WithFooter(src.Footer?.Text)
            .WithImageUrl(src.Image?.Url);

        if (src.Author != null)
            builder.WithAuthor(src.Author.Value.Name, src.Author.Value.IconUrl, src.Author.Value.Url);
        if (src.Timestamp.HasValue)
            builder.WithTimestamp(src.Timestamp.Value);

        // Update existing fields with ADO overrides, preserve the rest
        var seenFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in src.Fields)
        {
            seenFields.Add(field.Name);
            var value = overrides.TryGetValue(field.Name, out var ov) ? ov : field.Value;
            builder.AddField(field.Name, value, field.Inline);
        }

        // Append any ADO fields that were not in the original embed
        foreach (var (name, value) in overrides)
            if (!seenFields.Contains(name))
                builder.AddField(name, value, inline: true);

        await msg.ModifyAsync(props => props.Embeds = new[] { builder.Build() });
    }

    public async Task DeleteMessageAsync(ulong channelId, ulong messageId)
    {
        var channel = _client.GetChannel(channelId) as IMessageChannel;
        if (channel == null) return;
        try
        {
            var msg = await channel.GetMessageAsync(messageId) as IUserMessage;
            if (msg != null) await msg.DeleteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete message {MessageId}", messageId);
        }
    }

    public async Task<IMessage?> GetMessageAsync(ulong channelId, ulong messageId)
    {
        var channel = _client.GetChannel(channelId) as IMessageChannel;
        if (channel == null) return null;
        try
        {
            return await channel.GetMessageAsync(messageId);
        }
        catch
        {
            return null;
        }
    }

    public async Task<ulong> CreateThreadAsync(ulong channelId, ulong messageId, string name, CancellationToken ct = default)
    {
        var channel = _client.GetChannel(channelId) as ITextChannel;
        if (channel == null) return 0;

        var msg = await channel.GetMessageAsync(messageId);
        if (msg == null) return 0;

        var truncated = name.Length > 100 ? name[..100] : name;
        var thread = await channel.CreateThreadAsync(truncated, message: msg,
            type: ThreadType.PublicThread, autoArchiveDuration: ThreadArchiveDuration.OneWeek);
        return thread.Id;
    }

    public async Task ArchiveThreadAsync(ulong threadId, CancellationToken ct = default)
    {
        var thread = _client.GetChannel(threadId) as IThreadChannel;
        if (thread != null)
            await thread.ModifyAsync(props => props.Archived = true);
    }

    public async Task UnarchiveThreadAsync(ulong threadId, CancellationToken ct = default)
    {
        // Archived threads aren't in the gateway cache — fetch via REST and unarchive
        try
        {
            var restThread = await _client.Rest.GetChannelAsync(threadId) as global::Discord.Rest.RestThreadChannel;
            if (restThread != null)
                await restThread.ModifyAsync(props => props.Archived = false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unarchive thread {ThreadId}", threadId);
        }
    }

    public async Task<IThreadChannel?> GetThreadAsync(ulong channelId, ulong threadId, CancellationToken ct = default)
    {
        var thread = _client.GetChannel(threadId) as IThreadChannel;
        return thread;
    }

    public async Task AddReactionAsync(ulong channelId, ulong messageId, string reaction, CancellationToken ct = default)
    {
        var channel = _client.GetChannel(channelId) as IMessageChannel;
        if (channel == null) return;

        var msg = await channel.GetMessageAsync(messageId);
        if (msg == null) return;

        IEmote emote = ParseEmote(reaction);
        try { await msg.AddReactionAsync(emote); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to add reaction {Reaction} — check emoji is accessible to the bot", reaction); }
    }

    private static bool EmotesMatch(IEmote a, IEmote b)
    {
        if (a is Emote ea && b is Emote eb) return ea.Id == eb.Id;
        return a.Name == b.Name;
    }

    private static IEmote ParseEmote(string reaction)
    {
        // <:name:id> or <a:name:id>
        var match = System.Text.RegularExpressions.Regex.Match(reaction, @"<a?:(\w+):(\d+)>");
        if (match.Success && ulong.TryParse(match.Groups[2].Value, out var emoteId))
            return new Emote(emoteId, match.Groups[1].Value);

        // bare numeric ID
        if (ulong.TryParse(reaction.Trim(), out var bareId))
            return new Emote(bareId, "");

        // unicode emoji
        return new Emoji(reaction);
    }

    public async Task ClearReactionsAsync(ulong channelId, ulong messageId, string? mergedReaction, string? closedReaction, CancellationToken ct = default)
    {
        var channel = _client.GetChannel(channelId) as IMessageChannel;
        if (channel == null) return;

        var msg = await channel.GetMessageAsync(messageId) as IUserMessage;
        if (msg == null) return;

        var toMatch = new List<IEmote>();
        if (!string.IsNullOrEmpty(mergedReaction)) toMatch.Add(ParseEmote(mergedReaction));
        if (!string.IsNullOrEmpty(closedReaction))  toMatch.Add(ParseEmote(closedReaction));

        foreach (var reaction in msg.Reactions)
        {
            if (toMatch.Any(e => EmotesMatch(e, reaction.Key)))
            {
                await msg.RemoveReactionAsync(reaction.Key, _client.CurrentUser);
            }
        }
    }
}
