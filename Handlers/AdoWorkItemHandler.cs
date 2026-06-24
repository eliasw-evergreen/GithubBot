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
    private readonly IConfiguration _config;
    private readonly ILogger<AdoWorkItemHandler> _logger;

    public AdoWorkItemHandler(
        DiscordBotService discord,
        UserMapService userMap,
        WorkItemMapService workItemMap,
        IConfiguration config,
        ILogger<AdoWorkItemHandler> logger)
    {
        _discord = discord;
        _userMap = userMap;
        _workItemMap = workItemMap;
        _config = config;
        _logger = logger;
    }

    // ── workitem.created ────────────────────────────────────────────────────

    public async Task HandleWorkItemCreatedAsync(JsonElement payload, CancellationToken ct = default)
    {
        if (!TryGetChannel(out var channelId)) return;
        if (!TryParseWorkItem(payload, out var wi)) return;

        var embed = BuildBaseEmbed(wi, "✨ Work Item Created", wi.Color);
        AddStandardFields(embed, wi, showDescription: true);

        string? ping = wi.AssignedToDiscord != null ? $"<@{wi.AssignedToDiscord}>" : null;

        _logger.LogInformation("[ADO] Work item created #{Id} type={Type}", wi.Id, wi.WorkItemType);
        var msg = await _discord.SendMessageAsync(channelId, ping, embed.Build(), ct);
        if (msg != null)
        {
            var threadId = await _discord.CreateThreadAsync(channelId, msg.Id,
                $"#{wi.Id} — {wi.Title ?? wi.WorkItemType}", ct);
            _workItemMap.Set(wi.Id, new WorkItemMapEntry
            {
                MessageId = msg.Id,
                ThreadId  = threadId != 0 ? threadId : null,
                Title     = wi.Title,
                WorkItemType = wi.WorkItemType,
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

        var embed = new EmbedBuilder()
            .WithTitle($"✏️ {TypeEmoji(wi.WorkItemType).emoji} #{wi.Id} Updated{(wi.Title != null ? $": {wi.Title}" : "")}")
            .WithColor(new Color(0x5865F2))
            .WithUrl(wi.Url);

        // Show what changed
        if (changedFields.ValueKind == JsonValueKind.Object)
        {
            var interesting = new[] {
                ("System.State",                          "State"),
                ("System.AssignedTo",                     "Assigned To"),
                ("System.Title",                          "Title"),
                ("System.AreaPath",                       "Area"),
                ("Microsoft.VSTS.Common.Priority",        "Priority"),
                ("System.IterationPath",                  "Iteration"),
            };
            foreach (var (field, label) in interesting)
            {
                if (!changedFields.TryGetProperty(field, out var change)) continue;
                var oldVal = change.TryGetProperty("oldValue", out var ov) ? FormatFieldValue(ov) : null;
                var newVal = change.TryGetProperty("newValue", out var nv) ? FormatFieldValue(nv) : null;
                if (string.IsNullOrEmpty(newVal) || oldVal == newVal) continue;
                embed.AddField(label, oldVal != null ? $"{oldVal} → **{newVal}**" : $"**{newVal}**", inline: true);
            }
        }

        if (!string.IsNullOrWhiteSpace(wi.ChangedByEmail))
        {
            var d = _userMap.AdoToDiscord(wi.ChangedByEmail);
            embed.AddField("Changed By", d != null ? $"<@{d}>" : wi.ChangedByEmail, inline: true);
        }

        // Only ping if assignment changed
        string? ping = null;
        if (changedFields.ValueKind == JsonValueKind.Object &&
            changedFields.TryGetProperty("System.AssignedTo", out _) &&
            wi.AssignedToDiscord != null)
            ping = $"<@{wi.AssignedToDiscord}>";

        _logger.LogInformation("[ADO] Work item updated #{Id}", wi.Id);
        var target = await ResolveThreadAsync(channelId, wi, ct);
        await _discord.SendMessageAsync(target, ping, embed.Build(), ct);
    }

    // ── workitem.commented ──────────────────────────────────────────────────

    public async Task HandleWorkItemCommentedAsync(JsonElement payload, CancellationToken ct = default)
    {
        if (!TryGetChannel(out var channelId)) return;

        var resource = payload.TryGetProperty("resource", out var r) ? r : default;
        if (resource.ValueKind == JsonValueKind.Undefined) return;

        // resource.id is the work item ID; comment text is in fields["System.History"]
        var workItemId = resource.TryGetProperty("id", out var wiId) ? wiId.GetInt32() : 0;
        var fields = resource.TryGetProperty("fields", out var f) ? f : default;
        var commentText   = fields.ValueKind == JsonValueKind.Object ? Str(fields, "System.History") : null;
        var title         = fields.ValueKind == JsonValueKind.Object ? Str(fields, "System.Title") : null;
        var workItemType  = fields.ValueKind == JsonValueKind.Object ? Str(fields, "System.WorkItemType") : null;
        var commenterEmail = fields.ValueKind == JsonValueKind.Object ? Email(fields, "System.ChangedBy") : null;

        var plain = string.IsNullOrWhiteSpace(commentText)
            ? null : Regex.Replace(commentText, "<[^>]+>", "").Trim();
        if (plain?.Length > 1000) plain = plain[..1000] + "…";

        var emoji = TypeEmoji(workItemType).emoji;
        var embed = new EmbedBuilder()
            .WithTitle($"💬 Comment on {emoji} #{workItemId}{(title != null ? $": {title}" : "")}")
            .WithColor(new Color(0x57F287))
            .WithUrl(BuildWorkItemUrl(payload, workItemId));

        if (!string.IsNullOrWhiteSpace(plain))
            embed.WithDescription(plain);

        if (!string.IsNullOrEmpty(commenterEmail))
        {
            var d = _userMap.AdoToDiscord(commenterEmail);
            embed.AddField("By", d != null ? $"<@{d}>" : commenterEmail, inline: true);
        }

        _logger.LogInformation("[ADO] Work item commented #{Id}", workItemId);

        // Use a minimal WorkItemInfo for thread resolution
        var wi = new WorkItemInfo(workItemId, title, workItemType, null, null, null, null, null, null, null,
            BuildWorkItemUrl(payload, workItemId), Color.Default);
        var target = await ResolveThreadAsync(channelId, wi, ct);
        await _discord.SendMessageAsync(target, null, embed.Build(), ct);
    }

    // ── workitem.deleted ────────────────────────────────────────────────────

    public async Task HandleWorkItemDeletedAsync(JsonElement payload, CancellationToken ct = default)
    {
        if (!TryGetChannel(out var channelId)) return;
        if (!TryParseWorkItem(payload, out var wi)) return;

        var embed = BuildBaseEmbed(wi, "🗑️ Work Item Deleted", Color.Red);
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
                    MessageId    = found.Id,
                    ThreadId     = adoptedThreadId != 0 ? adoptedThreadId : null,
                    Title        = wi.Title,
                    WorkItemType = wi.WorkItemType,
                });
                return adoptedThreadId != 0 ? adoptedThreadId : channelId;
            }
        }

        // Nothing found — post a stub and create a thread from it
        _logger.LogInformation("[ADO] No existing message for work item #{Id}, creating stub", wi.Id);
        var (color, emoji) = TypeEmoji(wi.WorkItemType);
        var stubEmbed = new global::Discord.EmbedBuilder()
            .WithTitle($"{emoji} {wi.WorkItemType ?? "Work Item"} #{wi.Id}{(wi.Title != null ? $": {wi.Title}" : "")}")
            .WithColor(color)
            .WithUrl(wi.Url)
            .WithDescription("Activity was received for this work item before it was tracked.")
            .Build();

        var stub = await _discord.SendMessageAsync(channelId, null, stubEmbed, ct);
        if (stub != null)
        {
            var stubThreadId = await _discord.CreateThreadAsync(channelId, stub.Id,
                $"#{wi.Id} — {wi.Title ?? wi.WorkItemType ?? "Work Item"}", ct);
            _workItemMap.Set(wi.Id, new WorkItemMapEntry
            {
                MessageId    = stub.Id,
                ThreadId     = stubThreadId != 0 ? stubThreadId : null,
                Title        = wi.Title,
                WorkItemType = wi.WorkItemType,
            });
            return stubThreadId != 0 ? stubThreadId : channelId;
        }

        return channelId;
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
        Color Color);

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
        if (resource.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object)
            fields = f;
        else if (resource.TryGetProperty("revision", out var rev) &&
                 rev.TryGetProperty("fields", out var rf))
            fields = rf;
        if (fields.ValueKind == JsonValueKind.Undefined) return false;

        var id = resource.TryGetProperty("workItemId", out var wiId) ? wiId.GetInt32() :
                 resource.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;

        var assignedEmail  = Email(fields, "System.AssignedTo");
        var (color, _)     = TypeEmoji(Str(fields, "System.WorkItemType"));

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
            Color:            color);
        return true;
    }

    private EmbedBuilder BuildBaseEmbed(WorkItemInfo wi, string eventTitle, Color color)
    {
        var emoji = TypeEmoji(wi.WorkItemType).emoji;
        return new EmbedBuilder()
            .WithTitle($"{eventTitle} — {emoji} {wi.WorkItemType} #{wi.Id}{(wi.Title != null ? $": {wi.Title}" : "")}")
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
            var plain = Regex.Replace(wi.Description, "<[^>]+>", "").Trim();
            if (plain.Length > 300) plain = plain[..300] + "…";
            if (!string.IsNullOrWhiteSpace(plain))
                embed.WithDescription(plain);
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

    private static string? Email(JsonElement fields, string key)
    {
        if (!fields.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Object)
            return el.TryGetProperty("uniqueName", out var un) ? un.GetString() : null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
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
