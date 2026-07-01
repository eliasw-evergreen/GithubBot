using GithubBot.Discord;

namespace GithubBot.Services;

public class DeferredPingWorker(
    WorkHoursService workHours,
    DeferredPingService deferred,
    DiscordBotService discord,
    ILogger<DeferredPingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var delay = workHours.TimeUntilNextWorkStart();
            if (delay > TimeSpan.Zero)
            {
                logger.LogInformation("[DeferredPingWorker] Sleeping {Minutes:F1} min until next work start", delay.TotalMinutes);
                await Task.Delay(delay, ct);
            }

            await FlushAsync(ct);

            // Small buffer so we don't immediately re-fire at the same work-start second
            await Task.Delay(TimeSpan.FromMinutes(2), ct);
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        var pending = deferred.GetPending();
        if (pending.Count == 0) return;

        logger.LogInformation("[DeferredPingWorker] Flushing {Count} deferred ping entries", pending.Count);

        foreach (var entry in pending)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var pingsStr = string.Join(' ', entry.Pings);
                var labels = entry.Labels.Count > 0
                    ? "\n" + string.Join("\n", entry.Labels.Select(l => $"• {l}"))
                    : "";
                var content = $"{pingsStr} — pending notifications:{labels}";
                await discord.SendMessageAsync(entry.ChannelId, null, null, ct, immediateContent: content);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[DeferredPingWorker] Failed to flush pings for channel {ChannelId}", entry.ChannelId);
            }
        }

        deferred.Clear();
    }
}
