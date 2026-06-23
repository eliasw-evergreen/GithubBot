using System.Text.Json;

namespace GithubBot.Handlers;

public interface IGitHubEventHandler
{
    string EventType { get; }
    Task HandleAsync(string action, JsonElement payload, CancellationToken ct = default);
}
