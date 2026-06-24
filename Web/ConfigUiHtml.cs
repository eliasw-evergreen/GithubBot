using System.Text;
using GithubBot.Services;

namespace GithubBot.Web;

public record GuildUserInfo(string Id, string Name, List<string> RoleIds);
public record GuildRoleInfo(string Id, string Name);
public record ReactionInfo(string Key, string Label, string? Value, string Source);

public static class ConfigUiHtml
{
    public static string Render(
        List<GuildUserInfo> guildUsers,
        List<GuildRoleInfo> roles,
        Dictionary<string, List<string>> map,
        List<ReactionInfo> reactions)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Bot Config</title>
            <style>
              body { font-family: system-ui, sans-serif; max-width: 920px; margin: 2rem auto; padding: 0 1rem; background: #1a1a2e; color: #e0e0e0; }
              h1, h2 { color: #a78bfa; }
              h2 { margin-top: 2.5rem; margin-bottom: 1rem; font-size: 1.1rem; text-transform: uppercase; letter-spacing: .06em; }
              table { width: 100%; border-collapse: collapse; margin-bottom: 2rem; }
              th { text-align: left; padding: .5rem .75rem; background: #2d2d44; color: #a78bfa; font-size: .82rem; text-transform: uppercase; letter-spacing: .05em; }
              td { padding: .5rem .75rem; border-bottom: 1px solid #2d2d44; vertical-align: middle; }
              tr:last-child td { border-bottom: none; }
              tr.hidden { display: none; }
              .tag { display: inline-block; padding: .15rem .5rem; border-radius: 4px; font-size: .82rem; margin: .1rem .1rem .1rem 0; }
              .tag-gh  { background: #1f6feb33; color: #58a6ff; border: 1px solid #1f6feb66; }
              .tag-ado { background: #0078d433; color: #60baff; border: 1px solid #0078d466; }
              button.remove { background: none; border: none; color: #f87171; cursor: pointer; font-size: .8rem; padding: 0 .25rem; opacity: .7; }
              button.remove:hover { opacity: 1; }
              .row-form { display: flex; gap: .5rem; flex-wrap: wrap; align-items: center; }
              .row-form input, .row-form select { background: #2d2d44; border: 1px solid #4a4a6a; color: #e0e0e0; padding: .35rem .6rem; border-radius: 4px; font-size: .88rem; }
              .row-form button[type=submit] { background: #7c3aed; color: #fff; border: none; padding: .35rem .9rem; border-radius: 4px; cursor: pointer; font-size: .88rem; }
              .row-form button[type=submit]:hover { background: #6d28d9; }
              .btn-clear { background: #44415a; color: #ccc; border: none; padding: .35rem .75rem; border-radius: 4px; cursor: pointer; font-size: .88rem; }
              .btn-clear:hover { background: #5a5575; }
              .no-entries { color: #555; font-size: .9rem; }
              .user-name { font-weight: 500; }
              .user-id { font-size: .72rem; color: #555; }
              .filter-bar { display: flex; align-items: center; gap: .75rem; margin-bottom: 1rem; }
              .filter-bar select { background: #2d2d44; border: 1px solid #4a4a6a; color: #e0e0e0; padding: .35rem .7rem; border-radius: 4px; font-size: .9rem; }
              .source-tag { font-size: .75rem; color: #888; margin-left: .4rem; }
              .reaction-val { font-size: 1.1rem; min-width: 2rem; display: inline-block; }
              #presence-banner { display: none; background: #92400e; color: #fde68a; padding: .6rem 1rem; border-radius: 6px; margin-bottom: 1.25rem; font-size: .92rem; }
            </style>
            </head>
            <body>
            <div id="presence-banner"></div>
            <h1>Bot Config</h1>
            """);

        // ── User Mappings ────────────────────────────────────────────────────
        sb.Append("<h2>User Mappings</h2>");

        // Role filter dropdown
        sb.Append("""<div class="filter-bar"><label for="role-filter">Filter by role:</label><select id="role-filter" onchange="filterByRole(this.value)"><option value="">All members</option>""");
        foreach (var role in roles.OrderBy(r => r.Name))
            sb.Append($"<option value=\"{role.Id}\">{Esc(role.Name)}</option>");
        sb.Append("</select></div>");

        sb.Append("<table><thead><tr><th>Discord User</th><th>Mappings</th><th>Add</th></tr></thead><tbody>");

        foreach (var user in guildUsers)
        {
            map.TryGetValue(user.Id, out var entries);
            entries ??= [];
            var roleAttr = string.Join(",", user.RoleIds);

            sb.Append($"<tr data-roles=\"{roleAttr}\"><td><div class=\"user-name\">{Esc(user.Name)}</div><div class=\"user-id\">{user.Id}</div></td><td>");

            foreach (var entry in entries)
            {
                var isAdo = entry.StartsWith("ado:", StringComparison.OrdinalIgnoreCase);
                var label = isAdo ? entry[4..] : entry;
                var cls = isAdo ? "tag-ado" : "tag-gh";
                var title = isAdo ? "DevOps" : "GitHub";
                sb.Append($"<span class=\"tag {cls}\" title=\"{title}\">{Esc(label)}</span>");
                sb.Append($"""
                    <form method="post" action="/config/ui/remove" style="display:inline">
                    <input type="hidden" name="discord_id" value="{user.Id}">
                    <input type="hidden" name="stored_key" value="{Esc(entry)}">
                    <button type="submit" class="remove" title="Remove">✕</button>
                    </form>
                    """);
            }

            if (entries.Count == 0)
                sb.Append("<span class=\"no-entries\">—</span>");

            sb.Append("</td><td>");
            sb.Append($"""
                <form method="post" action="/config/ui/add" class="row-form">
                <input type="hidden" name="discord_id" value="{user.Id}">
                <input type="text" name="username" placeholder="username or email" style="width:170px">
                <select name="type">
                  <option value="github">GitHub</option>
                  <option value="devops">DevOps</option>
                </select>
                <button type="submit">Add</button>
                </form>
                """);

            sb.Append("</td></tr>");
        }

        sb.Append("</tbody></table>");

        // ── Reactions ────────────────────────────────────────────────────────
        sb.Append("<h2>Reactions</h2>");
        sb.Append("<table><thead><tr><th>Event</th><th>Current</th><th>Set</th><th></th></tr></thead><tbody>");

        foreach (var r in reactions)
        {
            var display = string.IsNullOrEmpty(r.Value) ? "<span class=\"no-entries\">unset</span>" : $"<span class=\"reaction-val\">{Esc(r.Value)}</span>";
            var sourceTag = $"<span class=\"source-tag\">[{r.Source}]</span>";

            sb.Append($"<tr><td>{Esc(r.Label)}</td><td>{display}{sourceTag}</td><td>");
            sb.Append($"""
                <form method="post" action="/config/ui/setreaction" class="row-form">
                <input type="hidden" name="event" value="{r.Key}">
                <input type="text" name="emoji" placeholder="emoji or emote" style="width:160px">
                <button type="submit">Set</button>
                </form>
                """);
            sb.Append("</td><td>");
            if (r.Source == "prefs")
            {
                sb.Append($"""
                    <form method="post" action="/config/ui/clearreaction">
                    <input type="hidden" name="event" value="{r.Key}">
                    <button type="submit" class="btn-clear">Clear override</button>
                    </form>
                    """);
            }
            sb.Append("</td></tr>");
        }

        sb.Append("</tbody></table>");

        // ── JS for role filtering ────────────────────────────────────────────
        sb.Append("""
            <script>
            function filterByRole(roleId) {
              document.querySelectorAll('tbody tr[data-roles]').forEach(row => {
                if (!roleId) { row.classList.remove('hidden'); return; }
                const roles = row.dataset.roles.split(',');
                row.classList.toggle('hidden', !roles.includes(roleId));
              });
            }

            const others = new Set();
            function updateBanner() {
              const banner = document.getElementById('presence-banner');
              if (others.size === 0) { banner.style.display = 'none'; return; }
              const names = [...others].join(', ');
              const verb = others.size === 1 ? 'is' : 'are';
              banner.textContent = `⚠️ ${names} ${verb} also editing`;
              banner.style.display = 'block';
            }
            const es = new EventSource('/config/ui/events');
            es.onmessage = e => {
              const colon = e.data.indexOf(':');
              const type = e.data.slice(0, colon);
              const name = e.data.slice(colon + 1);
              if (type === 'join') others.add(name);
              else if (type === 'leave') others.delete(name);
              updateBanner();
            };
            </script>
            </body></html>
            """);

        return sb.ToString();
    }

    private static string Esc(string s) => System.Web.HttpUtility.HtmlEncode(s);
}
