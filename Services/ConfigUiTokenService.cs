using System.Collections.Concurrent;
using System.Threading.Channels;

namespace GithubBot.Services;

public class ConfigUiTokenService
{
    private readonly ConcurrentDictionary<string, string> _pendingTokens = new();
    private readonly ConcurrentDictionary<string, (string Username, Channel<string> Ch)> _activeSessions = new();

    // Returns the token. discordUsername is shown to other editors.
    public string GenerateToken(string discordUsername)
    {
        var token = Guid.NewGuid().ToString("N");
        _pendingTokens[token] = discordUsername;
        return token;
    }

    // Returns the discord username if valid, null if invalid/already used.
    public string? ConsumeToken(string token)
        => _pendingTokens.TryRemove(token, out var username) ? username : null;

    // Registers a new SSE session. Returns the channel to read from and the
    // list of usernames already active (so we can send them as initial events).
    public (Channel<string> Channel, IReadOnlyList<string> CurrentUsers) RegisterSession(string sessionId, string username)
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var currentUsers = _activeSessions.Values.Select(v => v.Username).ToList();
        _activeSessions[sessionId] = (username, channel);
        Broadcast(sessionId, $"join:{username}");
        Broadcast(sessionId, "reload");
        return (channel, currentUsers);
    }

    public void UnregisterSession(string sessionId)
    {
        if (_activeSessions.TryRemove(sessionId, out var info))
        {
            Broadcast(sessionId, $"leave:{info.Username}");
            info.Ch.Writer.TryComplete();
        }
    }

    private void Broadcast(string senderSessionId, string message)
    {
        foreach (var (id, (_, ch)) in _activeSessions)
            if (id != senderSessionId)
                ch.Writer.TryWrite(message);
    }
}
