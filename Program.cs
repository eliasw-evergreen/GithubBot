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

app.MapGet("/config", (HttpContext context, ConfigUiTokenService tokens) =>
{
    var token = context.Request.Query["token"].FirstOrDefault();
    var username = string.IsNullOrEmpty(token) ? null : tokens.ConsumeToken(token);
    if (username == null)
        return Results.Text("Invalid or expired link.", statusCode: 403);

    context.Session.SetString("auth", "1");
    context.Session.SetString("username", username);
    return Results.Redirect("/config/ui");
});

app.MapGet("/config/ui", async (HttpContext context, UserMapService userMap, PreferencesService prefs, ScoreService scores, Discord.WebSocket.DiscordSocketClient discordClient, IConfiguration config) =>
{
    if (context.Session.GetString("auth") != "1")
        return Results.Text("Unauthorized.", statusCode: 401);

    var guildId = ulong.TryParse(config["Discord:GuildId"], out var gid) ? gid : 0UL;
    var guild = discordClient.GetGuild(guildId);

    if (guild != null)
        try { await guild.DownloadUsersAsync(); } catch { /* intent may not be enabled yet */ }

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

    var textChannels = guild?.TextChannels
        .OrderBy(c => c.Position)
        .Select(c => new ChannelInfo(c.Id.ToString(), c.Name))
        .ToList() ?? [];

    var channelKeys = new[]
    {
        ("pull",   "PR Channel",    "Discord:ChannelId"),
        ("ticket", "Ticket Channel","Discord:TicketChannelId"),
    };
    var channelConfigs = channelKeys.Select(t =>
    {
        var pref = prefs.GetChannel(t.Item1);
        var env  = config[t.Item3];
        var value = pref ?? (string.IsNullOrEmpty(env) ? null : env);
        var source = pref != null ? "prefs" : (!string.IsNullOrEmpty(env) ? ".env" : "unset");
        return new ChannelConfigInfo(t.Item1, t.Item2, value, source);
    }).ToList();

    var currentPingRole = prefs.ResolvePingRole(config["Roles:PrPing"]);
    var pingRoleSource = prefs.GetPingRole() != null ? "prefs" : (!string.IsNullOrEmpty(config["Roles:PrPing"]) ? ".env" : "unset");

    var currentConfigRole = prefs.ResolveConfigRole(config["Roles:Config"]);
    var configRoleSource = prefs.GetConfigRole() != null ? "prefs" : (!string.IsNullOrEmpty(config["Roles:Config"]) ? ".env" : "unset");

    var currentCommandRole = prefs.ResolveCommandRole(config["Roles:Command"]);
    var commandRoleSource = prefs.GetCommandRole() != null ? "prefs" : (!string.IsNullOrEmpty(config["Roles:Command"]) ? ".env" : "unset");

    var map = userMap.GetAll();
    var allScores = scores.GetAll();
    var rouletteExclusions = prefs.GetRouletteExclusions();
    var pointValues = new Dictionary<string, int>();
    foreach (var key in new[] { "PrOpened","PrMerged","Review","Comment","TicketCreated","TicketBug","TicketStory","TicketComment" })
        if (prefs.GetPointValue(key) is int pv) pointValues[key] = pv;

    var html = ConfigUiHtml.Render(guildUsers, roles, map, reactions, textChannels, channelConfigs, currentPingRole, pingRoleSource, allScores, rouletteExclusions, currentConfigRole, configRoleSource, currentCommandRole, commandRoleSource, prefs.GetPrDescMaxLines(), pointValues);
    context.Response.Headers.CacheControl = "no-store";
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

    var map = userMap.GetAll();
    if (!map.TryGetValue(discordId, out var existing)) existing = new();
    var list = type == "devops" ? existing.Ado : existing.Gh;
    if (!list.Any(n => n.Equals(username, StringComparison.OrdinalIgnoreCase)))
    {
        list.Add(username);
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
    var array = form["array"].FirstOrDefault()?.Trim(); // "gh" or "ado"
    var value = form["value"].FirstOrDefault()?.Trim();

    if (string.IsNullOrEmpty(discordId) || string.IsNullOrEmpty(value))
        return Results.Redirect("/config/ui");

    var map = userMap.GetAll();
    if (map.TryGetValue(discordId, out var existing))
    {
        var list = array == "ado" ? existing.Ado : existing.Gh;
        list.RemoveAll(n => n.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (existing.Gh.Count == 0 && existing.Ado.Count == 0) map.Remove(discordId);
        userMap.Save(map);
    }
    return Results.Redirect("/config/ui");
});

app.MapGet("/config/ui/events", async (HttpContext context, ConfigUiTokenService tokens) =>
{
    if (context.Session.GetString("auth") != "1") { context.Response.StatusCode = 401; return; }
    var username = context.Session.GetString("username") ?? "Unknown";
    var sessionId = context.Session.Id;

    context.Response.Headers["Content-Type"] = "text/event-stream";
    context.Response.Headers["Cache-Control"] = "no-cache";
    context.Response.Headers["X-Accel-Buffering"] = "no"; // disable nginx buffering

    var (channel, currentUsers) = tokens.RegisterSession(sessionId, username);
    try
    {
        // Send users already in the session as initial events
        foreach (var u in currentUsers)
        {
            await context.Response.WriteAsync($"data: join:{u}\n\n");
            await context.Response.Body.FlushAsync();
        }

        await foreach (var msg in channel.Reader.ReadAllAsync(context.RequestAborted))
        {
            await context.Response.WriteAsync($"data: {msg}\n\n");
            await context.Response.Body.FlushAsync();
        }
    }
    catch (OperationCanceledException) { }
    finally
    {
        tokens.UnregisterSession(sessionId);
    }
});

app.MapPost("/config/ui/setchannel", async (HttpContext context, PreferencesService prefs) =>
{
    if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
    var form = await context.Request.ReadFormAsync();
    var key = form["key"].FirstOrDefault()?.Trim();
    var channelId = form["channel_id"].FirstOrDefault()?.Trim();
    if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(channelId))
        prefs.SetChannel(key, channelId);
    return Results.Redirect("/config/ui");
});

app.MapPost("/config/ui/clearchannel", async (HttpContext context, PreferencesService prefs) =>
{
    if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
    var form = await context.Request.ReadFormAsync();
    var key = form["key"].FirstOrDefault()?.Trim();
    if (!string.IsNullOrEmpty(key)) prefs.ClearChannel(key);
    return Results.Redirect("/config/ui");
});

app.MapPost("/config/ui/setpingrole", async (HttpContext context, PreferencesService prefs) =>
{
    if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
    var form = await context.Request.ReadFormAsync();
    var roleId = form["role_id"].FirstOrDefault()?.Trim();
    if (!string.IsNullOrEmpty(roleId)) prefs.SetPingRole(roleId);
    return Results.Redirect("/config/ui");
});

app.MapPost("/config/ui/clearpingrole", async (HttpContext context, PreferencesService prefs) =>
{
    if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
    prefs.ClearPingRole();
    return Results.Redirect("/config/ui");
});

app.MapPost("/config/ui/setconfigrole", async (HttpContext context, PreferencesService prefs) =>
{
    if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
    var form = await context.Request.ReadFormAsync();
    var roleId = form["role_id"].FirstOrDefault()?.Trim();
    if (!string.IsNullOrEmpty(roleId)) prefs.SetConfigRole(roleId);
    return Results.Redirect("/config/ui");
});

app.MapPost("/config/ui/clearconfigrole", async (HttpContext context, PreferencesService prefs) =>
{
    if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
    prefs.ClearConfigRole();
    return Results.Redirect("/config/ui");
});

app.MapPost("/config/ui/setcommandrole", async (HttpContext context, PreferencesService prefs) =>
{
    if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
    var form = await context.Request.ReadFormAsync();
    var roleId = form["role_id"].FirstOrDefault()?.Trim();
    if (!string.IsNullOrEmpty(roleId)) prefs.SetCommandRole(roleId);
    return Results.Redirect("/config/ui");
});

app.MapPost("/config/ui/clearcommandrole", async (HttpContext context, PreferencesService prefs) =>
{
    if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
    prefs.ClearCommandRole();
    return Results.Redirect("/config/ui");
});

app.MapPost("/config/ui/setprdescmaxlines", async (HttpContext context, PreferencesService prefs) =>
{
    if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
    var form = await context.Request.ReadFormAsync();
    var raw = form["value"].FirstOrDefault()?.Trim();
    if (int.TryParse(raw, out var v)) prefs.SetPrDescMaxLines(v);
    return Results.Redirect("/config/ui");
});

app.MapPost("/config/ui/clearprdescmaxlines", async (HttpContext context, PreferencesService prefs) =>
{
    if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
    prefs.SetPrDescMaxLines(null);
    return Results.Redirect("/config/ui");
});

app.MapPost("/config/ui/setpointvalue", async (HttpContext context, PreferencesService prefs) =>
{
    if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
    var form = await context.Request.ReadFormAsync();
    var key = form["key"].FirstOrDefault()?.Trim();
    var raw = form["value"].FirstOrDefault()?.Trim();
    if (!string.IsNullOrEmpty(key) && int.TryParse(raw, out var v)) prefs.SetPointValue(key, v);
    return Results.Redirect("/config/ui");
});

app.MapPost("/config/ui/clearpointvalue", async (HttpContext context, PreferencesService prefs) =>
{
    if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
    var form = await context.Request.ReadFormAsync();
    var key = form["key"].FirstOrDefault()?.Trim();
    if (!string.IsNullOrEmpty(key)) prefs.ClearPointValue(key);
    return Results.Redirect("/config/ui");
});

app.MapPost("/config/ui/setscore", async (HttpContext context, ScoreService scores) =>
{
    if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
    var form = await context.Request.ReadFormAsync();
    var discordId = form["discord_id"].FirstOrDefault()?.Trim();
    if (string.IsNullOrEmpty(discordId)) return Results.Redirect("/config/ui");
    int.TryParse(form["pr_opened"], out var prOpened);
    int.TryParse(form["pr_merged"], out var prMerged);
    int.TryParse(form["review"], out var review);
    int.TryParse(form["comments"], out var comments);
    int.TryParse(form["ticket_created"], out var ticketCreated);
    int.TryParse(form["ticket_resolved"], out var ticketResolved);
    int.TryParse(form["ticket_comments"], out var ticketComments);
    int.TryParse(form["bonus"], out var bonus);
    scores.SetScore(discordId, new ScoreEntry
    {
        PrOpened = prOpened,
        PrMerged = prMerged,
        ReviewSubmitted = review,
        Comments = comments,
        TicketCreated = ticketCreated,
        TicketResolved = ticketResolved,
        TicketComments = ticketComments,
        Bonus = bonus,
        Total = prOpened + prMerged + review + comments + ticketCreated + ticketResolved + ticketComments + bonus,
    });
    return Results.Redirect("/config/ui");
});

app.MapPost("/config/ui/resetscore", async (HttpContext context, ScoreService scores) =>
{
    if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
    var form = await context.Request.ReadFormAsync();
    var discordId = form["discord_id"].FirstOrDefault()?.Trim();
    if (!string.IsNullOrEmpty(discordId)) scores.ResetScore(discordId);
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

app.MapPost("/config/ui/setrouletteexclusion", async (HttpContext context, PreferencesService prefs) =>
{
    if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
    var form = await context.Request.ReadFormAsync();
    var discordId = form["discord_id"].FirstOrDefault()?.Trim();
    var excluded = form["excluded"].FirstOrDefault() == "1";
    if (!string.IsNullOrEmpty(discordId))
        prefs.SetRouletteExclusion(discordId, excluded);
    return Results.Redirect("/config/ui");
});

app.Run();
