using System.Text.Json;
using System.Text.RegularExpressions;
using Discord;
using GithubBot.Discord;
using GithubBot.Services;

namespace GithubBot.Handlers;

public class AdoWorkItemHandler
{
    private readonly DiscordBotService _discord;
    private readonly UserMapService _userMap;
    private readonly WorkItemMapService _workItemMap;
    private readonly ScoreService _scores;
    private readonly AdoApiService? _adoApi;
    private readonly IConfiguration _config;
    private readonly ILogger<AdoWorkItemHandler> _logger;

    // Prevents concurrent handlers for the same work item from racing to create duplicate stubs/threads
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> _wiLocks = new();

    public AdoWorkItemHandler(
        DiscordBotService discord,
        UserMapService userMap,
        WorkItemMapService workItemMap,
        ScoreService scores,
        IConfiguration config,
        ILogger<AdoWorkItemHandler> logger,
        AdoApiService? adoApi = null)
    {
        _discord = discord;
        _userMap = userMap;
        _workItemMap = workItemMap;
        _scores = scores;
        _adoApi = adoApi;
        _config = config;
        _logger = logger;
    }

    // ── workitem.created ────────────────────────────────────────────────────

    public async Task HandleWorkItemCreatedAsync(JsonElement payload, CancellationToken ct = default)
    {
        if (!TryGetChannel(out var channelId)) return;
        if (!TryParseWorkItem(payload, out var wi)) return;

        var embed = BuildBaseEmbed(wi, wi.Color);
        AddStandardFields(embed, wi, showDescription: true);

        _logger.LogInformation("[ADO] Work item created #{Id} type={Type}", wi.Id, wi.WorkItemType);
        string? creatorDiscordId = !string.IsNullOrEmpty(wi.CreatedByEmail) ? _userMap.AdoToDiscord(wi.CreatedByEmail) : null;
        if (creatorDiscordId != null)
            _scores.Award(creatorDiscordId, ScoreCategory.TicketCreated);

        var pings = new List<string>();
        if (creatorDiscordId != null) pings.Add($"<@{creatorDiscordId}>");
        if (wi.AssignedToDiscord != null && wi.AssignedToDiscord != creatorDiscordId) pings.Add($"<@{wi.AssignedToDiscord}>");
        string? ping = pings.Count > 0 ? string.Join(" ", pings) : null;

        var msg = await _discord.SendMessageAsync(channelId, ping, embed.Build(), ct);
        if (msg != null)
        {
            var threadId = await _discord.CreateThreadAsync(channelId, msg.Id,
                $"#{wi.Id} — {wi.Title ?? wi.WorkItemType}", ct);
            _workItemMap.Set(wi.Id, new WorkItemMapEntry
            {
                MessageId      = msg.Id,
                ThreadId       = threadId != 0 ? threadId : null,
                Title          = wi.Title,
                WorkItemType   = wi.WorkItemType,
                AssignedToEmail = wi.AssignedToEmail,
            });
        }
    }

    // ── workitem.updated ────────────────────────────────────────────────────

    public async Task HandleWorkItemUpdatedAsync(JsonElement payload, CancellationToken ct = default)
    {
        if (!TryGetChannel(out var channelId)) return;
        if (!TryParseWorkItem(payload, out var wi)) return;

        var resource = payload.GetProperty("resource");
        var changedFields = resource.TryGetProperty("fields", out var cf) ? cf : default;

        // Comment edits arrive as workitem.updated with System.History changed
        if (changedFields.ValueKind == JsonValueKind.Object &&
            changedFields.TryGetProperty("System.History", out var historyDiff) &&
            historyDiff.TryGetProperty("newValue", out var historyNewEl) &&
            historyNewEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(historyNewEl.GetString()))
        {
            var commentHtml = historyNewEl.GetString()!;
            var plain = StripHtml(commentHtml);
            if (plain.Length > 1000) plain = plain[..1000] + "…";

            int? commentId = null;
            int commentVersion = 2; // arrived via updated = edit
            if (resource.TryGetProperty("commentVersionRef", out var cvr))
            {
                if (cvr.TryGetProperty("commentId", out var cid)) commentId = cid.GetInt32();
                if (cvr.TryGetProperty("version", out var ver)) commentVersion = ver.GetInt32();
            }

            var editEmbed = new EmbedBuilder()
                .WithTitle($"[#{wi.Id}] ✏️ Comment Edited on {TypeEmoji(wi.WorkItemType).emoji}{(wi.Title != null ? $": {wi.Title}" : "")}")
                .WithColor(new Color(0x5865F2u))
                .WithUrl(wi.Url);
            if (!string.IsNullOrWhiteSpace(plain)) editEmbed.WithDescription(plain);
            if (!string.IsNullOrEmpty(wi.ChangedByEmail))
            {
                var d = _userMap.AdoToDiscord(wi.ChangedByEmail);
                editEmbed.AddField("By", d != null ? $"<@{d}>" : wi.ChangedByEmail, inline: true);
            }

            var wiLock2 = _wiLocks.GetOrAdd(wi.Id, _ => new SemaphoreSlim(1, 1));
            await wiLock2.WaitAsync(ct);
            ulong editTarget;
            try { editTarget = await ResolveThreadAsync(channelId, wi, ct); }
            finally { wiLock2.Release(); }

            if (commentId.HasValue)
            {
                var storedEntry = _workItemMap.Get(wi.Id);
                if (storedEntry != null && storedEntry.CommentMessages.TryGetValue(commentId.Value, out var existingMsgId))
                {
                    await _discord.EditMessageAsync(editTarget, existingMsgId, null, editEmbed.Build());
                    return;
                }
            }

            // Not tracked — post as new and track it
            var editMsg = await _discord.SendMessageAsync(editTarget, null, editEmbed.Build(), ct);
            if (editMsg != null && commentId.HasValue)
            {
                var storedEntry = _workItemMap.Get(wi.Id);
                if (storedEntry != null)
                {
                    storedEntry.CommentMessages[commentId.Value] = editMsg.Id;
                    _workItemMap.Set(wi.Id, storedEntry);
                }
            }
            return;
        }

        // Fields that are always noise — changed by every event or carry no user-visible info
        var skipFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.Rev", "System.RevisedDate", "System.ChangedDate", "System.Watermark",
            "System.History", "System.CommentCount", "System.AuthorizedDate", "System.AuthorizedAs",
            "System.PersonId",
            "Microsoft.VSTS.Common.StateChangeDate", "Microsoft.VSTS.Common.AuthorizedDate",
        };

        // Friendly names for well-known fields; unknown fields use the last segment of the field name
        var friendlyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["System.State"]                          = "State",
            ["System.AssignedTo"]                     = "Assigned To",
            ["System.Title"]                          = "Title",
            ["System.AreaPath"]                       = "Area",
            ["System.IterationPath"]                  = "Iteration",
            ["Microsoft.VSTS.Common.Priority"]        = "Priority",
            ["Microsoft.VSTS.Common.Severity"]        = "Severity",
            ["Microsoft.VSTS.Common.ResolvedReason"]  = "Resolved Reason",
            ["Microsoft.VSTS.TCM.ReproSteps"]         = "Repro Steps",
        };

        var fieldLines = new List<(string label, string value)>();
        if (changedFields.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in changedFields.EnumerateObject())
            {
                if (skipFields.Contains(prop.Name)) continue;
                var oldRaw = prop.Value.TryGetProperty("oldValue", out var ov) ? FormatFieldValue(ov) : null;
                var newRaw = prop.Value.TryGetProperty("newValue", out var nv) ? FormatFieldValue(nv) : null;
                var bothEmpty = string.IsNullOrEmpty(oldRaw) && string.IsNullOrEmpty(newRaw);
                var unchanged = !string.IsNullOrEmpty(oldRaw) && oldRaw == newRaw;
                if (bothEmpty || unchanged) continue;
                var oldVal = StripHtml(oldRaw);
                var newVal = StripHtml(newRaw);
                var label = friendlyNames.TryGetValue(prop.Name, out var fn)
                    ? fn
                    : prop.Name.Contains('.') ? prop.Name[(prop.Name.LastIndexOf('.') + 1)..] : prop.Name;
                var display = (!string.IsNullOrEmpty(oldVal), !string.IsNullOrEmpty(newVal)) switch {
                    (true,  true)  => $"{oldVal} → **{newVal}**",
                    (true,  false) => $"~~{oldVal}~~ *(unassigned)*",
                    _              => $"**{newVal}**",
                };
                fieldLines.Add((label, display));
            }
        }

        if (fieldLines.Count == 0)
        {
            if (changedFields.ValueKind == JsonValueKind.Object)
                foreach (var prop in changedFields.EnumerateObject())
                    _logger.LogDebug("[ADO] Skipped field {Field}: old={Old} new={New}",
                        prop.Name,
                        prop.Value.TryGetProperty("oldValue", out var dov) ? dov.GetRawText() : "(none)",
                        prop.Value.TryGetProperty("newValue", out var dnv) ? dnv.GetRawText() : "(none)");
            _logger.LogInformation("[ADO] Work item updated #{Id} — no interesting fields changed, skipping", wi.Id);
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle($"[#{wi.Id}] ✏️ {TypeEmoji(wi.WorkItemType).emoji} Updated{(wi.Title != null ? $": {wi.Title}" : "")}")
            .WithColor(new Color(0x5865F2))
            .WithUrl(wi.Url);

        foreach (var (label, value) in fieldLines)
            embed.AddField(label, value.Length > 1024 ? value[..1021] + "…" : value, inline: true);

        if (!string.IsNullOrWhiteSpace(wi.ChangedByEmail))
        {
            var d = _userMap.AdoToDiscord(wi.ChangedByEmail);
            embed.AddField("Changed By", d != null ? $"<@{d}>" : wi.ChangedByEmail, inline: true);
        }

        // Ping assignee if AssignedTo changed to a different (non-empty) person
        string? ping = null;
        if (changedFields.ValueKind == JsonValueKind.Object &&
            changedFields.TryGetProperty("System.AssignedTo", out var atDiff) &&
            atDiff.TryGetProperty("newValue", out var atNew))
        {
            var atNewVal = FormatFieldValue(atNew);
            var atOldVal = atDiff.TryGetProperty("oldValue", out var atOld) ? FormatFieldValue(atOld) : null;
            if (!string.IsNullOrEmpty(atNewVal) && atNewVal != atOldVal && wi.AssignedToDiscord != null)
                ping = $"<@{wi.AssignedToDiscord}>";
        }

        // Award resolved points to assignee if state changed to a done state
        if (changedFields.ValueKind == JsonValueKind.Object &&
            changedFields.TryGetProperty("System.State", out var stateChange) &&
            stateChange.TryGetProperty("newValue", out var newState))
        {
            var newStateStr = newState.ValueKind == JsonValueKind.String ? newState.GetString() : null;
            var doneStates = new[] { "Resolved", "Closed", "Done" };
            if (newStateStr != null && doneStates.Contains(newStateStr, StringComparer.OrdinalIgnoreCase))
            {
                var resolvedBy = !string.IsNullOrEmpty(wi.AssignedToDiscord) ? wi.AssignedToDiscord
                    : (!string.IsNullOrEmpty(wi.ChangedByEmail) ? _userMap.AdoToDiscord(wi.ChangedByEmail) : null);
                if (resolvedBy != null)
                    _scores.AwardTicketResolved(resolvedBy, wi.WorkItemType);
            }
        }

        _logger.LogInformation("[ADO] Work item updated #{Id}", wi.Id);

        var wiLock = _wiLocks.GetOrAdd(wi.Id, _ => new SemaphoreSlim(1, 1));
        await wiLock.WaitAsync(ct);
        ulong target;
        try { target = await ResolveThreadAsync(channelId, wi, ct); }
        finally { wiLock.Release(); }

        var stored = _workItemMap.Get(wi.Id);
        if (stored != null)
        {
            // wi has the full current state from resource.revision.fields — rebuild the embed from it
            var updatedEmbed = BuildBaseEmbed(wi, wi.Color);
            AddStandardFields(updatedEmbed, wi, showDescription: true);
            await _discord.EditMessageAsync(channelId, stored.MessageId, null, updatedEmbed.Build());

            if (wi.Title != null && wi.Title != stored.Title)
                stored.Title = wi.Title;
            stored.AssignedToEmail = wi.AssignedToEmail;
            _workItemMap.Set(wi.Id, stored);
        }

        // Post the field-change summary in the thread
        await _discord.SendMessageAsync(target, ping, embed.Build(), ct);
    }

    // ── workitem.commented ──────────────────────────────────────────────────

    public async Task HandleWorkItemCommentedAsync(JsonElement payload, CancellationToken ct = default)
    {
        if (!TryGetChannel(out var channelId)) return;

        var resource = payload.TryGetProperty("resource", out var r) ? r : default;
        if (resource.ValueKind == JsonValueKind.Undefined) return;

        var workItemId = resource.TryGetProperty("id", out var wiId) ? wiId.GetInt32() : 0;
        var fields = resource.TryGetProperty("fields", out var f) ? f : default;
        var commentText    = fields.ValueKind == JsonValueKind.Object ? Str(fields, "System.History") : null;
        var title          = fields.ValueKind == JsonValueKind.Object ? Str(fields, "System.Title") : null;
        var workItemType   = fields.ValueKind == JsonValueKind.Object ? Str(fields, "System.WorkItemType") : null;
        var commenterEmail = fields.ValueKind == JsonValueKind.Object ? Email(fields, "System.ChangedBy") : null;

        // commentVersionRef.commentId is stable across edits; version > 1 means edited
        int? commentId = null;
        int commentVersion = 1;
        if (resource.TryGetProperty("commentVersionRef", out var cvr))
        {
            if (cvr.TryGetProperty("commentId", out var cid)) commentId = cid.GetInt32();
            if (cvr.TryGetProperty("version", out var ver)) commentVersion = ver.GetInt32();
        }

        var plain = string.IsNullOrWhiteSpace(commentText) ? null : StripHtml(commentText);
        if (plain?.Length > 1000) plain = plain[..1000] + "…";

        var emoji = TypeEmoji(workItemType).emoji;
        var isEdit = commentVersion > 1;
        var embedTitle = isEdit
            ? $"[#{workItemId}] ✏️ Comment Edited on {emoji}{(title != null ? $": {title}" : "")}"
            : $"[#{workItemId}] 💬 Comment on {emoji}{(title != null ? $": {title}" : "")}";

        var embed = new EmbedBuilder()
            .WithTitle(embedTitle)
            .WithColor(isEdit ? new Color(0x5865F2u) : new Color(0x57F287u))
            .WithUrl(BuildWorkItemUrl(payload, workItemId));

        if (!string.IsNullOrWhiteSpace(plain))
            embed.WithDescription(plain);

        if (!string.IsNullOrEmpty(commenterEmail))
        {
            var d = _userMap.AdoToDiscord(commenterEmail);
            _logger.LogInformation("[ADO] Comment by email={Email} resolved={Resolved}", commenterEmail, d ?? "null");
            if (fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("System.ChangedBy", out var changedByEl))
                TryRegisterIdentityString(changedByEl);
            embed.AddField("By", d != null ? $"<@{d}>" : commenterEmail, inline: true);
        }

        if (!isEdit && !string.IsNullOrEmpty(commenterEmail) && _userMap.AdoToDiscord(commenterEmail) is string commentDiscordId)
            _scores.Award(commentDiscordId, ScoreCategory.TicketComment);

        _logger.LogInformation("[ADO] Work item {Action} #{Id} commentId={CommentId} version={Version}",
            isEdit ? "comment edited" : "commented", workItemId, commentId, commentVersion);

        var wi = new WorkItemInfo(workItemId, title, workItemType, null, null, null, null, null, null, null,
            BuildWorkItemUrl(payload, workItemId), Color.Default);

        var wiLock = _wiLocks.GetOrAdd(workItemId, _ => new SemaphoreSlim(1, 1));
        await wiLock.WaitAsync(ct);
        ulong target;
        try { target = await ResolveThreadAsync(channelId, wi, ct); }
        finally { wiLock.Release(); }

        // If this is an edit and we have the original Discord message tracked, edit it in place
        if (isEdit && commentId.HasValue)
        {
            var stored = _workItemMap.Get(workItemId);
            if (stored != null && stored.CommentMessages.TryGetValue(commentId.Value, out var existingMsgId))
            {
                await _discord.EditMessageAsync(target, existingMsgId, null, embed.Build());
                return;
            }
        }

        // New comment (or untracked edit) — post and track the message ID
        var mentionedPings = ExtractMentionNames(commentText)
            .Select(name => _userMap.AdoDisplayNameToDiscord(name))
            .Where(d => d != null)
            .Distinct()
            .Select(d => $"<@{d}>")
            .ToList();
        var mentionContent = !isEdit && mentionedPings.Count > 0 ? string.Join(" ", mentionedPings) : null;

        var msg = await _discord.SendMessageAsync(target, mentionContent, embed.Build(), ct);
        if (msg != null && commentId.HasValue)
        {
            var stored = _workItemMap.Get(workItemId);
            if (stored != null)
            {
                stored.CommentMessages[commentId.Value] = msg.Id;
                _workItemMap.Set(workItemId, stored);
            }
        }
    }

    // ── workitem.deleted ────────────────────────────────────────────────────

    public async Task HandleWorkItemDeletedAsync(JsonElement payload, CancellationToken ct = default)
    {
        if (!TryGetChannel(out var channelId)) return;
        if (!TryParseWorkItem(payload, out var wi)) return;

        var embed = BuildBaseEmbed(wi, Color.Red, "🗑️ Deleted");
        AddStandardFields(embed, wi, showDescription: false);

        _logger.LogInformation("[ADO] Work item deleted #{Id}", wi.Id);
        var target = await ResolveThreadAsync(channelId, wi, ct);
        await _discord.SendMessageAsync(target, null, embed.Build(), ct);

        // Archive the thread if we have one
        var stored = _workItemMap.Get(wi.Id);
        if (stored?.ThreadId is ulong threadId)
            await _discord.ArchiveThreadAsync(threadId, ct);
    }

    // ── Thread resolution ────────────────────────────────────────────────────

    private async Task<ulong> ResolveThreadAsync(ulong channelId, WorkItemInfo wi, CancellationToken ct)
    {
        var stored = _workItemMap.Get(wi.Id);

        if (stored?.ThreadId is ulong threadId)
        {
            var cached = _discord.Client.GetChannel(threadId);
            if (cached != null) return threadId;

            try
            {
                var restThread = await _discord.Client.Rest.GetChannelAsync(threadId)
                    as global::Discord.Rest.RestThreadChannel;
                if (restThread != null)
                {
                    if (restThread.IsArchived)
                        await restThread.ModifyAsync(p => p.Archived = false);
                    return threadId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ADO] Thread {ThreadId} not accessible, falling back to search", threadId);
            }
        }

        // Have a tracked message but no accessible thread — re-create thread from it
        if (stored?.MessageId is ulong msgId && msgId != 0)
        {
            var newThreadId = await _discord.CreateThreadAsync(channelId, msgId,
                $"#{wi.Id} — {wi.Title ?? wi.WorkItemType ?? "Work Item"}", ct);
            if (newThreadId != 0)
            {
                stored!.ThreadId = newThreadId;
                _workItemMap.Set(wi.Id, stored);
                return newThreadId;
            }
        }

        // Not tracked at all — search channel history for a message mentioning this work item ID
        var textChannel = _discord.Client.GetChannel(channelId) as global::Discord.ITextChannel;
        if (textChannel != null)
        {
            global::Discord.IMessage? found = null;
            ulong lastId = 0;
            var idPattern = $"#{wi.Id}";
            for (var page = 0; page < 3 && found == null; page++)
            {
                var batch = lastId == 0
                    ? await textChannel.GetMessagesAsync(100).FlattenAsync()
                    : await textChannel.GetMessagesAsync(lastId, global::Discord.Direction.Before, 100).FlattenAsync();
                var messages = batch.ToList();
                if (messages.Count == 0) break;
                found = messages.FirstOrDefault(m =>
                    m.Embeds.Any(e => e.Title != null && e.Title.Contains(idPattern)));
                lastId = messages[^1].Id;
            }

            if (found != null)
            {
                _logger.LogInformation("[ADO] Found existing message {MsgId} for work item #{Id}", found.Id, wi.Id);
                var adoptedThreadId = await _discord.CreateThreadAsync(channelId, found.Id,
                    $"#{wi.Id} — {wi.Title ?? wi.WorkItemType ?? "Work Item"}", ct);
                _workItemMap.Set(wi.Id, new WorkItemMapEntry
                {
                    MessageId       = found.Id,
                    ThreadId        = adoptedThreadId != 0 ? adoptedThreadId : null,
                    Title           = wi.Title,
                    WorkItemType    = wi.WorkItemType,
                    AssignedToEmail = wi.AssignedToEmail,
                });
                return adoptedThreadId != 0 ? adoptedThreadId : channelId;
            }
        }

        // Nothing found — try to fetch full details from ADO API, fall back to stub
        _logger.LogInformation("[ADO] No existing message for work item #{Id}, creating embed", wi.Id);
        global::Discord.Embed initialEmbed;
        WorkItemInfo embedWi = wi;
        if (_adoApi != null)
        {
            var fetched = await _adoApi.GetWorkItemsAsync([wi.Id], ct);
            if (fetched.Count > 0)
            {
                var item = fetched[0];
                var assignedEmail = ExtractEmail(item.AssignedTo);
                embedWi = wi with
                {
                    Title          = item.Title ?? wi.Title,
                    WorkItemType   = item.WorkItemType ?? wi.WorkItemType,
                    State          = item.State,
                    AreaPath       = item.AreaPath,
                    AssignedToEmail   = assignedEmail,
                    AssignedToDiscord = !string.IsNullOrEmpty(assignedEmail) ? _userMap.AdoToDiscord(assignedEmail) : null,
                    Url            = _adoApi.BuildWorkItemUrl(wi.Id),
                };
            }
        }
        var embedBuilder = BuildBaseEmbed(embedWi, embedWi.Color);
        AddStandardFields(embedBuilder, embedWi, showDescription: false);
        initialEmbed = embedBuilder.Build();

        var stub = await _discord.SendMessageAsync(channelId, null, initialEmbed, ct);
        if (stub != null)
        {
            var stubThreadId = await _discord.CreateThreadAsync(channelId, stub.Id,
                $"#{embedWi.Id} — {embedWi.Title ?? embedWi.WorkItemType ?? "Work Item"}", ct);
            _workItemMap.Set(wi.Id, new WorkItemMapEntry
            {
                MessageId       = stub.Id,
                ThreadId        = stubThreadId != 0 ? stubThreadId : null,
                Title           = embedWi.Title,
                WorkItemType    = embedWi.WorkItemType,
                AssignedToEmail = embedWi.AssignedToEmail,
            });
            return stubThreadId != 0 ? stubThreadId : channelId;
        }

        return channelId;
    }

    // ── /trackticket ─────────────────────────────────────────────────────────

    public async Task<string> TrackWorkItemAsync(int id, CancellationToken ct = default)
    {
        if (_adoApi == null) return "ADO API not configured (set ADO_ORG_URL, ADO_PROJECT, ADO_PAT).";
        if (!TryGetChannel(out var channelId)) return "No ticket channel configured.";

        var items = await _adoApi.GetWorkItemsAsync([id], ct);
        if (items.Count == 0) return $"Ticket #{id} not found in ADO.";

        var item = items[0];
        var (color, _) = TypeEmoji(item.WorkItemType);
        var assignedEmail = ExtractEmail(item.AssignedTo);
        var wi = new WorkItemInfo(
            Id:               id,
            Title:            item.Title,
            WorkItemType:     item.WorkItemType,
            State:            item.State,
            AreaPath:         item.AreaPath,
            Description:      item.Description,
            AssignedToEmail:  assignedEmail,
            AssignedToDiscord: !string.IsNullOrEmpty(assignedEmail) ? _userMap.AdoToDiscord(assignedEmail) : null,
            CreatedByEmail:   null,
            ChangedByEmail:   null,
            Url:              _adoApi.BuildWorkItemUrl(id),
            Color:            color);

        // Already tracked — refresh embed and return link
        if (_workItemMap.Get(id) is { } existing)
        {
            if (existing.MessageId != 0)
            {
                var refreshed = BuildBaseEmbed(wi, color);
                AddStandardFields(refreshed, wi, showDescription: false);
                await _discord.EditMessageAsync(channelId, existing.MessageId, null, refreshed.Build());
                existing.Title          = wi.Title;
                existing.AssignedToEmail = wi.AssignedToEmail;
                _workItemMap.Set(id, existing);
            }

            var link = existing.ThreadId.HasValue
                ? $"<#{existing.ThreadId.Value}>"
                : existing.MessageId != 0 &&
                  ulong.TryParse(_config["Discord:GuildId"], out var gid) &&
                  ulong.TryParse(_config["Discord:TicketChannelId"], out var cid)
                    ? $"https://discord.com/channels/{gid}/{cid}/{existing.MessageId}"
                    : $"#{id}";
            return $"Ticket #{id} refreshed — {link}";
        }

        var embed = BuildBaseEmbed(wi, color);
        AddStandardFields(embed, wi, showDescription: true);

        var wiLock = _wiLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await wiLock.WaitAsync(ct);
        try
        {
            var msg = await _discord.SendMessageAsync(channelId, null, embed.Build(), ct);
            if (msg == null) return "Failed to post embed to ticket channel.";

            var threadId = await _discord.CreateThreadAsync(channelId, msg.Id,
                $"#{id} — {item.Title ?? item.WorkItemType ?? "Work Item"}", ct);

            _workItemMap.Set(id, new WorkItemMapEntry
            {
                MessageId       = msg.Id,
                ThreadId        = threadId != 0 ? threadId : null,
                Title           = item.Title,
                WorkItemType    = item.WorkItemType,
                AssignedToEmail = assignedEmail,
            });
        }
        finally { wiLock.Release(); }

        _logger.LogInformation("[ADO] Manually tracked work item #{Id}", id);
        return $"✅ Ticket #{id} is now tracked.";
    }

    // ── /untrackticket ───────────────────────────────────────────────────────

    public async Task<string> UntrackWorkItemAsync(int id, CancellationToken ct = default)
    {
        if (!TryGetChannel(out var channelId)) return "No ticket channel configured.";

        var stored = _workItemMap.Get(id);
        if (stored == null) return $"Ticket #{id} is not being tracked.";

        if (stored.ThreadId is ulong threadId)
        {
            try { await _discord.ArchiveThreadAsync(threadId, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "[ADO] Could not archive thread {ThreadId} for #{Id}", threadId, id); }
        }

        if (stored.MessageId != 0)
        {
            try { await _discord.DeleteMessageAsync(channelId, stored.MessageId); }
            catch (Exception ex) { _logger.LogWarning(ex, "[ADO] Could not delete message {MsgId} for #{Id}", stored.MessageId, id); }
        }

        _workItemMap.Remove(id);
        _logger.LogInformation("[ADO] Untracked work item #{Id}", id);
        return $"✅ Ticket #{id} untracked — message deleted and thread archived.";
    }

    // ── Shared helpers ───────────────────────────────────────────────────────

    private record WorkItemInfo(
        int Id,
        string? Title,
        string? WorkItemType,
        string? State,
        string? AreaPath,
        string? Description,
        string? AssignedToEmail,
        string? AssignedToDiscord,
        string? CreatedByEmail,
        string? ChangedByEmail,
        string? Url,
        Color Color,
        string? ReproSteps = null,
        string? ExpectedOutcome = null,
        string? ActualOutcome = null);

    private bool TryGetChannel(out ulong channelId)
    {
        channelId = 0;
        var raw = _config["Discord:TicketChannelId"];
        if (!ulong.TryParse(raw, out channelId) || channelId == 0)
        {
            _logger.LogWarning("[ADO] No ticket channel configured");
            return false;
        }
        return true;
    }

    private bool TryParseWorkItem(JsonElement payload, out WorkItemInfo wi)
    {
        wi = null!;
        var resource = payload.TryGetProperty("resource", out var r) ? r : default;
        if (resource.ValueKind == JsonValueKind.Undefined) return false;

        JsonElement fields = default;
        // revision.fields has the full current state; resource.fields for updates only has {oldValue,newValue} diffs
        if (resource.TryGetProperty("revision", out var rev) &&
            rev.TryGetProperty("fields", out var rf) && rf.ValueKind == JsonValueKind.Object)
            fields = rf;
        else if (resource.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object)
            fields = f;
        if (fields.ValueKind == JsonValueKind.Undefined) return false;

        var id = resource.TryGetProperty("workItemId", out var wiId) ? wiId.GetInt32() :
                 resource.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;

        var assignedEmail  = Email(fields, "System.AssignedTo");
        var (color, _)     = TypeEmoji(Str(fields, "System.WorkItemType"));

        // Register display name → Discord mappings for all identity fields we encounter
        foreach (var key in new[] { "System.AssignedTo", "System.CreatedBy", "System.ChangedBy" })
            if (fields.TryGetProperty(key, out var idField)) TryRegisterIdentityString(idField);

        wi = new WorkItemInfo(
            Id:               id,
            Title:            Str(fields, "System.Title"),
            WorkItemType:     Str(fields, "System.WorkItemType") ?? "Work Item",
            State:            Str(fields, "System.State"),
            AreaPath:         Str(fields, "System.AreaPath"),
            Description:      Str(fields, "System.Description"),
            AssignedToEmail:  assignedEmail,
            AssignedToDiscord: !string.IsNullOrEmpty(assignedEmail) ? _userMap.AdoToDiscord(assignedEmail) : null,
            CreatedByEmail:   Email(fields, "System.CreatedBy"),
            ChangedByEmail:   Email(fields, "System.ChangedBy"),
            Url:              BuildWorkItemUrl(payload, id),
            Color:            color,
            ReproSteps:       Str(fields, "Microsoft.VSTS.TCM.ReproSteps"),
            ExpectedOutcome:  Str(fields, "Custom.ExpectedOutcome"),
            ActualOutcome:    Str(fields, "Custom.ActualOutcome"));
        return true;
    }

    private EmbedBuilder BuildBaseEmbed(WorkItemInfo wi, Color color, string? eventTitle = null)
    {
        var emoji = TypeEmoji(wi.WorkItemType).emoji;
        var prefix = string.IsNullOrEmpty(eventTitle) ? "" : $"{eventTitle} — ";
        return new EmbedBuilder()
            .WithTitle($"[#{wi.Id}] {prefix}{emoji} {wi.WorkItemType}{(wi.Title != null ? $": {wi.Title}" : "")}")
            .WithColor(color)
            .WithUrl(wi.Url);
    }

    private void AddStandardFields(EmbedBuilder embed, WorkItemInfo wi, bool showDescription)
    {
        if (!string.IsNullOrWhiteSpace(wi.State))
            embed.AddField("State", wi.State, inline: true);
        if (!string.IsNullOrWhiteSpace(wi.AreaPath))
            embed.AddField("Area", wi.AreaPath, inline: true);
        if (!string.IsNullOrWhiteSpace(wi.AssignedToEmail))
            embed.AddField("Assigned To",
                wi.AssignedToDiscord != null ? $"<@{wi.AssignedToDiscord}>" : wi.AssignedToEmail, inline: true);
        if (!string.IsNullOrWhiteSpace(wi.CreatedByEmail))
        {
            var d = _userMap.AdoToDiscord(wi.CreatedByEmail);
            embed.AddField("Created By", d != null ? $"<@{d}>" : wi.CreatedByEmail, inline: true);
        }
        if (showDescription && !string.IsNullOrWhiteSpace(wi.Description))
        {
            var plain = StripHtml(wi.Description);
            if (plain.Length > 300) plain = plain[..300] + "…";
            if (!string.IsNullOrWhiteSpace(plain))
                embed.WithDescription(plain);
        }
        if (wi.WorkItemType == "Bug")
        {
            void AddBugField(string label, string? raw)
            {
                var text = StripHtml(raw);
                if (string.IsNullOrWhiteSpace(text)) return;
                if (text.Length > 500) text = text[..497] + "…";
                embed.AddField(label, text);
            }
            AddBugField("Steps to Reproduce", wi.ReproSteps);
            AddBugField("Expected Outcome", wi.ExpectedOutcome);
            AddBugField("Actual Outcome", wi.ActualOutcome);
        }
    }

    private string? BuildWorkItemUrl(JsonElement payload, int id)
    {
        if (payload.TryGetProperty("_links", out var links) &&
            links.TryGetProperty("html", out var html) &&
            html.TryGetProperty("href", out var href))
            return href.GetString();

        if (payload.TryGetProperty("resourceContainers", out var containers) &&
            containers.TryGetProperty("project", out var project) &&
            project.TryGetProperty("baseUrl", out var baseUrl))
        {
            var b = baseUrl.GetString()?.TrimEnd('/');
            if (b != null && id != 0) return $"{b}/_workitems/edit/{id}";
        }
        return null;
    }

    private static (Color color, string emoji) TypeEmoji(string? type) => type switch
    {
        "Bug"        => (Color.Red,       "🐛"),
        "Task"       => (Color.Blue,      "✅"),
        "User Story" => (Color.Green,     "📖"),
        "Epic"       => (Color.Purple,    "🏔"),
        "Feature"    => (Color.Orange,    "⭐"),
        _            => (Color.LightGrey, "📋"),
    };

    private static string? Str(JsonElement fields, string key)
        => fields.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;

    private static string? ExtractEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        var lastSpace = s.LastIndexOf(' ');
        if (lastSpace >= 0)
        {
            var tail = s[(lastSpace + 1)..].Trim().Trim('<', '>');
            if (tail.Contains('@')) return tail;
        }
        return s.Trim('<', '>').Contains('@') ? s.Trim('<', '>') : null;
    }

    private static string? Email(JsonElement fields, string key)
    {
        if (!fields.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Object)
            return el.TryGetProperty("uniqueName", out var un) ? un.GetString() : null;
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString() ?? "";
            // ADO sometimes sends "Display Name email@domain.com" as a plain string
            s = s.Trim();
            var lastSpace = s.LastIndexOf(' ');
            if (lastSpace >= 0)
            {
                var tail = s.Substring(lastSpace + 1).Trim().Trim('<', '>');
                if (tail.Contains('@')) return tail;
            }
            return s.Trim('<', '>');
        }
        return null;
    }

    // Extract display names from ADO mention HTML: >@Display Name<
    private static IEnumerable<string> ExtractMentionNames(string? html)
    {
        if (string.IsNullOrEmpty(html)) yield break;
        foreach (Match m in Regex.Matches(html, @"data-vss-mention=""[^""]+""[^>]*>@([^<]+)<", RegexOptions.IgnoreCase))
            yield return m.Groups[1].Value.Trim();
    }

    // Register GUID→Discord mapping from an identity JsonElement (has both "id" and "uniqueName")
    // Parse "Display Name <email>" strings and register the display name → Discord mapping
    private void TryRegisterIdentityString(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.String) return;
        var s = el.GetString() ?? "";
        var match = Regex.Match(s, @"^(.+?)\s*<([^>]+)>$");
        if (!match.Success) return;
        _userMap.RegisterAdoDisplayName(match.Groups[1].Value, match.Groups[2].Value);
    }

    private static string StripHtml(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // Block elements → newlines before stripping
        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</p>|</li>|</h\d>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<li\s*/?>", "• ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", "");
        s = System.Net.WebUtility.HtmlDecode(s);
        // Collapse 3+ newlines to 2
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    private static string FormatFieldValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object  => el.TryGetProperty("uniqueName", out var un) ? un.GetString() ?? "" : "",
        JsonValueKind.String  => el.GetString() ?? "",
        JsonValueKind.Number  => el.GetRawText(),
        JsonValueKind.Null    => "—",
        _                     => el.GetRawText(),
    };
}
