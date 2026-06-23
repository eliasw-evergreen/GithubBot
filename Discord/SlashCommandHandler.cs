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
    private readonly IConfiguration _config;
    private readonly ILogger<SlashCommandHandler> _logger;
    private readonly bool _noAuth;
    private readonly ulong _channelId;

    public SlashCommandHandler(
        DiscordSocketClient client,
        UserMapService userMap,
        PrMapService prMap,
        PreferencesService prefs,
        IConfiguration config,
        ILogger<SlashCommandHandler> logger)
    {
        _client = client;
        _userMap = userMap;
        _prMap = prMap;
        _prefs = prefs;
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
                    .WithDescription("Add a GitHub username mapping for a Discord user (multiple allowed)")
                    .AddOption("discord_user", ApplicationCommandOptionType.User, "The Discord user", isRequired: true)
                    .AddOption("github_username", ApplicationCommandOptionType.String, "Their GitHub username", isRequired: true)
                    .Build(),
                guildId);

            await rest.CreateGuildCommand(
                new SlashCommandBuilder()
                    .WithName("unmapuser")
                    .WithDescription("Remove a GitHub username from a Discord user")
                    .AddOption("discord_user", ApplicationCommandOptionType.User, "The Discord user", isRequired: true)
                    .AddOption("github_username", ApplicationCommandOptionType.String,
                        "Specific GitHub username to remove (leave blank to remove all)", isRequired: false)
                    .Build(),
                guildId);

            await rest.CreateGuildCommand(
                new SlashCommandBuilder()
                    .WithName("listmappings")
                    .WithDescription("Show all Discord <-> GitHub user mappings")
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
                        .AddChoice("Changes Requested", "changes_requested")
                        .AddChoice("Comment", "comment")
                        .AddChoice("Merged", "merged")
                        .AddChoice("Closed", "closed"))
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
                        .AddChoice("Changes Requested", "changes_requested")
                        .AddChoice("Comment", "comment")
                        .AddChoice("Merged", "merged")
                        .AddChoice("Closed", "closed"))
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
        }
    }

    private async Task HandleMapUser(SocketSlashCommand command)
    {
        var discordUser = (SocketUser)command.Data.Options.First(o => o.Name == "discord_user").Value;
        var githubUsername = ((string)command.Data.Options.First(o => o.Name == "github_username").Value).Trim();
        var map = _userMap.GetAll();

        if (!map.TryGetValue(discordUser.Id.ToString(), out var existing))
            existing = [];

        if (existing.Any(n => n.Equals(githubUsername, StringComparison.OrdinalIgnoreCase)))
        {
            await command.RespondAsync($"**{githubUsername}** is already mapped to <@{discordUser.Id}>.", ephemeral: true);
            return;
        }

        existing.Add(githubUsername);
        map[discordUser.Id.ToString()] = existing;
        _userMap.Save(map);

        var all = string.Join(", ", existing.Select(n => $"**[{n}](https://github.com/{n})**"));
        await command.RespondAsync(embeds: [new EmbedBuilder()
            .WithColor(Color.Green)
            .WithDescription($"<@{discordUser.Id}> is now mapped to: {all}")
            .WithCurrentTimestamp()
            .Build()]);

        _ = Task.Run(() => BackfillMappingAsync(githubUsername, discordUser.Id.ToString()));
    }

    private async Task HandleUnmapUser(SocketSlashCommand command)
    {
        var discordUser = (SocketUser)command.Data.Options.First(o => o.Name == "discord_user").Value;
        var githubUsername = command.Data.Options.FirstOrDefault(o => o.Name == "github_username")?.Value as string;
        var map = _userMap.GetAll();

        if (!map.TryGetValue(discordUser.Id.ToString(), out var existing) || existing.Count == 0)
        {
            await command.RespondAsync("No mapping found for that user.", ephemeral: true);
            return;
        }

        if (!string.IsNullOrEmpty(githubUsername))
        {
            var updated = existing.Where(n => !n.Equals(githubUsername, StringComparison.OrdinalIgnoreCase)).ToList();
            if (updated.Count == existing.Count)
            {
                await command.RespondAsync($"**{githubUsername}** was not mapped to <@{discordUser.Id}>.", ephemeral: true);
                return;
            }
            if (updated.Count == 0)
                map.Remove(discordUser.Id.ToString());
            else
                map[discordUser.Id.ToString()] = updated;
            _userMap.Save(map);
            await command.RespondAsync(embeds: [new EmbedBuilder()
                .WithColor(Color.Orange)
                .WithDescription($"Removed **{githubUsername}** from <@{discordUser.Id}>'s mappings")
                .WithCurrentTimestamp()
                .Build()]);
        }
        else
        {
            map.Remove(discordUser.Id.ToString());
            _userMap.Save(map);
            await command.RespondAsync(embeds: [new EmbedBuilder()
                .WithColor(Color.Orange)
                .WithDescription($"Removed all GitHub mappings for <@{discordUser.Id}>")
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
            var links = string.Join(", ", kvp.Value.Select(n => $"**[{n}](https://github.com/{n})**"));
            return $"<@{kvp.Key}> → {links}";
        });

        await command.RespondAsync(embeds: [new EmbedBuilder()
            .WithTitle("Discord ↔ GitHub Mappings")
            .WithColor(new Color(0x5865F2))
            .WithDescription(string.Join('\n', lines))
            .WithCurrentTimestamp()
            .Build()]);
    }

    private async Task HandleSetReaction(SocketSlashCommand command)
    {
        var eventKey = (string)command.Data.Options.First(o => o.Name == "event").Value;
        var emoji = ((string)command.Data.Options.First(o => o.Name == "emoji").Value).Trim();

        _prefs.SetReaction(eventKey, emoji);

        await command.RespondAsync(embeds: [new EmbedBuilder()
            .WithColor(Color.Green)
            .WithDescription($"Reaction for **{EventLabel(eventKey)}** is now set to {emoji}")
            .WithCurrentTimestamp()
            .Build()], ephemeral: true);
    }

    private async Task HandleClearReaction(SocketSlashCommand command)
    {
        var eventKey = (string)command.Data.Options.First(o => o.Name == "event").Value;

        _prefs.ClearReaction(eventKey);

        await command.RespondAsync(embeds: [new EmbedBuilder()
            .WithColor(Color.Orange)
            .WithDescription($"Reaction override for **{EventLabel(eventKey)}** cleared — .env value will be used.")
            .WithCurrentTimestamp()
            .Build()], ephemeral: true);
    }

    private static string EventLabel(string eventKey) => eventKey switch
    {
        "changes_requested" => "Changes Requested",
        "comment"           => "Comment",
        "merged"            => "Merged",
        "closed"            => "Closed",
        _                   => eventKey,
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
