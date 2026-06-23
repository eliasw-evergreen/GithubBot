using Discord;
using Discord.WebSocket;
using GithubBot.Services;

namespace GithubBot.Discord;

public class DiscordBotService : IHostedService
{
    private readonly DiscordSocketClient _client;
    private readonly IConfiguration _config;
    private readonly UserMapService _userMap;
    private readonly SlashCommandHandler _slashHandler;
    private readonly ILogger<DiscordBotService> _logger;

    private ulong _channelId;

    public DiscordBotService(
        DiscordSocketClient client,
        IConfiguration config,
        UserMapService userMap,
        SlashCommandHandler slashHandler,
        ILogger<DiscordBotService> logger)
    {
        _client = client;
        _config = config;
        _userMap = userMap;
        _slashHandler = slashHandler;
        _logger = logger;

        _client.Log += msg =>
        {
            _logger.LogInformation("[Discord] {Message}", msg.ToString());
            return Task.CompletedTask;
        };

        _client.Ready += OnReady;
        _client.InteractionCreated += _slashHandler.HandleInteractionAsync;
    }

    public DiscordSocketClient Client => _client;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var token = _config["Discord:BotToken"];
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Discord bot token is not configured");

        _channelId = ulong.TryParse(_config["Discord:ChannelId"], out var id) ? id : 0;

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _client.StopAsync();
    }

    private async Task OnReady()
    {
        _logger.LogInformation("Logged in as {User}", _client.CurrentUser?.Username ?? "unknown");
        await _slashHandler.RegisterAsync();
    }

    public Task<IMessageChannel?> GetChannelAsync(CancellationToken ct = default)
    {
        if (_channelId == 0) return Task.FromResult<IMessageChannel?>(null);
        return Task.FromResult(_client.GetChannel(_channelId) as IMessageChannel);
    }

    public Task<IMessageChannel?> GetTargetChannel(IMessageChannel channel, PrMapEntry? stored, CancellationToken ct = default)
    {
        if (stored?.ThreadId == null || stored.ThreadId == 0) return Task.FromResult<IMessageChannel?>(channel);

        var thread = _client.GetChannel(stored.ThreadId.Value) as IMessageChannel;
        return Task.FromResult<IMessageChannel?>(thread ?? channel);
    }

    public async Task<IUserMessage?> SendMessageAsync(ulong channelId, string? content, Embed? embed, CancellationToken ct = default)
    {
        var channel = _client.GetChannel(channelId) as IMessageChannel;
        if (channel == null) return null;

        return await channel.SendMessageAsync(
            string.IsNullOrEmpty(content) ? null : content,
            embed: embed);
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
        await msg.AddReactionAsync(emote);
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
