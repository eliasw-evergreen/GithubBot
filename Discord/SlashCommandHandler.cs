using Discord;
using Discord.Rest;
using Discord.WebSocket;
using GithubBot.Services;

namespace GithubBot.Discord;

public class SlashCommandHandler
{
    private readonly DiscordSocketClient _client;
    private readonly UserMapService _userMap;
    private readonly IConfiguration _config;
    private readonly ILogger<SlashCommandHandler> _logger;

    public SlashCommandHandler(
        DiscordSocketClient client,
        UserMapService userMap,
        IConfiguration config,
        ILogger<SlashCommandHandler> logger)
    {
        _client = client;
        _userMap = userMap;
        _config = config;
        _logger = logger;
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

        var configRole = _config["Roles:Config"];
        if (!string.IsNullOrEmpty(configRole) && ulong.TryParse(configRole, out var roleId))
        {
            if (command.User is SocketGuildUser guildUser && !guildUser.Roles.Any(r => r.Id == roleId))
            {
                await command.RespondAsync("You do not have permission to use this command.", ephemeral: true);
                return;
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
}
