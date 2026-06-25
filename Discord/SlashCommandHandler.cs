using Discord;
using Discord.Rest;
using Discord.WebSocket;
using GithubBot.Handlers;
using GithubBot.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GithubBot.Discord;

public class SlashCommandHandler
{
    private readonly DiscordSocketClient _client;
    private readonly UserMapService _userMap;
    private readonly PrMapService _prMap;
    private readonly PreferencesService _prefs;
    private readonly ScoreService _scores;
    private readonly RouletteService _roulette;
    private readonly ConfigUiTokenService _configTokens;
    private readonly WorkItemMapService _workItemMap;
    private readonly AdoApiService? _adoApi;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly ILogger<SlashCommandHandler> _logger;
    private readonly bool _noAuth;
    private readonly ulong _channelId;

    public SlashCommandHandler(
        DiscordSocketClient client,
        UserMapService userMap,
        PrMapService prMap,
        PreferencesService prefs,
        ScoreService scores,
        RouletteService roulette,
        ConfigUiTokenService configTokens,
        WorkItemMapService workItemMap,
        IServiceProvider services,
        IConfiguration config,
        ILogger<SlashCommandHandler> logger,
        AdoApiService? adoApi = null)
    {
        _client = client;
        _userMap = userMap;
        _prMap = prMap;
        _prefs = prefs;
        _scores = scores;
        _roulette = roulette;
        _configTokens = configTokens;
        _workItemMap = workItemMap;
        _services = services;
        _adoApi = adoApi;
        _config = config;
        _logger = logger;
        _noAuth = config.GetValue<bool>("NoAuth");
        _channelId = ulong.TryParse(config["Discord:ChannelId"], out var id) ? id : 0;
    }

    // Bump this whenever the command definitions change.
    private const string CommandsVersion = "v15";
    private int _registering = 0;

    public async Task RegisterAsync()
    {
        if (Interlocked.CompareExchange(ref _registering, 1, 0) != 0) return;
        try
        {
        var guildIdStr = _config["Discord:GuildId"];
        if (!ulong.TryParse(guildIdStr, out var guildId)) return;

        var forceRegister = _config.GetValue<bool>("ForceRegisterCommands");
        if (!forceRegister && _prefs.GetCommandsVersion() == CommandsVersion)
        {
            _logger.LogInformation("Slash commands already at {Version}, skipping registration", CommandsVersion);
            return;
        }

        if (forceRegister)
            _logger.LogInformation("Force-registering slash commands (--ForceRegisterCommands)");

        _logger.LogInformation("Registering slash commands (stored={Stored}, current={Current})",
            _prefs.GetCommandsVersion() ?? "none", CommandsVersion);

        var rest = _client.Rest;

        try
        {
            var commands = new ApplicationCommandProperties[]
            {
                new SlashCommandBuilder()
                    .WithName("score")
                    .WithDescription("Show your score and stats, or view another user's score")
                    .AddOption("user", ApplicationCommandOptionType.User, "User to look up (defaults to yourself)", isRequired: false)
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("leaderboard")
                    .WithDescription("Show the top scorers")
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("leaderboard-verbose")
                    .WithDescription("Show leaderboard with per-category score breakdown")
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("configui")
                    .WithDescription("Generate a one-time link to the web config UI")
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("prroulette")
                    .WithDescription("Assign random users to review a PR; bonus points if they actually comment or review")
                    .AddOption("pr", ApplicationCommandOptionType.String, "PR to assign (start typing to search)", isRequired: true, isAutocomplete: true)
                    .AddOption("role", ApplicationCommandOptionType.Role, "Limit candidates to this role", isRequired: false)
                    .AddOption("count", ApplicationCommandOptionType.Integer, "Number of users to assign (default: 1)", isRequired: false)
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("botdelete")
                    .WithDescription("Delete one or more bot messages by ID")
                    .AddOption("message_ids", ApplicationCommandOptionType.String, "Space or comma-separated message IDs", isRequired: true)
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("listmappings")
                    .WithDescription("List Discord ↔ GitHub/DevOps mappings")
                    .AddOption("user", ApplicationCommandOptionType.User, "Show mappings for a specific user (omit for all)", isRequired: false)
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("trackticket")
                    .WithDescription("Fetch a ticket from ADO and start tracking it in Discord")
                    .AddOption("id", ApplicationCommandOptionType.Integer, "Work item ID", isRequired: true)
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("unassigned")
                    .WithDescription("List ADO tickets with no assignee")
                    .AddOption("min-priority", ApplicationCommandOptionType.Integer, "Only show tickets with this priority or higher (1=Critical, 4=Low)", isRequired: false,
                        choices: [new ApplicationCommandOptionChoiceProperties { Name = "1 – Critical", Value = 1L },
                                  new ApplicationCommandOptionChoiceProperties { Name = "2 – High",     Value = 2L },
                                  new ApplicationCommandOptionChoiceProperties { Name = "3 – Medium",   Value = 3L },
                                  new ApplicationCommandOptionChoiceProperties { Name = "4 – Low",      Value = 4L }])
                    .AddOption("max-size", ApplicationCommandOptionType.Integer, "Max story points / effort to include", isRequired: false)
                    .AddOption("area-path", ApplicationCommandOptionType.String, "Filter by area path (includes sub-paths)", isRequired: false, isAutocomplete: true)
                    .AddOption("type", ApplicationCommandOptionType.String, "Work item type (Bug, Task, User Story…)", isRequired: false, isAutocomplete: true)
                    .AddOption("state", ApplicationCommandOptionType.String, "Work item state (Active, New…)", isRequired: false, isAutocomplete: true)
                    .AddOption("created-after", ApplicationCommandOptionType.String, "Only show tickets created after (e.g. 7d, 30d, 90d, 1y)", isRequired: false, isAutocomplete: true)
                    .AddOption("order-by", ApplicationCommandOptionType.String, "Sort order", isRequired: false,
                        choices: [new ApplicationCommandOptionChoiceProperties { Name = "Priority",     Value = "priority" },
                                  new ApplicationCommandOptionChoiceProperties { Name = "Size",         Value = "size" },
                                  new ApplicationCommandOptionChoiceProperties { Name = "Created Date", Value = "created" },
                                  new ApplicationCommandOptionChoiceProperties { Name = "ID",           Value = "id" }])
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("untrackticket")
                    .WithDescription("Stop tracking a ticket — deletes the embed and archives the thread")
                    .AddOption("id", ApplicationCommandOptionType.Integer, "Work item ID to untrack", isRequired: true, isAutocomplete: true)
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("map-user")
                    .WithDescription("Map a Discord user to a GitHub or DevOps identity")
                    .AddOption("username", ApplicationCommandOptionType.String, "GitHub username or DevOps email/display name", isRequired: true)
                    .AddOption("platform", ApplicationCommandOptionType.String, "Platform to map to", isRequired: true,
                        choices: [new ApplicationCommandOptionChoiceProperties { Name = "GitHub", Value = "gh" },
                                  new ApplicationCommandOptionChoiceProperties { Name = "DevOps", Value = "ado" }])
                    .AddOption("user", ApplicationCommandOptionType.User, "Discord user to map (defaults to yourself)", isRequired: false)
                    .Build(),
            };

            await rest.BulkOverwriteGuildCommands(commands, guildId,
                new global::Discord.RequestOptions
                {
                    Timeout = 90000,
                    RetryMode = global::Discord.RetryMode.RetryRatelimit,
                });
            _prefs.SetCommandsVersion(CommandsVersion);
            _logger.LogInformation("Slash commands registered ({Version})", CommandsVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register slash commands");
        }
        }
        finally
        {
            Interlocked.Exchange(ref _registering, 0);
        }
    }

    public async Task HandleInteractionAsync(SocketInteraction interaction)
    {
        if (interaction is not SocketSlashCommand command) return;

        if (!_noAuth)
        {
            var requiredRole = command.Data.Name == "configui"
                ? _prefs.ResolveConfigRole(_config["Roles:Config"])
                : _prefs.ResolveCommandRole(_config["Roles:Command"]);
            if (!string.IsNullOrEmpty(requiredRole) && ulong.TryParse(requiredRole, out var roleId))
            {
                if (command.User is SocketGuildUser guildUser && !guildUser.Roles.Any(r => r.Id == roleId))
                {
                    await command.RespondAsync("You do not have permission to use this command.", ephemeral: true);
                    return;
                }
            }
        }

        switch (command.Data.Name)
        {
            case "score":
                await HandleScore(command);
                break;
            case "leaderboard":
                await HandleLeaderboard(command, verbose: false);
                break;
            case "leaderboard-verbose":
                await HandleLeaderboard(command, verbose: true);
                break;
            case "configui":
                await HandleConfigUi(command);
                break;
            case "prroulette":
                await HandlePrRoulette(command);
                break;
            case "botdelete":
                await HandleBotDelete(command);
                break;
            case "listmappings":
                await HandleListMappings(command);
                break;
            case "map-user":
                await HandleMapUser(command);
                break;
            case "unassigned":
                await HandleUnassigned(command);
                break;
            case "trackticket":
                await HandleTrackTicket(command);
                break;
            case "untrackticket":
                await HandleUntrackTicket(command);
                break;
        }
    }

    public async Task HandleAutocompleteAsync(SocketAutocompleteInteraction interaction)
    {
        var focused = interaction.Data.Options.FirstOrDefault(o => o.Focused);
        if (focused == null) return;
        var input = (focused.Value as string ?? "").ToLowerInvariant();

        if (interaction.Data.CommandName == "prroulette" && focused.Name == "pr")
        {
            var choices = _prMap.GetAll()
                .Where(kvp => kvp.Value.ClosedAt == null && kvp.Value.PrNumber != null)
                .Select(kvp => new
                {
                    NodeId = kvp.Key,
                    Label = $"#{kvp.Value.PrNumber} {kvp.Value.PrTitle ?? ""}".Trim(),
                    kvp.Value.PrNumber
                })
                .Where(x => string.IsNullOrEmpty(input) || x.Label.ToLowerInvariant().Contains(input))
                .OrderBy(x => x.PrNumber)
                .Take(25)
                .Select(x => new AutocompleteResult(x.Label.Length > 100 ? x.Label[..100] : x.Label, x.NodeId))
                .ToList();
            await interaction.RespondAsync(choices);
            return;
        }

        if (interaction.Data.CommandName == "trackticket" && focused.Name == "id" && _adoApi != null)
        {
            var summaries = await _adoApi.GetWorkItemSummariesAsync();
            var trackedIds = _workItemMap.GetAll().Keys
                .Select(k => int.TryParse(k, out var n) ? n : 0)
                .ToHashSet();

            var choices = summaries
                .Where(x => string.IsNullOrEmpty(input) ||
                             x.Id.ToString().Contains(input) ||
                             (x.Title?.ToLowerInvariant().Contains(input) ?? false))
                .OrderBy(x => trackedIds.Contains(x.Id) ? 1 : 0) // untracked first
                .ThenByDescending(x => x.Id)
                .Take(25)
                .Select(x =>
                {
                    var tracked = trackedIds.Contains(x.Id) ? " ✓" : "";
                    var label = $"#{x.Id}{(x.Type != null ? $" [{x.Type}]" : "")}{(x.Title != null ? $" — {x.Title}" : "")}{tracked}";
                    if (label.Length > 100) label = label[..100];
                    return new AutocompleteResult(label, (long)x.Id);
                })
                .ToList();
            await interaction.RespondAsync(choices);
            return;
        }

        if (interaction.Data.CommandName == "untrackticket" && focused.Name == "id")
        {
            var choices = _workItemMap.GetAll()
                .Select(kv => new { Id = kv.Key, kv.Value.Title, kv.Value.WorkItemType })
                .Where(x => string.IsNullOrEmpty(input) || x.Id.Contains(input) ||
                            (x.Title?.ToLowerInvariant().Contains(input) ?? false))
                .OrderBy(x => int.TryParse(x.Id, out var n) ? n : int.MaxValue)
                .Take(25)
                .Select(x =>
                {
                    var label = $"#{x.Id}{(x.WorkItemType != null ? $" [{x.WorkItemType}]" : "")}{(x.Title != null ? $" — {x.Title}" : "")}";
                    if (label.Length > 100) label = label[..100];
                    return new AutocompleteResult(label, long.TryParse(x.Id, out var v) ? (object)v : x.Id);
                })
                .ToList();
            await interaction.RespondAsync(choices);
            return;
        }

        if (interaction.Data.CommandName == "unassigned")
        {
            List<AutocompleteResult> suggestions = focused.Name switch
            {
                "type" => new[]
                {
                    "Bug", "Task", "User Story", "Feature", "Epic",
                    "Test Case", "Test Plan", "Test Suite", "Issue"
                }
                .Where(s => string.IsNullOrEmpty(input) || s.ToLowerInvariant().Contains(input))
                .Take(25)
                .Select(s => new AutocompleteResult(s, s))
                .ToList(),

                "state" => new[]
                {
                    "New", "Active", "Committed", "In Progress", "Resolved",
                    "Closed", "Done", "Removed", "To Do", "In Review"
                }
                .Where(s => string.IsNullOrEmpty(input) || s.ToLowerInvariant().Contains(input))
                .Take(25)
                .Select(s => new AutocompleteResult(s, s))
                .ToList(),

                "area-path" when _adoApi != null => (await _adoApi.GetAreaPathsAsync())
                    .Where(p => string.IsNullOrEmpty(input) || p.ToLowerInvariant().Contains(input))
                    .Take(25)
                    .Select(p => new AutocompleteResult(p, p))
                    .ToList(),

                "created-after" => new[]
                {
                    ("7d",   "Last 7 days"),
                    ("14d",  "Last 14 days"),
                    ("30d",  "Last 30 days"),
                    ("60d",  "Last 60 days"),
                    ("90d",  "Last 90 days"),
                    ("180d", "Last 180 days"),
                    ("1y",   "Last year"),
                }
                .Where(s => string.IsNullOrEmpty(input) || s.Item1.Contains(input) || s.Item2.ToLowerInvariant().Contains(input))
                .Take(25)
                .Select(s => new AutocompleteResult(s.Item2, s.Item1))
                .ToList(),

                _ => []
            };
            await interaction.RespondAsync(suggestions);
        }
    }

    private async Task HandleConfigUi(SocketSlashCommand command)
    {
        var displayName = (command.User as SocketGuildUser)?.DisplayName ?? command.User.GlobalName ?? command.User.Username;
        var token = _configTokens.GenerateToken(displayName);
        var port = _config.GetValue<int?>("Port") ?? 3000;
        var host = _config["PublicHost"] ?? $"http://localhost:{port}";
        var url = $"{host.TrimEnd('/')}/config?token={token}";

        await command.RespondAsync(ephemeral: true, embeds: [new EmbedBuilder()
            .WithColor(new Color(0x7c3aed))
            .WithTitle("Config UI")
            .WithDescription($"[Open user mapping editor]({url})\n\nThis link is **one-time use** and valid for the duration of your browser session.")
            .WithCurrentTimestamp()
            .Build()]);
    }

    private async Task HandlePrRoulette(SocketSlashCommand command)
    {
        var prNodeId = (string)command.Data.Options.First(o => o.Name == "pr").Value;
        var role = command.Data.Options.FirstOrDefault(o => o.Name == "role")?.Value as SocketRole;
        var count = command.Data.Options.FirstOrDefault(o => o.Name == "count")?.Value is long c ? (int)c : 1;
        if (count < 1) count = 1;

        var prEntry = _prMap.Get(prNodeId);
        if (prEntry == null)
        {
            await command.RespondAsync("PR not found in the map.", ephemeral: true);
            return;
        }

        var guildId = ulong.TryParse(_config["Discord:GuildId"], out var gid) ? gid : 0UL;
        var guild = _client.GetGuild(guildId);
        if (guild == null)
        {
            await command.RespondAsync("Could not resolve the guild.", ephemeral: true);
            return;
        }

        // Build candidate pool: mapped Discord users that are guild members and not excluded
        var candidates = _userMap.GetAll().Keys
            .Select(id => ulong.TryParse(id, out var uid) ? guild.GetUser(uid) : null)
            .Where(u => u != null)
            .Cast<SocketGuildUser>()
            .Where(u => !_prefs.IsRouletteExcluded(u.Id.ToString()))
            .Where(u => role == null || u.Roles.Any(r => r.Id == role.Id))
            .ToList();

        if (candidates.Count == 0)
        {
            await command.RespondAsync("No mapped users found" + (role != null ? $" with the {role.Mention} role" : "") + ".", ephemeral: true);
            return;
        }

        var rng = new Random();
        var picked = candidates.OrderBy(_ => rng.Next()).Take(count).ToList();
        var pickedIds = picked.Select(u => u.Id.ToString()).ToList();

        _roulette.Assign(prNodeId, pickedIds);

        var prLabel = prEntry.PrNumber != null
            ? $"PR #{prEntry.PrNumber}{(prEntry.PrTitle != null ? $" — {prEntry.PrTitle}" : "")}"
            : "the PR";

        var pings = string.Join(' ', picked.Select(u => $"<@{u.Id}>"));
        var plural = picked.Count == 1 ? "has" : "have";
        var roleNote = role != null ? $" from {role.Mention}" : "";

        var embed = new EmbedBuilder()
            .WithTitle("PR Roulette 🎰")
            .WithColor(new Color(0xe91e63))
            .WithDescription(
                $"{pings}\n\nYou {plural} been selected{roleNote} to review **{prLabel}**!")
            .WithCurrentTimestamp()
            .Build();

        // Post the roulette message in the PR thread if available, else fall back to ephemeral
        if (prEntry.ThreadId is ulong threadId && threadId != 0)
        {
            await command.RespondAsync("Roulette assigned! Pinging in the PR thread.", ephemeral: true);
            var channel = _client.GetChannel(threadId) as IMessageChannel;
            if (channel != null)
                await channel.SendMessageAsync(pings, embed: embed);
        }
        else
        {
            await command.RespondAsync(pings, embeds: [embed], ephemeral: false);
        }
    }

    private async Task HandleBotDelete(SocketSlashCommand command)
    {
        var raw = command.Data.Options.FirstOrDefault(o => o.Name == "message_ids")?.Value as string ?? "";
        var ids = raw.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);

        if (ids.Length == 0)
        {
            await command.RespondAsync("No message IDs provided.", ephemeral: true);
            return;
        }

        var channel = command.Channel;
        var botId = _client.CurrentUser?.Id ?? 0;
        int deleted = 0, skipped = 0;

        await command.DeferAsync(ephemeral: true);

        foreach (var idStr in ids)
        {
            if (!ulong.TryParse(idStr.Trim(), out var msgId)) { skipped++; continue; }
            try
            {
                var msg = await channel.GetMessageAsync(msgId);
                if (msg == null || msg.Author.Id != botId) { skipped++; continue; }
                await msg.DeleteAsync();
                deleted++;
            }
            catch { skipped++; }
        }

        var reply = deleted > 0
            ? $"Deleted {deleted} message{(deleted == 1 ? "" : "s")}."
            : "No bot messages were deleted.";
        if (skipped > 0) reply += $" {skipped} ID{(skipped == 1 ? "" : "s")} skipped (not found, not mine, or invalid).";

        await command.FollowupAsync(reply, ephemeral: true);
    }

    private async Task HandleListMappings(SocketSlashCommand command)
    {
        var map = _userMap.GetAll();
        var targetUser = command.Data.Options.FirstOrDefault(o => o.Name == "user")?.Value as SocketUser;

        if (targetUser != null)
        {
            var discordId = targetUser.Id.ToString();
            if (!map.TryGetValue(discordId, out var entry) || (entry.Gh.Count == 0 && entry.Ado.Count == 0))
            {
                await command.RespondAsync($"<@{discordId}> has no mappings.", ephemeral: true);
                return;
            }

            var eb = new EmbedBuilder()
                .WithTitle($"Mappings for {targetUser.Username}")
                .WithColor(new Color(0x5865F2));
            if (entry.Gh.Count > 0)  eb.AddField("GitHub", string.Join("\n", entry.Gh));
            if (entry.Ado.Count > 0) eb.AddField("DevOps", string.Join("\n", entry.Ado));
            await command.RespondAsync(ephemeral: true, embeds: [eb.Build()]);
        }
        else
        {
            if (map.Count == 0)
            {
                await command.RespondAsync("No mappings configured yet.", ephemeral: true);
                return;
            }

            var lines = new List<string>();
            foreach (var (discordId, entry) in map)
            {
                var parts = new List<string>();
                if (entry.Gh.Count > 0)  parts.Add($"GH: {string.Join(", ", entry.Gh)}");
                if (entry.Ado.Count > 0) parts.Add($"ADO: {string.Join(", ", entry.Ado)}");
                if (parts.Count > 0)
                    lines.Add($"<@{discordId}> — {string.Join(" · ", parts)}");
            }

            await command.RespondAsync(ephemeral: true, embeds: [new EmbedBuilder()
                .WithTitle("User Mappings")
                .WithColor(new Color(0x5865F2))
                .WithDescription(string.Join('\n', lines))
                .Build()]);
        }
    }

    private static DateTime? ParseRelativeDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim().ToLowerInvariant();
        if (raw.EndsWith('y') && int.TryParse(raw[..^1], out var years))
            return DateTime.UtcNow.AddYears(-years);
        if (raw.EndsWith('d') && int.TryParse(raw[..^1], out var days))
            return DateTime.UtcNow.AddDays(-days);
        if (DateTime.TryParse(raw, out var dt))
            return dt.ToUniversalTime();
        return null;
    }

    private async Task HandleTrackTicket(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);
        var id = Convert.ToInt32(command.Data.Options.FirstOrDefault(o => o.Name == "id")?.Value ?? 0L);
        if (id <= 0)
        {
            await command.ModifyOriginalResponseAsync(m => m.Content = "Invalid ticket ID.");
            return;
        }
        var result = await _services.GetRequiredService<AdoWorkItemHandler>().TrackWorkItemAsync(id);
        await command.ModifyOriginalResponseAsync(m => m.Content = result);
    }

    private async Task HandleUntrackTicket(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);
        var id = Convert.ToInt32(command.Data.Options.FirstOrDefault(o => o.Name == "id")?.Value ?? 0L);
        if (id <= 0)
        {
            await command.ModifyOriginalResponseAsync(m => m.Content = "Invalid ticket ID.");
            return;
        }
        var result = await _services.GetRequiredService<AdoWorkItemHandler>().UntrackWorkItemAsync(id);
        await command.ModifyOriginalResponseAsync(m => m.Content = result);
    }

    private async Task HandleUnassigned(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        var ticketChannelId = ulong.TryParse(_config["Discord:TicketChannelId"], out var tcid) ? tcid : _channelId;
        var guildId = (command.Channel as SocketGuildChannel)?.Guild.Id;

        var minPriority  = command.Data.Options.FirstOrDefault(o => o.Name == "min-priority")?.Value is long mp ? (int?)mp : null;
        var maxSize      = command.Data.Options.FirstOrDefault(o => o.Name == "max-size")?.Value is long ms ? (double?)ms : null;
        var areaPath     = command.Data.Options.FirstOrDefault(o => o.Name == "area-path")?.Value as string;
        var type         = command.Data.Options.FirstOrDefault(o => o.Name == "type")?.Value as string;
        var state        = command.Data.Options.FirstOrDefault(o => o.Name == "state")?.Value as string;
        var orderBy      = command.Data.Options.FirstOrDefault(o => o.Name == "order-by")?.Value as string;
        var createdAfterRaw = command.Data.Options.FirstOrDefault(o => o.Name == "created-after")?.Value as string;
        var createdAfter = ParseRelativeDate(createdAfterRaw);

        string body;
        int count;

        if (_adoApi != null)
        {
            var items = await _adoApi.GetUnassignedWorkItemsAsync(minPriority, maxSize, areaPath, type, state, createdAfter, orderBy);
            count = items.Count;
            if (count == 0)
            {
                await command.ModifyOriginalResponseAsync(m => m.Content = "No unassigned active tickets.");
                return;
            }

            var priorityLabel = new[] { "", "🔴 Critical", "🟠 High", "🟡 Medium", "🟢 Low" };
            var lines = items.Select(wi =>
            {
                var tracked = _workItemMap.Get(wi.Id);
                var threadLink = tracked?.ThreadId.HasValue == true
                    ? $"<#{tracked.ThreadId!.Value}>"
                    : tracked != null && tracked.MessageId != 0 && guildId.HasValue
                        ? $"https://discord.com/channels/{guildId}/{ticketChannelId}/{tracked.MessageId}"
                        : null;
                var type  = string.IsNullOrEmpty(wi.WorkItemType) ? "" : $" [{wi.WorkItemType}]";
                var title = string.IsNullOrEmpty(wi.Title) ? "" : $" — {wi.Title}";
                var prio  = wi.Priority is >= 1 and <= 4 ? $" {priorityLabel[wi.Priority.Value]}" : "";
                var size  = wi.Size.HasValue ? $" `{wi.Size.Value}pts`" : "";
                var link  = threadLink != null ? $" {threadLink}" : "";
                return $"**#{wi.Id}**{type}{title}{prio}{size}{link}";
            });
            body = string.Join("\n", lines);
        }
        else
        {
            // Fallback: local map only (tickets seen since last deploy)
            var unassigned = _workItemMap.GetAll()
                .Where(kv => string.IsNullOrEmpty(kv.Value.AssignedToEmail))
                .OrderBy(kv => int.TryParse(kv.Key, out var n) ? n : int.MaxValue)
                .ToList();
            count = unassigned.Count;
            if (count == 0)
            {
                await command.ModifyOriginalResponseAsync(m => m.Content = "No unassigned tracked tickets.");
                return;
            }
            var lines = unassigned.Select(kv =>
            {
                var e = kv.Value;
                var threadLink = e.ThreadId.HasValue
                    ? $"<#{e.ThreadId.Value}>"
                    : e.MessageId != 0 && guildId.HasValue ? $"https://discord.com/channels/{guildId}/{ticketChannelId}/{e.MessageId}" : null;
                var type = string.IsNullOrEmpty(e.WorkItemType) ? "" : $" [{e.WorkItemType}]";
                var title = string.IsNullOrEmpty(e.Title) ? "" : $" — {e.Title}";
                var link = threadLink != null ? $" {threadLink}" : "";
                return $"**#{kv.Key}**{type}{title}{link}";
            });
            body = string.Join("\n", lines) + "\n\n*⚠️ ADO API not configured — showing locally tracked tickets only.*";
        }

        if (body.Length > 3900) body = body[..3900] + "\n…";

        var filters = new List<string>();
        if (minPriority.HasValue)          filters.Add($"prio ≤ {minPriority}");
        if (maxSize.HasValue)              filters.Add($"size ≤ {maxSize}");
        if (!string.IsNullOrEmpty(areaPath))      filters.Add(areaPath);
        if (!string.IsNullOrEmpty(type))           filters.Add(type);
        if (!string.IsNullOrEmpty(state))          filters.Add(state);
        if (!string.IsNullOrEmpty(createdAfterRaw)) filters.Add($"created {createdAfterRaw}");
        var filterSuffix = filters.Count > 0 ? $" — {string.Join(", ", filters)}" : "";

        var embed = new EmbedBuilder()
            .WithTitle($"🎫 Unassigned Tickets ({count}){filterSuffix}")
            .WithDescription(body)
            .WithColor(Color.Orange)
            .Build();

        await command.ModifyOriginalResponseAsync(m => { m.Content = ""; m.Embed = embed; });
    }

    private async Task HandleMapUser(SocketSlashCommand command)
    {
        var username = command.Data.Options.FirstOrDefault(o => o.Name == "username")?.Value as string ?? "";
        var platform = command.Data.Options.FirstOrDefault(o => o.Name == "platform")?.Value as string ?? "gh";
        var targetUser = command.Data.Options.FirstOrDefault(o => o.Name == "user")?.Value as SocketUser ?? command.User;

        username = username.Trim();
        if (string.IsNullOrEmpty(username))
        {
            await command.RespondAsync("Username cannot be empty.", ephemeral: true);
            return;
        }

        var discordId = targetUser.Id.ToString();
        var map = _userMap.GetAll();
        if (!map.TryGetValue(discordId, out var entry)) entry = new();

        var list = platform == "ado" ? entry.Ado : entry.Gh;
        if (list.Any(n => n.Equals(username, StringComparison.OrdinalIgnoreCase)))
        {
            await command.RespondAsync($"<@{discordId}> is already mapped to `{username}` on {(platform == "ado" ? "DevOps" : "GitHub")}.", ephemeral: true);
            return;
        }

        list.Add(username);
        map[discordId] = entry;
        _userMap.Save(map);

        var platformLabel = platform == "ado" ? "DevOps" : "GitHub";
        await command.RespondAsync($"Mapped <@{discordId}> → `{username}` ({platformLabel}).", ephemeral: true);
    }

    private async Task HandleScore(SocketSlashCommand command)
    {
        var targetUser = command.Data.Options.FirstOrDefault(o => o.Name == "user")?.Value as SocketUser ?? command.User;
        var entry = _scores.GetScore(targetUser.Id.ToString());

        if (entry == null)
        {
            await command.RespondAsync($"<@{targetUser.Id}> has no score yet.", ephemeral: true);
            return;
        }

        var isSelf = targetUser.Id == command.User.Id;
        var title = isSelf ? "Your Score" : $"{targetUser.GlobalName ?? targetUser.Username}'s Score";

        await command.RespondAsync(ephemeral: true, embeds: [new EmbedBuilder()
            .WithTitle(title)
            .WithColor(new Color(0xF1C40F))
            .WithThumbnailUrl(targetUser.GetAvatarUrl() ?? targetUser.GetDefaultAvatarUrl())
            .AddField("Total", $"**{entry.Total}** pts", inline: true)
            .AddField("​", "​", inline: true)
            .AddField("​", "​", inline: true)
            .AddField("PR Opened",      $"{entry.PrOpened} pts ({entry.PrOpened / ScoreService.PointsPrOpened} PR{(entry.PrOpened / ScoreService.PointsPrOpened == 1 ? "" : "s")})", inline: true)
            .AddField("PR Merged",      $"{entry.PrMerged} pts ({entry.PrMerged / ScoreService.PointsPrMerged} PR{(entry.PrMerged / ScoreService.PointsPrMerged == 1 ? "" : "s")})", inline: true)
            .AddField("Reviews",        $"{entry.ReviewSubmitted} pts ({entry.ReviewSubmitted / ScoreService.PointsReview} review{(entry.ReviewSubmitted / ScoreService.PointsReview == 1 ? "" : "s")})", inline: true)
            .AddField("Comments",       $"{entry.Comments} pts ({entry.Comments / ScoreService.PointsComment} comment{(entry.Comments / ScoreService.PointsComment == 1 ? "" : "s")})", inline: true)
            .AddField("Ticket Created", $"{entry.TicketCreated} pts", inline: true)
            .AddField("Ticket Resolved",$"{entry.TicketResolved} pts", inline: true)
            .AddField("Ticket Comments",$"{entry.TicketComments} pts", inline: true)
            .AddField("Roulette Bonus", $"{entry.Bonus} pts", inline: true)
            .WithCurrentTimestamp()
            .Build()]);
    }

    private async Task HandleLeaderboard(SocketSlashCommand command, bool verbose = false)
    {
        var board = _scores.GetLeaderboard().Take(10).ToList();

        if (board.Count == 0)
        {
            await command.RespondAsync("No scores recorded yet.", ephemeral: true);
            return;
        }

        var medals = new[] { "🥇", "🥈", "🥉" };

        if (!verbose)
        {
            var lines = board.Select((entry, i) =>
            {
                var prefix = i < medals.Length ? medals[i] : $"**#{i + 1}**";
                return $"{prefix} <@{entry.DiscordId}> — **{entry.Entry.Total}** pts";
            });

            await command.RespondAsync(ephemeral: true, embeds: [new EmbedBuilder()
                .WithTitle("Leaderboard")
                .WithColor(new Color(0xF1C40F))
                .WithDescription(string.Join('\n', lines))
                .WithCurrentTimestamp()
                .Build()]);
        }
        else
        {
            var embed = new EmbedBuilder()
                .WithTitle("Leaderboard")
                .WithColor(new Color(0xF1C40F))
                .WithCurrentTimestamp();

            foreach (var (entry, i) in board.Select((e, i) => (e, i)))
            {
                var prefix = i < medals.Length ? medals[i] : $"#{i + 1}";
                var value = $"{prefix} <@{entry.DiscordId}> — **{entry.Entry.Total} pts**\n" +
                            $"PRs: {entry.Entry.PrOpened / ScoreService.PointsPrOpened} opened · {entry.Entry.PrMerged / ScoreService.PointsPrMerged} merged · " +
                            $"Reviews: {entry.Entry.ReviewSubmitted / ScoreService.PointsReview} · Comments: {entry.Entry.Comments / ScoreService.PointsComment}\n" +
                            $"Tickets: {entry.Entry.TicketCreated / ScoreService.PointsTicketCreated} created · " +
                            $"{entry.Entry.TicketResolved} resolved pts · " +
                            $"{entry.Entry.TicketComments / ScoreService.PointsTicketComment} ticket comments";
                embed.AddField("​", value);
            }

            await command.RespondAsync(ephemeral: true, embeds: [embed.Build()]);
        }
    }

    private async Task BackfillMappingAsync(string githubLogin, string discordId)
    {
        try
        {
            if (_channelId == 0) return;
            var oldText = $"**{githubLogin}**";
            var newText = $"<@{discordId}>";

            var channel = _client.GetChannel(_channelId) as IMessageChannel;
            if (channel == null) return;

            foreach (var (_, entry) in _prMap.GetAll())
            {
                IMessage? msg;
                try { msg = await channel.GetMessageAsync(entry.MessageId); }
                catch { continue; }

                if (msg?.Embeds == null || msg.Embeds.Count == 0) continue;

                var embed = msg.Embeds.First();
                if (!EmbedContains(embed, oldText)) continue;

                var updated = ReplaceInEmbed(embed, oldText, newText);
                var newContent = msg.Content?.Replace(oldText, newText);

                if (msg is IUserMessage userMsg)
                    await userMsg.ModifyAsync(props =>
                    {
                        if (newContent != null) props.Content = newContent;
                        props.Embeds = new[] { updated };
                    });

                _logger.LogInformation("[Backfill] Updated message {MsgId} for {Login}", entry.MessageId, githubLogin);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Backfill] Failed for {Login}", githubLogin);
        }
    }

    private static bool EmbedContains(IEmbed embed, string text)
        => embed.Fields.Any(f => f.Value.Contains(text) || f.Name.Contains(text))
        || (embed.Description?.Contains(text) ?? false);

    private static Embed ReplaceInEmbed(IEmbed src, string oldText, string newText)
    {
        string Replace(string? s) => s?.Replace(oldText, newText) ?? "";

        var builder = new EmbedBuilder()
            .WithTitle(src.Title)
            .WithUrl(src.Url)
            .WithDescription(Replace(src.Description))
            .WithFooter(src.Footer?.Text)
            .WithImageUrl(src.Image?.Url)
            .WithThumbnailUrl(src.Thumbnail?.Url);

        if (src.Color.HasValue)
            builder.WithColor(src.Color.Value);

        if (src.Author != null)
            builder.WithAuthor(src.Author.Value.Name, src.Author.Value.IconUrl, src.Author.Value.Url);

        if (src.Timestamp.HasValue)
            builder.WithTimestamp(src.Timestamp.Value);

        foreach (var field in src.Fields)
            builder.AddField(Replace(field.Name), Replace(field.Value), field.Inline);

        return builder.Build();
    }
}
