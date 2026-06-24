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
    ["PruneDays"] = Env.GetString("PRUNE_DAYS"),
    ["Port"] = Env.GetString("PORT"),
    ["PublicHost"] = Env.GetString("PUBLIC_HOST"),
    ["Discord:TicketChannelId"] = Env.GetString("DISCORD_TICKET_CHANNEL_ID"),
    ["AzureDevOps:WebhookSecret"] = Env.GetString("ADO_WEBHOOK_SECRET"),
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
builder.Services.AddSingleton(new PreferencesService(Path.Combine(dataPath, "preferences.json")));
builder.Services.AddSingleton(new ScoreService(Path.Combine(dataPath, "scores.json")));
builder.Services.AddSingleton<ConfigUiTokenService>();
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

    // TODO: parse eventType from payload (e.g. workitem.updated, build.complete)
    // TODO: dispatch to ADO event handlers
    appLogger.LogInformation("ADO webhook received (stub)");
    return Results.Ok("ok");
});

// ── Config UI ─────────────────────────────────────────────────────────────

app.MapGet("/config", (HttpContext context, ConfigUiTokenService tokens) =>
{
    var token = context.Request.Query["token"].FirstOrDefault();
    if (string.IsNullOrEmpty(token) || !tokens.ConsumeToken(token))
        return Results.Text("Invalid or expired link.", statusCode: 403);

    context.Session.SetString("auth", "1");
    return Results.Redirect("/config/ui");
});

app.MapGet("/config/ui", (HttpContext context, UserMapService userMap, PreferencesService prefs, Discord.WebSocket.DiscordSocketClient discordClient, IConfiguration config) =>
{
    if (context.Session.GetString("auth") != "1")
        return Results.Text("Unauthorized.", statusCode: 401);

    var guildId = ulong.TryParse(config["Discord:GuildId"], out var gid) ? gid : 0UL;
    var guild = discordClient.GetGuild(guildId);

    var guildUsers = guild?.Users
        .OrderBy(u => u.DisplayName)
        .Select(u => new GuildUserInfo(
            u.Id.ToString(),
            u.DisplayName,
            u.Roles.Select(r => r.Id.ToString()).ToList()))
        .ToList() ?? [];

    var roles = guild?.Roles
        .Where(r => !r.IsEveryone)
        .Select(r => new GuildRoleInfo(r.Id.ToString(), r.Name))
        .ToList() ?? [];

    var reactionKeys = new[]
    {
        ("opened",             "Opened",             "Reactions:Opened"),
        ("reopened",           "Reopened",           "Reactions:Reopened"),
        ("ready_for_review",   "Ready for Review",   "Reactions:ReadyForReview"),
        ("converted_to_draft", "Converted to Draft", "Reactions:ConvertedToDraft"),
        ("merged",             "Merged",             "Reactions:Merged"),
        ("closed",             "Closed",             "Reactions:Closed"),
        ("approved",           "Approved",           "Reactions:Approved"),
        ("changes_requested",  "Changes Requested",  "Reactions:ChangesRequested"),
        ("review_requested",   "Review Requested",   "Reactions:ReviewRequested"),
        ("assigned",           "Assigned",           "Reactions:Assigned"),
        ("comment",            "Comment",            "Reactions:Comment"),
    };

    var reactions = reactionKeys.Select(t =>
    {
        var pref = prefs.GetReaction(t.Item1);
        var env  = config[t.Item3];
        var value = pref ?? (string.IsNullOrEmpty(env) ? null : env);
        var source = pref != null ? "prefs" : (!string.IsNullOrEmpty(env) ? ".env" : "unset");
        return new ReactionInfo(t.Item1, t.Item2, value, source);
    }).ToList();

    var map = userMap.GetAll();
    var html = ConfigUiHtml.Render(guildUsers, roles, map, reactions);
    return Results.Content(html, "text/html");
});

app.MapPost("/config/ui/add", async (HttpContext context, UserMapService userMap) =>
{
    if (context.Session.GetString("auth") != "1")
        return Results.Text("Unauthorized.", statusCode: 401);

    var form = await context.Request.ReadFormAsync();
    var discordId = form["discord_id"].FirstOrDefault()?.Trim();
    var username = form["username"].FirstOrDefault()?.Trim();
    var type = form["type"].FirstOrDefault() ?? "github";

    if (string.IsNullOrEmpty(discordId) || string.IsNullOrEmpty(username))
        return Results.Redirect("/config/ui");

    var storedKey = type == "devops" ? UserMapService.Encode(username) : username;
    var map = userMap.GetAll();
    if (!map.TryGetValue(discordId, out var existing)) existing = [];
    if (!existing.Any(n => n.Equals(storedKey, StringComparison.OrdinalIgnoreCase)))
    {
        existing.Add(storedKey);
        map[discordId] = existing;
        userMap.Save(map);
    }
    return Results.Redirect("/config/ui");
});

app.MapPost("/config/ui/remove", async (HttpContext context, UserMapService userMap) =>
{
    if (context.Session.GetString("auth") != "1")
        return Results.Text("Unauthorized.", statusCode: 401);

    var form = await context.Request.ReadFormAsync();
    var discordId = form["discord_id"].FirstOrDefault()?.Trim();
    var storedKey = form["stored_key"].FirstOrDefault()?.Trim();

    if (string.IsNullOrEmpty(discordId) || string.IsNullOrEmpty(storedKey))
        return Results.Redirect("/config/ui");

    var map = userMap.GetAll();
    if (map.TryGetValue(discordId, out var existing))
    {
        existing.RemoveAll(n => n.Equals(storedKey, StringComparison.OrdinalIgnoreCase));
        if (existing.Count == 0) map.Remove(discordId);
        else map[discordId] = existing;
        userMap.Save(map);
    }
    return Results.Redirect("/config/ui");
});

app.MapPost("/config/ui/setreaction", async (HttpContext context, PreferencesService prefs) =>
{
    if (context.Session.GetString("auth") != "1")
        return Results.Text("Unauthorized.", statusCode: 401);

    var form = await context.Request.ReadFormAsync();
    var eventKey = form["event"].FirstOrDefault()?.Trim();
    var emoji    = form["emoji"].FirstOrDefault()?.Trim();

    if (!string.IsNullOrEmpty(eventKey) && !string.IsNullOrEmpty(emoji))
        prefs.SetReaction(eventKey, emoji);

    return Results.Redirect("/config/ui");
});

app.MapPost("/config/ui/clearreaction", async (HttpContext context, PreferencesService prefs) =>
{
    if (context.Session.GetString("auth") != "1")
        return Results.Text("Unauthorized.", statusCode: 401);

    var form = await context.Request.ReadFormAsync();
    var eventKey = form["event"].FirstOrDefault()?.Trim();

    if (!string.IsNullOrEmpty(eventKey))
        prefs.ClearReaction(eventKey);

    return Results.Redirect("/config/ui");
});

app.Run();
