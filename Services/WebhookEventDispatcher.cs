using GithubBot.Handlers;

namespace GithubBot.Services;

public class WebhookEventDispatcher
{
    private readonly Dictionary<string, IGitHubEventHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public WebhookEventDispatcher(IEnumerable<IGitHubEventHandler> handlers)
    {
        foreach (var handler in handlers)
        {
            if (!_handlers.TryAdd(handler.EventType, handler))
            {
                throw new InvalidOperationException($"Duplicate handler registered for event '{handler.EventType}'");
            }
        }
    }

    public async Task DispatchAsync(string eventType, string action, System.Text.Json.JsonElement payload, CancellationToken ct = default)
    {
        if (_handlers.TryGetValue(eventType, out var handler))
        {
            await handler.HandleAsync(action, payload, ct);
        }
    }
}
