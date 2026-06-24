using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNetEnv;
using GithubBot.Discord;
using GithubBot.Handlers;
using GithubBot.Services;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// Find repo root by walking up from content root looking for .env
var searchDir = builder.Environment.ContentRootPath;
string? repoRoot = null;
while (searchDir != null)
{
    if (File.Exists(Path.Combine(searchDir, ".env")))
    {
        repoRoot = searchDir;
        break;
    }
    var parent = Directory.GetParent(searchDir);
    searchDir = parent?.FullName;
}
repoRoot ??= builder.Environment.ContentRootPath;

// Load .env (shared with JS version) into process env vars
var envPath = Path.Combine(repoRoot, ".env");
if (File.Exists(envPath))
    Env.Load(envPath);

// Map .env flat keys to .NET config hierarchy and merge them in
var envMap = new Dictionary<string, string?>
{
    ["Discord:BotToken"] = Env.GetString("DISCORD_BOT_TOKEN"),
    ["Discord:ClientId"] = Env.GetString("DISCORD_CLIENT_ID"),
    ["Discord:GuildId"] = Env.GetString("DISCORD_GUILD_ID"),
    ["Discord:ChannelId"] = Env.GetString("DISCORD_CHANNEL_ID"),
    ["GitHub:WebhookSecret"] = Env.GetString("GITHUB_WEBHOOK_SECRET"),
    ["Reactions:Merged"] = Env.GetString("MERGED_REACTION"),
    ["Reactions:Comment"] = Env.GetString("COMMENT_REACTION"),
    ["Reactions:Closed"] = Env.GetString("CLOSED_REACTION"),
    ["Reactions:ChangesRequested"] = Env.GetString("CHANGES_REQUESTED_REACTION"),
    ["Reactions:ReviewRequested"] = Env.GetString("REVIEW_REQUESTED_REACTION"),
    ["Reactions:Assigned"] = Env.GetString("ASSIGNED_REACTION"),
    ["Reactions:Approved"] = Env.GetString("APPROVED_REACTION"),
    ["Reactions:Opened"] = Env.GetString("OPENED_REACTION"),
    ["Reactions:Reopened"] = Env.GetString("REOPENED_REACTION"),
    ["Reactions:ReadyForReview"] = Env.GetString("READY_FOR_REVIEW_REACTION"),
    ["Reactions:ConvertedToDraft"] = Env.GetString("CONVERTED_TO_DRAFT_REACTION"),
    ["Roles:PrPing"] = Env.GetString("PR_PING_ROLE"),
    ["Roles:Config"] = Env.GetString("CONFIG_ROLE"),
    ["PruneDays"] = Env.GetString("PRUNE_DAYS"),
    ["Port"] = Env.GetString("PORT"),
};

builder.Configuration
    .AddInMemoryCollection(envMap.Where(kv => kv.Value != null))
    .AddEnvironmentVariables();

var port = builder.Configuration.GetValue<int?>("Port") ?? 3000;
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Use repo root for data files (shared with JS version)
var dataPath = repoRoot;

// Register services
builder.Services.AddSingleton(new UserMapService(Path.Combine(dataPath, "usermap.json")));
builder.Services.AddSingleton(new PrMapService(Path.Combine(dataPath, "prmap.json")));
builder.Services.AddSingleton(new PreferencesService(Path.Combine(dataPath, "preferences.json")));
builder.Services.AddSingleton(new Discord.WebSocket.DiscordSocketClient(new Discord.WebSocket.DiscordSocketConfig
{
    GatewayIntents = Discord.GatewayIntents.Guilds | Discord.GatewayIntents.MessageContent,
}));
builder.Services.AddSingleton<DiscordBotService>();
builder.Services.AddSingleton<SlashCommandHandler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DiscordBotService>());

// Register handlers
builder.Services.AddSingleton<IGitHubEventHandler, PullRequestHandler>();
builder.Services.AddSingleton<IGitHubEventHandler, PullRequestReviewHandler>();
builder.Services.AddSingleton<IGitHubEventHandler, PullRequestReviewCommentHandler>();
builder.Services.AddSingleton<IGitHubEventHandler, IssueCommentHandler>();
builder.Services.AddSingleton<WebhookEventDispatcher>();

// Allow reading raw request body
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.BufferBody = true;
});

builder.Host.UseSerilog((ctx, cfg) => cfg
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting", LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(repoRoot, "logs", "log-.txt"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}"));

var app = builder.Build();
var appLogger = app.Services.GetRequiredService<ILogger<Program>>();

// ── Webhook endpoint ─────────────────────────────────────────────────────

app.MapPost("/ghwebhook", async (HttpContext context, WebhookEventDispatcher dispatcher) =>
{
    var eventType = context.Request.Headers["x-github-event"].FirstOrDefault() ?? "";
    var signature = context.Request.Headers["x-hub-signature-256"].FirstOrDefault();

    // Read raw body
    context.Request.EnableBuffering();
    using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
    var body = await reader.ReadToEndAsync();
    context.Request.Body.Position = 0;

    // Verify signature
    var secret = app.Configuration["GitHub:WebhookSecret"];
    if (!string.IsNullOrEmpty(secret))
    {
        if (string.IsNullOrEmpty(signature))
            return Results.Unauthorized();

        var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var computed = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var expected = $"sha256={computed}";

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature),
                Encoding.UTF8.GetBytes(expected)))
            return Results.Unauthorized();
    }

    // Parse action from payload
    string action;
    try
    {
        using var doc = JsonDocument.Parse(body);
        action = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
        var payload = doc.RootElement;

        // Prune PR map before handling
        var prMap = context.RequestServices.GetRequiredService<PrMapService>();
        var pruneDays = app.Configuration.GetValue<int>("PruneDays");
        prMap.Prune(pruneDays);

        appLogger.LogInformation("Webhook received event={EventType} action={Action}", eventType, action);

        await dispatcher.DispatchAsync(eventType, action, payload);
    }
    catch (Exception ex)
    {
        appLogger.LogError(ex, "Webhook error event={EventType} action={Action}", eventType, action);
    }

    return Results.Ok("ok");
});

app.Run();
