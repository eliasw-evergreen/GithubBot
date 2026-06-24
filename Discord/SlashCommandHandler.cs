using Discord;
using Discord.Rest;
using Discord.WebSocket;
using GithubBot.Services;

namespace GithubBot.Discord;

public class SlashCommandHandler
{
    private readonly DiscordSocketClient _client;
    private readonly UserMapService _userMap;
    private readonly PrMapService _prMap;
    private readonly PreferencesService _prefs;
    private readonly ScoreService _scores;
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
        IConfiguration config,
        ILogger<SlashCommandHandler> logger)
    {
        _client = client;
        _userMap = userMap;
        _prMap = prMap;
        _prefs = prefs;
        _scores = scores;
        _config = config;
        _logger = logger;
        _noAuth = config.GetValue<bool>("NoAuth");
        _channelId = ulong.TryParse(config["Discord:ChannelId"], out var id) ? id : 0;
    }

    public async Task RegisterAsync()
    {
        var guildIdStr = _config["Discord:GuildId"];
        if (!ulong.TryParse(guildIdStr, out var guildId)) return;

        var rest = _client.Rest;

        try
        {
            var existing = await rest.GetGuildApplicationCommands(guildId);
            foreach (var cmd in existing) await cmd.DeleteAsync();

            await rest.CreateGuildCommand(
                new SlashCommandBuilder()
                    .WithName("mapuser")
                    .WithDescription("Map a GitHub username or DevOps email to a Discord user")
                    .AddOption("discord_user", ApplicationCommandOptionType.User, "The Discord user", isRequired: true)
                    .AddOption("username", ApplicationCommandOptionType.String, "GitHub username or DevOps email", isRequired: true)
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("type")
                        .WithDescription("Account type to map")
                        .WithRequired(false)
                        .WithType(ApplicationCommandOptionType.String)
                        .AddChoice("GitHub", "github")
                        .AddChoice("DevOps", "devops"))
                    .Build(),
                guildId);

            await rest.CreateGuildCommand(
                new SlashCommandBuilder()
                    .WithName("unmapuser")
                    .WithDescription("Remove a username mapping from a Discord user")
                    .AddOption("discord_user", ApplicationCommandOptionType.User, "The Discord user", isRequired: true)
                    .AddOption("username", ApplicationCommandOptionType.String,
                        "Specific username/email to remove (leave blank to remove all)", isRequired: false)
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("type")
                        .WithDescription("Account type to remove")
                        .WithRequired(false)
                        .WithType(ApplicationCommandOptionType.String)
                        .AddChoice("GitHub", "github")
                        .AddChoice("DevOps", "devops"))
                    .Build(),
                guildId);

            await rest.CreateGuildCommand(
                new SlashCommandBuilder()
                    .WithName("listmappings")
                    .WithDescription("Show all Discord <-> GitHub/DevOps user mappings")
                    .Build(),
                guildId);

            await rest.CreateGuildCommand(
                new SlashCommandBuilder()
                    .WithName("setreaction")
                    .WithDescription("Override a reaction emoji for yourself")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("event")
                        .WithDescription("The event to set a reaction for")
                        .WithRequired(true)
                        .WithType(ApplicationCommandOptionType.String)
                        .AddChoice("Opened", "opened")
                        .AddChoice("Reopened", "reopened")
                        .AddChoice("Ready for Review", "ready_for_review")
                        .AddChoice("Converted to Draft", "converted_to_draft")
                        .AddChoice("Merged", "merged")
                        .AddChoice("Closed", "closed")
                        .AddChoice("Approved", "approved")
                        .AddChoice("Changes Requested", "changes_requested")
                        .AddChoice("Review Requested", "review_requested")
                        .AddChoice("Assigned", "assigned")
                        .AddChoice("Comment", "comment"))
                    .AddOption("emoji", ApplicationCommandOptionType.String, "Emoji or custom emote ID to use", isRequired: true)
                    .Build(),
                guildId);

            await rest.CreateGuildCommand(
                new SlashCommandBuilder()
                    .WithName("clearreaction")
                    .WithDescription("Remove your reaction override and use the server default")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("event")
                        .WithDescription("The event to clear")
                        .WithRequired(true)
                        .WithType(ApplicationCommandOptionType.String)
                        .AddChoice("Opened", "opened")
                        .AddChoice("Reopened", "reopened")
                        .AddChoice("Ready for Review", "ready_for_review")
                        .AddChoice("Converted to Draft", "converted_to_draft")
                        .AddChoice("Merged", "merged")
                        .AddChoice("Closed", "closed")
                        .AddChoice("Approved", "approved")
                        .AddChoice("Changes Requested", "changes_requested")
                        .AddChoice("Review Requested", "review_requested")
                        .AddChoice("Assigned", "assigned")
                        .AddChoice("Comment", "comment"))
                    .Build(),
                guildId);

            await rest.CreateGuildCommand(
                new SlashCommandBuilder()
                    .WithName("listreactions")
                    .WithDescription("Show all active reactions (preferences override .env)")
                    .Build(),
                guildId);

            await rest.CreateGuildCommand(
                new SlashCommandBuilder()
                    .WithName("setpingrole")
                    .WithDescription("Set the role to ping when a PR is opened or merged")
                    .AddOption("role", ApplicationCommandOptionType.Role, "The role to ping", isRequired: true)
                    .Build(),
                guildId);

            await rest.CreateGuildCommand(
                new SlashCommandBuilder()
                    .WithName("clearpingrole")
                    .WithDescription("Clear the ping role override and fall back to .env")
                    .Build(),
                guildId);

            await rest.CreateGuildCommand(
                new SlashCommandBuilder()
                    .WithName("score")
                    .WithDescription("Show your score and stats, or view another user's score")
                    .AddOption("user", ApplicationCommandOptionType.User, "User to look up (defaults to yourself)", isRequired: false)
                    .Build(),
                guildId);

            await rest.CreateGuildCommand(
                new SlashCommandBuilder()
                    .WithName("leaderboard")
                    .WithDescription("Show the top scorers")
                    .AddOption("verbose", ApplicationCommandOptionType.Boolean, "Show per-category breakdown for each user", isRequired: false)
                    .Build(),
                guildId);

            _logger.LogInformation("Slash commands registered");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register slash commands");
        }
    }

    public async Task HandleInteractionAsync(SocketInteraction interaction)
    {
        if (interaction is not SocketSlashCommand command) return;

        if (!_noAuth)
        {
            var configRole = _config["Roles:Config"];
            if (!string.IsNullOrEmpty(configRole) && ulong.TryParse(configRole, out var roleId))
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
            case "mapuser":
                await HandleMapUser(command);
                break;
            case "unmapuser":
                await HandleUnmapUser(command);
                break;
            case "listmappings":
                await HandleListMappings(command);
                break;
            case "setreaction":
                await HandleSetReaction(command);
                break;
            case "clearreaction":
                await HandleClearReaction(command);
                break;
            case "listreactions":
                await HandleListReactions(command);
                break;
            case "setpingrole":
                await HandleSetPingRole(command);
                break;
            case "clearpingrole":
                await HandleClearPingRole(command);
                break;
            case "score":
                await HandleScore(command);
                break;
            case "leaderboard":
                await HandleLeaderboard(command);
                break;
        }
    }

    private async Task HandleMapUser(SocketSlashCommand command)
    {
        var discordUser = (SocketUser)command.Data.Options.First(o => o.Name == "discord_user").Value;
        var username = ((string)command.Data.Options.First(o => o.Name == "username").Value).Trim();
        var type = command.Data.Options.FirstOrDefault(o => o.Name == "type")?.Value as string ?? "github";
        var isAdo = type == "devops";
        var storedKey = isAdo ? UserMapService.Encode(username) : username;

        var map = _userMap.GetAll();
        if (!map.TryGetValue(discordUser.Id.ToString(), out var existing))
            existing = [];

        if (existing.Any(n => n.Equals(storedKey, StringComparison.OrdinalIgnoreCase)))
        {
            await command.RespondAsync($"**{username}** is already mapped to <@{discordUser.Id}>.", ephemeral: true);
            return;
        }

        existing.Add(storedKey);
        map[discordUser.Id.ToString()] = existing;
        _userMap.Save(map);

        var all = string.Join(", ", existing.Select(FormatMappingEntry));
        await command.RespondAsync(ephemeral: true, embeds: [new EmbedBuilder()
            .WithColor(Color.Green)
            .WithDescription($"<@{discordUser.Id}> is now mapped to: {all}")
            .WithCurrentTimestamp()
            .Build()]);

        if (!isAdo)
            _ = Task.Run(() => BackfillMappingAsync(username, discordUser.Id.ToString()));
    }

    private async Task HandleUnmapUser(SocketSlashCommand command)
    {
        var discordUser = (SocketUser)command.Data.Options.First(o => o.Name == "discord_user").Value;
        var username = command.Data.Options.FirstOrDefault(o => o.Name == "username")?.Value as string;
        var type = command.Data.Options.FirstOrDefault(o => o.Name == "type")?.Value as string ?? "github";
        var map = _userMap.GetAll();

        if (!map.TryGetValue(discordUser.Id.ToString(), out var existing) || existing.Count == 0)
        {
            await command.RespondAsync("No mapping found for that user.", ephemeral: true);
            return;
        }

        if (!string.IsNullOrEmpty(username))
        {
            var storedKey = type == "devops" ? UserMapService.Encode(username) : username;
            var updated = existing.Where(n => !n.Equals(storedKey, StringComparison.OrdinalIgnoreCase)).ToList();
            if (updated.Count == existing.Count)
            {
                await command.RespondAsync($"**{username}** was not mapped to <@{discordUser.Id}>.", ephemeral: true);
                return;
            }
            if (updated.Count == 0)
                map.Remove(discordUser.Id.ToString());
            else
                map[discordUser.Id.ToString()] = updated;
            _userMap.Save(map);
            await command.RespondAsync(ephemeral: true, embeds: [new EmbedBuilder()
                .WithColor(Color.Orange)
                .WithDescription($"Removed **{username}** from <@{discordUser.Id}>'s mappings")
                .WithCurrentTimestamp()
                .Build()]);
        }
        else
        {
            map.Remove(discordUser.Id.ToString());
            _userMap.Save(map);
            await command.RespondAsync(ephemeral: true, embeds: [new EmbedBuilder()
                .WithColor(Color.Orange)
                .WithDescription($"Removed all mappings for <@{discordUser.Id}>")
                .WithCurrentTimestamp()
                .Build()]);
        }
    }

    private async Task HandleListMappings(SocketSlashCommand command)
    {
        var entries = _userMap.GetAll();
        if (entries.Count == 0)
        {
            await command.RespondAsync("No mappings configured yet.", ephemeral: true);
            return;
        }

        var lines = entries.Select(kvp =>
        {
            var links = string.Join(", ", kvp.Value.Select(FormatMappingEntry));
            return $"<@{kvp.Key}> → {links}";
        });

        await command.RespondAsync(ephemeral: true, embeds: [new EmbedBuilder()
            .WithTitle("Discord ↔ GitHub / DevOps Mappings")
            .WithColor(new Color(0x5865F2))
            .WithDescription(string.Join('\n', lines))
            .WithCurrentTimestamp()
            .Build()]);
    }

    private static string FormatMappingEntry(string storedKey) => UserMapService.Label(storedKey);

    private async Task HandleSetReaction(SocketSlashCommand command)
    {
        var eventKey = (string)command.Data.Options.First(o => o.Name == "event").Value;
        var emoji = ((string)command.Data.Options.First(o => o.Name == "emoji").Value).Trim();

        _prefs.SetReaction(eventKey, emoji);

        await command.RespondAsync(ephemeral: true, embeds: [new EmbedBuilder()
            .WithColor(Color.Green)
            .WithDescription($"Reaction for **{EventLabel(eventKey)}** is now set to {emoji}")
            .WithCurrentTimestamp()
            .Build()]);
    }

    private async Task HandleClearReaction(SocketSlashCommand command)
    {
        var eventKey = (string)command.Data.Options.First(o => o.Name == "event").Value;

        _prefs.ClearReaction(eventKey);

        await command.RespondAsync(ephemeral: true, embeds: [new EmbedBuilder()
            .WithColor(Color.Orange)
            .WithDescription($"Reaction override for **{EventLabel(eventKey)}** cleared — .env value will be used.")
            .WithCurrentTimestamp()
            .Build()]);
    }

    private async Task HandleListReactions(SocketSlashCommand command)
    {
        var keys = new[] { "opened", "reopened", "ready_for_review", "converted_to_draft", "merged", "closed", "approved", "changes_requested", "review_requested", "assigned", "comment" };
        var envKeys = new Dictionary<string, string>
        {
            ["opened"]             = "Reactions:Opened",
            ["reopened"]           = "Reactions:Reopened",
            ["ready_for_review"]   = "Reactions:ReadyForReview",
            ["converted_to_draft"] = "Reactions:ConvertedToDraft",
            ["merged"]             = "Reactions:Merged",
            ["closed"]             = "Reactions:Closed",
            ["approved"]           = "Reactions:Approved",
            ["changes_requested"]  = "Reactions:ChangesRequested",
            ["review_requested"]   = "Reactions:ReviewRequested",
            ["assigned"]           = "Reactions:Assigned",
            ["comment"]            = "Reactions:Comment",
        };

        var lines = keys.Select(key =>
        {
            var pref = _prefs.GetReaction(key);
            var env  = _config[envKeys[key]];
            var active = pref ?? env;
            var source = pref != null ? "prefs" : (!string.IsNullOrEmpty(env) ? ".env" : "unset");
            var display = string.IsNullOrEmpty(active) ? "*unset*" : active;
            return $"**{EventLabel(key)}** — {display} `[{source}]`";
        });

        await command.RespondAsync(ephemeral: true, embeds: [new EmbedBuilder()
            .WithTitle("Active Reactions")
            .WithColor(new Color(0x5865F2))
            .WithDescription(string.Join('\n', lines))
            .WithCurrentTimestamp()
            .Build()]);
    }

    private async Task HandleSetPingRole(SocketSlashCommand command)
    {
        var role = (SocketRole)command.Data.Options.First(o => o.Name == "role").Value;
        _prefs.SetPingRole(role.Id.ToString());
        await command.RespondAsync(ephemeral: true, embeds: [new EmbedBuilder()
            .WithColor(Color.Green)
            .WithDescription($"PR ping role set to {role.Mention}")
            .WithCurrentTimestamp()
            .Build()]);
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
            .AddField("PR Opened", $"{entry.PrOpened} pts ({entry.PrOpened / ScoreService.PointsPrOpened} PR{(entry.PrOpened / ScoreService.PointsPrOpened == 1 ? "" : "s")})", inline: true)
            .AddField("PR Merged", $"{entry.PrMerged} pts ({entry.PrMerged / ScoreService.PointsPrMerged} PR{(entry.PrMerged / ScoreService.PointsPrMerged == 1 ? "" : "s")})", inline: true)
            .AddField("Reviews", $"{entry.ReviewSubmitted} pts ({entry.ReviewSubmitted / ScoreService.PointsReview} review{(entry.ReviewSubmitted / ScoreService.PointsReview == 1 ? "" : "s")})", inline: true)
            .AddField("Comments", $"{entry.Comments} pts ({entry.Comments / ScoreService.PointsComment} comment{(entry.Comments / ScoreService.PointsComment == 1 ? "" : "s")})", inline: true)
            .WithCurrentTimestamp()
            .Build()]);
    }

    private async Task HandleLeaderboard(SocketSlashCommand command)
    {
        var verbose = command.Data.Options.FirstOrDefault(o => o.Name == "verbose")?.Value is true;
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
                            $"Reviews: {entry.Entry.ReviewSubmitted / ScoreService.PointsReview} · Comments: {entry.Entry.Comments / ScoreService.PointsComment}";
                embed.AddField("​", value);
            }

            await command.RespondAsync(ephemeral: true, embeds: [embed.Build()]);
        }
    }

    private async Task HandleClearPingRole(SocketSlashCommand command)
    {
        _prefs.ClearPingRole();
        await command.RespondAsync(ephemeral: true, embeds: [new EmbedBuilder()
            .WithColor(Color.Orange)
            .WithDescription("PR ping role override cleared — .env value will be used.")
            .WithCurrentTimestamp()
            .Build()]);
    }

    private static string EventLabel(string eventKey) => eventKey switch
    {
        "opened"             => "Opened",
        "reopened"           => "Reopened",
        "ready_for_review"   => "Ready for Review",
        "converted_to_draft" => "Converted to Draft",
        "merged"             => "Merged",
        "closed"             => "Closed",
        "approved"           => "Approved",
        "changes_requested"  => "Changes Requested",
        "review_requested"   => "Review Requested",
        "assigned"           => "Assigned",
        "comment"            => "Comment",
        _                    => eventKey,
    };

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
