using System.Text.Json;
using Discord;
using GithubBot.Discord;
using GithubBot.Services;

namespace GithubBot.Handlers;

public class AdoWorkItemHandler
{
    private readonly DiscordBotService _discord;
    private readonly UserMapService _userMap;
    private readonly IConfiguration _config;
    private readonly ILogger<AdoWorkItemHandler> _logger;

    public AdoWorkItemHandler(
        DiscordBotService discord,
        UserMapService userMap,
        IConfiguration config,
        ILogger<AdoWorkItemHandler> logger)
    {
        _discord = discord;
        _userMap = userMap;
        _config = config;
        _logger = logger;
    }

    public async Task HandleWorkItemCreatedAsync(JsonElement payload, CancellationToken ct = default)
    {
        var channelIdStr = _config["Discord:TicketChannelId"];
        if (!ulong.TryParse(channelIdStr, out var channelId) || channelId == 0)
        {
            _logger.LogWarning("[ADO] No ticket channel configured, skipping work item event");
            return;
        }

        // ADO workitem.created payload structure
        var resource = payload.TryGetProperty("resource", out var r) ? r : default;
        if (resource.ValueKind == JsonValueKind.Undefined) return;

        var fields = resource.TryGetProperty("fields", out var f) ? f : default;
        if (fields.ValueKind == JsonValueKind.Undefined) return;

        var id = resource.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
        var title = fields.TryGetProperty("System.Title", out var titleEl) ? titleEl.GetString() : null;
        var workItemType = fields.TryGetProperty("System.WorkItemType", out var typeEl) ? typeEl.GetString() : "Work Item";
        var state = fields.TryGetProperty("System.State", out var stateEl) ? stateEl.GetString() : null;
        var areaPath = fields.TryGetProperty("System.AreaPath", out var areaEl) ? areaEl.GetString() : null;
        var description = fields.TryGetProperty("System.Description", out var descEl) ? descEl.GetString() : null;
        var assignedToEmail = fields.TryGetProperty("System.AssignedTo", out var assignedToEl)
            ? (assignedToEl.ValueKind == JsonValueKind.Object
                ? (assignedToEl.TryGetProperty("uniqueName", out var un) ? un.GetString() : null)
                : assignedToEl.GetString())
            : null;
        var createdByEmail = fields.TryGetProperty("System.CreatedBy", out var createdByEl)
            ? (createdByEl.ValueKind == JsonValueKind.Object
                ? (createdByEl.TryGetProperty("uniqueName", out var un2) ? un2.GetString() : null)
                : createdByEl.GetString())
            : null;

        // Build the URL — ADO payload has a _links.html.href or we can build from resourceContainers
        string? workItemUrl = null;
        if (payload.TryGetProperty("_links", out var links) &&
            links.TryGetProperty("html", out var htmlLink) &&
            htmlLink.TryGetProperty("href", out var hrefEl))
            workItemUrl = hrefEl.GetString();

        if (workItemUrl == null && payload.TryGetProperty("resourceContainers", out var containers) &&
            containers.TryGetProperty("project", out var project) &&
            project.TryGetProperty("baseUrl", out var baseUrlEl))
        {
            var baseUrl = baseUrlEl.GetString()?.TrimEnd('/');
            if (baseUrl != null && id != 0)
                workItemUrl = $"{baseUrl}/_workitems/edit/{id}";
        }

        var (color, emoji) = workItemType switch
        {
            "Bug"         => (Color.Red,       "🐛"),
            "Task"        => (Color.Blue,      "✅"),
            "User Story"  => (Color.Green,     "📖"),
            "Epic"        => (Color.Purple,    "🏔"),
            "Feature"     => (Color.Orange,    "⭐"),
            _             => (Color.LightGrey, "📋"),
        };

        var embed = new EmbedBuilder()
            .WithTitle($"{emoji} {workItemType} #{id}: {title}")
            .WithColor(color)
            .WithUrl(workItemUrl);

        if (!string.IsNullOrWhiteSpace(state))
            embed.AddField("State", state, inline: true);

        if (!string.IsNullOrWhiteSpace(areaPath))
            embed.AddField("Area", areaPath, inline: true);

        if (!string.IsNullOrWhiteSpace(assignedToEmail))
        {
            var discordId = _userMap.AdoToDiscord(assignedToEmail);
            embed.AddField("Assigned To", discordId != null ? $"<@{discordId}>" : assignedToEmail, inline: true);
        }

        if (!string.IsNullOrWhiteSpace(createdByEmail))
        {
            var discordId = _userMap.AdoToDiscord(createdByEmail);
            embed.AddField("Created By", discordId != null ? $"<@{discordId}>" : createdByEmail, inline: true);
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            // Strip HTML tags from description
            var plainDesc = System.Text.RegularExpressions.Regex.Replace(description, "<[^>]+>", "").Trim();
            if (plainDesc.Length > 300) plainDesc = plainDesc[..300] + "…";
            if (!string.IsNullOrWhiteSpace(plainDesc))
                embed.WithDescription(plainDesc);
        }

        // Ping assigned user if mapped
        string? ping = null;
        if (!string.IsNullOrEmpty(assignedToEmail))
        {
            var discordId = _userMap.AdoToDiscord(assignedToEmail);
            if (discordId != null) ping = $"<@{discordId}>";
        }

        _logger.LogInformation("[ADO] Work item created #{Id} type={Type} title={Title}", id, workItemType, title);
        await _discord.SendMessageAsync(channelId, ping, embed.Build(), ct);
    }
}
