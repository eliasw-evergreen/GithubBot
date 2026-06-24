using Discord;
using Discord.Rest;
using Discord.WebSocket;
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
        IConfiguration config,
        ILogger<SlashCommandHandler> logger)
    {
        _client = client;
        _userMap = userMap;
        _prMap = prMap;
        _prefs = prefs;
        _scores = scores;
        _roulette = roulette;
        _configTokens = configTokens;
        _config = config;
        _logger = logger;
        _noAuth = config.GetValue<bool>("NoAuth");
        _channelId = ulong.TryParse(config["Discord:ChannelId"], out var id) ? id : 0;
    }

    // Bump this whenever the command definitions change.
    private const string CommandsVersion = "v8";
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
        }
    }

    public async Task HandleAutocompleteAsync(SocketAutocompleteInteraction interaction)
    {
        if (interaction.Data.CommandName != "prroulette") return;
        var focused = interaction.Data.Options.FirstOrDefault(o => o.Focused);
        if (focused?.Name != "pr") return;

        var input = (focused.Value as string ?? "").ToLowerInvariant();

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
