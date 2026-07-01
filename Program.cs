using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNetEnv;
using GithubBot.Discord;
using GithubBot.Handlers;
using GithubBot.Services;
using GithubBot.Web;
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
    ["Discord:ChannelId"] = Env.GetString("DISCORD_PULL_CHANNEL_ID"),
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
    ["Roles:Command"] = Env.GetString("COMMAND_ROLE"),
    ["PruneDays"] = Env.GetString("PRUNE_DAYS"),
    ["Port"] = Env.GetString("PORT"),
    ["PublicHost"] = Env.GetString("PUBLIC_HOST"),
    ["Discord:TicketChannelId"] = Env.GetString("DISCORD_TICKET_CHANNEL_ID"),
    ["AzureDevOps:WebhookSecret"] = Env.GetString("ADO_WEBHOOK_SECRET"),
    ["AzureDevOps:OrgUrl"]        = Env.GetString("ADO_ORG_URL"),
    ["AzureDevOps:Project"]       = Env.GetString("ADO_PROJECT"),
    ["AzureDevOps:Pat"]           = Env.GetString("ADO_PAT"),
    ["GitHub:Pat"]                = Env.GetString("GITHUB_PAT"),
    ["GitHub:Repos"]              = Env.GetString("GITHUB_REPOS"),
    ["OpenRouter:ApiKey"]         = Env.GetString("OPENROUTER_API_KEY"),
    ["OpenRouter:Model"]          = Env.GetString("OPENROUTER_MODEL"),
    ["WorkHours:Start"]           = Env.GetString("WORK_HOURS_START"),
    ["WorkHours:End"]             = Env.GetString("WORK_HOURS_END"),
    ["WorkHours:Timezone"]        = Env.GetString("WORK_HOURS_TIMEZONE"),
    ["WorkHours:Days"]            = Env.GetString("WORK_HOURS_DAYS"),
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
builder.Services.AddSingleton(new CommentMapService(Path.Combine(dataPath, "commentmap.json")));
builder.Services.AddSingleton(new ReviewMapService(Path.Combine(dataPath, "reviewmap.json")));
builder.Services.AddSingleton(new PreferencesService(Path.Combine(dataPath, "preferences.json")));
builder.Services.AddSingleton(sp => new ScoreService(Path.Combine(dataPath, "scores.json"), sp.GetRequiredService<PreferencesService>()));
builder.Services.AddSingleton(new RouletteService(Path.Combine(dataPath, "roulette.json")));
builder.Services.AddSingleton(new WorkItemMapService(Path.Combine(dataPath, "workitemmap.json")));
builder.Services.AddSingleton<ConfigUiTokenService>();
builder.Services.AddSingleton(new DeferredPingService(Path.Combine(dataPath, "deferredpings.json")));
builder.Services.AddSingleton<WorkHoursService>();
builder.Services.AddHostedService<DeferredPingWorker>();
builder.Services.AddSingleton<GithubBot.Handlers.AdoWorkItemHandler>();

var adoOrgUrl = builder.Configuration["AzureDevOps:OrgUrl"];
var adoProject = builder.Configuration["AzureDevOps:Project"];
var adoPat     = builder.Configuration["AzureDevOps:Pat"];
if (!string.IsNullOrEmpty(adoOrgUrl) && !string.IsNullOrEmpty(adoProject) && !string.IsNullOrEmpty(adoPat))
{
    var adoLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<AdoApiService>();
    builder.Services.AddSingleton(new AdoApiService(adoOrgUrl, adoProject, adoPat, adoLogger));
}
var ghPat = builder.Configuration["GitHub:Pat"];
if (!string.IsNullOrEmpty(ghPat))
{
    var ghLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<GitHubApiService>();
    builder.Services.AddSingleton(new GitHubApiService(ghPat, ghLogger));
}
var orApiKey = builder.Configuration["OpenRouter:ApiKey"];
var orModel  = builder.Configuration["OpenRouter:Model"];
if (!string.IsNullOrEmpty(orApiKey) && !string.IsNullOrEmpty(orModel))
{
    var orLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<PrSummaryService>();
    builder.Services.AddSingleton(new PrSummaryService(orApiKey, orModel, orLogger));
}

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(o =>
{
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.IsEssential = true;
    // No MaxAge = true browser-session cookie (dies when browser closes)
});
builder.Services.AddSingleton(new Discord.WebSocket.DiscordSocketClient(new Discord.WebSocket.DiscordSocketConfig
{
    GatewayIntents = Discord.GatewayIntents.Guilds | Discord.GatewayIntents.MessageContent | Discord.GatewayIntents.GuildMembers,
    // Respect Retry-After / X-RateLimit-Reset-After headers on all requests
    DefaultRetryMode = Discord.RetryMode.RetryRatelimit,
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
app.UseSession();
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
        {
            appLogger.LogWarning("Webhook rejected: missing x-hub-signature-256 header");
            return Results.Unauthorized();
        }

        var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var computed = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var expected = $"sha256={computed}";

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature),
                Encoding.UTF8.GetBytes(expected)))
        {
            appLogger.LogWarning("Webhook rejected: invalid signature");
            return Results.Unauthorized();
        }
    }

    // Parse action from payload
    var action = "";
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

app.MapPost("/adowebhook", async (HttpContext context) =>
{
    var adoSecret = app.Configuration["AzureDevOps:WebhookSecret"];
    if (!string.IsNullOrEmpty(adoSecret))
    {
        // ADO sends credentials as HTTP Basic Auth — password is the configured secret
        var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return Results.Unauthorized();

        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authHeader["Basic ".Length..].Trim()));
        var password = decoded.Contains(':') ? decoded[(decoded.IndexOf(':') + 1)..] : decoded;

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(password),
                Encoding.UTF8.GetBytes(adoSecret)))
            return Results.Unauthorized();
    }

    context.Request.EnableBuffering();
    using var adoReader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
    var adoBody = await adoReader.ReadToEndAsync();
    context.Request.Body.Position = 0;

    string adoEventType;
    JsonElement adoPayload;
    try
    {
        using var adoDoc = JsonDocument.Parse(adoBody);
        adoEventType = adoDoc.RootElement.TryGetProperty("eventType", out var et) ? et.GetString() ?? "" : "";
        adoPayload = adoDoc.RootElement.Clone();
    }
    catch (Exception ex)
    {
        appLogger.LogError(ex, "ADO webhook: failed to parse payload");
        return Results.BadRequest("Invalid JSON");
    }

    appLogger.LogInformation("ADO webhook received eventType={EventType}", adoEventType);

    try
    {
        var adoHandler = app.Services.GetRequiredService<GithubBot.Handlers.AdoWorkItemHandler>();
        switch (adoEventType)
        {
            case "workitem.created":
                await adoHandler.HandleWorkItemCreatedAsync(adoPayload); break;
            case "workitem.updated":
                await adoHandler.HandleWorkItemUpdatedAsync(adoPayload); break;
            case "workitem.commented":
                await adoHandler.HandleWorkItemCommentedAsync(adoPayload); break;
            case "workitem.deleted":
                await adoHandler.HandleWorkItemDeletedAsync(adoPayload); break;
        }
    }
    catch (Exception ex)
    {
        appLogger.LogError(ex, "ADO webhook error eventType={EventType}", adoEventType);
    }

    return Results.Ok("ok");
});

// ── Config UI ─────────────────────────────────────────────────────────────

app.MapConfigUiEndpoints();


app.Run();
