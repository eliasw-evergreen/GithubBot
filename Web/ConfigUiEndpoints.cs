using Discord.WebSocket;
using GithubBot.Services;

namespace GithubBot.Web;

public static class ConfigUiEndpoints
{
    public static void MapConfigUiEndpoints(this WebApplication app)
    {
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

        app.MapGet("/config/ui", async (HttpContext context, UserMapService userMap, PreferencesService prefs, ScoreService scores, DiscordSocketClient discordClient, IConfiguration config) =>
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
                ("pull",   "PR Channel",     "Discord:ChannelId"),
                ("ticket", "Ticket Channel", "Discord:TicketChannelId"),
            };
            var channelConfigs = channelKeys.Select(t =>
            {
                var pref = prefs.GetChannel(t.Item1);
                var env  = config[t.Item3];
                var value = pref ?? (string.IsNullOrEmpty(env) ? null : env);
                var source = pref != null ? "prefs" : (!string.IsNullOrEmpty(env) ? ".env" : "unset");
                return new ChannelConfigInfo(t.Item1, t.Item2, value, source);
            }).ToList();

            var currentPingRole    = prefs.ResolvePingRole(config["Roles:PrPing"]);
            var pingRoleSource     = prefs.GetPingRole()    != null ? "prefs" : (!string.IsNullOrEmpty(config["Roles:PrPing"])   ? ".env" : "unset");
            var currentConfigRole  = prefs.ResolveConfigRole(config["Roles:Config"]);
            var configRoleSource   = prefs.GetConfigRole()  != null ? "prefs" : (!string.IsNullOrEmpty(config["Roles:Config"])  ? ".env" : "unset");
            var currentCommandRole = prefs.ResolveCommandRole(config["Roles:Command"]);
            var commandRoleSource  = prefs.GetCommandRole() != null ? "prefs" : (!string.IsNullOrEmpty(config["Roles:Command"]) ? ".env" : "unset");

            var map = userMap.GetAll();
            var allScores = scores.GetAll();
            var rouletteExclusions = prefs.GetRouletteExclusions();
            var pointValues = new Dictionary<string, int>();
            foreach (var key in new[] { "PrOpened","PrMerged","Review","Comment","TicketCreated","TicketBug","TicketStory","TicketComment" })
                if (prefs.GetPointValue(key) is int pv) pointValues[key] = pv;

            var workHours = prefs.ResolveWorkHours(config);
            var workHoursSource = (prefs.GetWorkHoursStart() ?? prefs.GetWorkHoursEnd() ?? prefs.GetWorkHoursTimezone() ?? prefs.GetWorkHoursDays()) != null
                ? "prefs"
                : (config["WorkHours:Start"] != null || config["WorkHours:End"] != null ? ".env" : "unset");

            var html = ConfigUiHtml.Render(guildUsers, roles, map, reactions, textChannels, channelConfigs, currentPingRole, pingRoleSource, allScores, rouletteExclusions, currentConfigRole, configRoleSource, currentCommandRole, commandRoleSource, prefs.GetPrDescMaxLines(), pointValues, workHours, workHoursSource);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Content(html, "text/html");
        });

        app.MapPost("/config/ui/add", async (HttpContext context, UserMapService userMap) =>
        {
            if (context.Session.GetString("auth") != "1")
                return Results.Text("Unauthorized.", statusCode: 401);

            var form = await context.Request.ReadFormAsync();
            var discordId = form["discord_id"].FirstOrDefault()?.Trim();
            var username  = form["username"].FirstOrDefault()?.Trim();
            var type      = form["type"].FirstOrDefault() ?? "github";

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

            var form      = await context.Request.ReadFormAsync();
            var discordId = form["discord_id"].FirstOrDefault()?.Trim();
            var array     = form["array"].FirstOrDefault()?.Trim(); // "gh" or "ado"
            var value     = form["value"].FirstOrDefault()?.Trim();

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
            var username  = context.Session.GetString("username") ?? "Unknown";
            var sessionId = context.Session.Id;

            context.Response.Headers["Content-Type"]      = "text/event-stream";
            context.Response.Headers["Cache-Control"]     = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            var (channel, currentUsers) = tokens.RegisterSession(sessionId, username);
            try
            {
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
            var form      = await context.Request.ReadFormAsync();
            var key       = form["key"].FirstOrDefault()?.Trim();
            var channelId = form["channel_id"].FirstOrDefault()?.Trim();
            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(channelId))
                prefs.SetChannel(key, channelId);
            return Results.Redirect("/config/ui");
        });

        app.MapPost("/config/ui/clearchannel", async (HttpContext context, PreferencesService prefs) =>
        {
            if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
            var form = await context.Request.ReadFormAsync();
            var key  = form["key"].FirstOrDefault()?.Trim();
            if (!string.IsNullOrEmpty(key)) prefs.ClearChannel(key);
            return Results.Redirect("/config/ui");
        });

        app.MapPost("/config/ui/setpingrole", async (HttpContext context, PreferencesService prefs) =>
        {
            if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
            var form   = await context.Request.ReadFormAsync();
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
            var form   = await context.Request.ReadFormAsync();
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
            var form   = await context.Request.ReadFormAsync();
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
            var raw  = form["value"].FirstOrDefault()?.Trim();
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
            var key  = form["key"].FirstOrDefault()?.Trim();
            var raw  = form["value"].FirstOrDefault()?.Trim();
            if (!string.IsNullOrEmpty(key) && int.TryParse(raw, out var v)) prefs.SetPointValue(key, v);
            return Results.Redirect("/config/ui");
        });

        app.MapPost("/config/ui/clearpointvalue", async (HttpContext context, PreferencesService prefs) =>
        {
            if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
            var form = await context.Request.ReadFormAsync();
            var key  = form["key"].FirstOrDefault()?.Trim();
            if (!string.IsNullOrEmpty(key)) prefs.ClearPointValue(key);
            return Results.Redirect("/config/ui");
        });

        app.MapPost("/config/ui/setscore", async (HttpContext context, ScoreService scores) =>
        {
            if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
            var form      = await context.Request.ReadFormAsync();
            var discordId = form["discord_id"].FirstOrDefault()?.Trim();
            if (string.IsNullOrEmpty(discordId)) return Results.Redirect("/config/ui");
            int.TryParse(form["pr_opened"],       out var prOpened);
            int.TryParse(form["pr_merged"],        out var prMerged);
            int.TryParse(form["review"],           out var review);
            int.TryParse(form["comments"],         out var comments);
            int.TryParse(form["ticket_created"],   out var ticketCreated);
            int.TryParse(form["ticket_resolved"],  out var ticketResolved);
            int.TryParse(form["ticket_comments"],  out var ticketComments);
            int.TryParse(form["bonus"],            out var bonus);
            scores.SetScore(discordId, new ScoreEntry
            {
                PrOpened        = prOpened,
                PrMerged        = prMerged,
                ReviewSubmitted = review,
                Comments        = comments,
                TicketCreated   = ticketCreated,
                TicketResolved  = ticketResolved,
                TicketComments  = ticketComments,
                Bonus           = bonus,
                Total           = prOpened + prMerged + review + comments + ticketCreated + ticketResolved + ticketComments + bonus,
            });
            return Results.Redirect("/config/ui");
        });

        app.MapPost("/config/ui/resetscore", async (HttpContext context, ScoreService scores) =>
        {
            if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
            var form      = await context.Request.ReadFormAsync();
            var discordId = form["discord_id"].FirstOrDefault()?.Trim();
            if (!string.IsNullOrEmpty(discordId)) scores.ResetScore(discordId);
            return Results.Redirect("/config/ui");
        });

        app.MapPost("/config/ui/setreaction", async (HttpContext context, PreferencesService prefs) =>
        {
            if (context.Session.GetString("auth") != "1")
                return Results.Text("Unauthorized.", statusCode: 401);
            var form     = await context.Request.ReadFormAsync();
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
            var form     = await context.Request.ReadFormAsync();
            var eventKey = form["event"].FirstOrDefault()?.Trim();
            if (!string.IsNullOrEmpty(eventKey)) prefs.ClearReaction(eventKey);
            return Results.Redirect("/config/ui");
        });

        app.MapPost("/config/ui/setrouletteexclusion", async (HttpContext context, PreferencesService prefs) =>
        {
            if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
            var form      = await context.Request.ReadFormAsync();
            var discordId = form["discord_id"].FirstOrDefault()?.Trim();
            var excluded  = form["excluded"].FirstOrDefault() == "1";
            if (!string.IsNullOrEmpty(discordId))
                prefs.SetRouletteExclusion(discordId, excluded);
            return Results.Redirect("/config/ui");
        });

        app.MapPost("/config/ui/setworkhours", async (HttpContext context, PreferencesService prefs) =>
        {
            if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
            var form  = await context.Request.ReadFormAsync();
            var field = form["field"].FirstOrDefault()?.Trim();
            var value = form["value"].FirstOrDefault()?.Trim();
            if (string.IsNullOrEmpty(value)) return Results.Redirect("/config/ui");
            switch (field)
            {
                case "start":    prefs.SetWorkHoursStart(value);    break;
                case "end":      prefs.SetWorkHoursEnd(value);      break;
                case "timezone": prefs.SetWorkHoursTimezone(value); break;
                case "days":     prefs.SetWorkHoursDays(value);     break;
            }
            return Results.Redirect("/config/ui");
        });

        app.MapPost("/config/ui/setworkdays", async (HttpContext context, PreferencesService prefs) =>
        {
            if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
            var form = await context.Request.ReadFormAsync();
            var days = form["day"].Where(d => !string.IsNullOrEmpty(d)).ToList();
            prefs.SetWorkHoursDays(days.Count > 0 ? string.Join(",", days) : "");
            return Results.Redirect("/config/ui");
        });

        app.MapPost("/config/ui/clearworkhours", async (HttpContext context, PreferencesService prefs) =>
        {
            if (context.Session.GetString("auth") != "1") return Results.Text("Unauthorized.", statusCode: 401);
            var form  = await context.Request.ReadFormAsync();
            var field = form["field"].FirstOrDefault()?.Trim();
            switch (field)
            {
                case "start":    prefs.ClearWorkHoursStart();    break;
                case "end":      prefs.ClearWorkHoursEnd();      break;
                case "timezone": prefs.ClearWorkHoursTimezone(); break;
                case "days":     prefs.ClearWorkHoursDays();     break;
                default: // clear all
                    prefs.ClearWorkHoursStart(); prefs.ClearWorkHoursEnd();
                    prefs.ClearWorkHoursTimezone(); prefs.ClearWorkHoursDays();
                    break;
            }
            return Results.Redirect("/config/ui");
        });
    }
}
