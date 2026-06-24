using System.Text;
using GithubBot.Services;

namespace GithubBot.Web;

public record GuildUserInfo(string Id, string Name, List<string> RoleIds);
public record GuildRoleInfo(string Id, string Name);
public record ReactionInfo(string Key, string Label, string? Value, string Source);
public record ChannelInfo(string Id, string Name);
public record ChannelConfigInfo(string Key, string Label, string? Value, string Source);

public static class ConfigUiHtml
{
    public static string Render(
        List<GuildUserInfo> guildUsers,
        List<GuildRoleInfo> roles,
        Dictionary<string, List<string>> map,
        List<ReactionInfo> reactions,
        List<ChannelInfo> textChannels,
        List<ChannelConfigInfo> channelConfigs,
        string? currentPingRole,
        string pingRoleSource,
        IReadOnlyDictionary<string, ScoreEntry> scores,
        IReadOnlySet<string> rouletteExclusions,
        string? currentConfigRole,
        string configRoleSource,
        string? currentCommandRole,
        string commandRoleSource)
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
              * { box-sizing: border-box; }
              body { font-family: system-ui, sans-serif; max-width: 980px; margin: 2rem auto; padding: 0 1rem; background: #1a1a2e; color: #e0e0e0; }
              h1 { color: #a78bfa; }
              h2 { color: #a78bfa; margin-top: 2.5rem; margin-bottom: .75rem; font-size: 1rem; text-transform: uppercase; letter-spacing: .07em; border-bottom: 1px solid #2d2d44; padding-bottom: .4rem; }
              table { width: 100%; border-collapse: collapse; margin-bottom: 1rem; }
              th { text-align: left; padding: .45rem .7rem; background: #2d2d44; color: #a78bfa; font-size: .78rem; text-transform: uppercase; letter-spacing: .05em; }
              td { padding: .45rem .7rem; border-bottom: 1px solid #222238; vertical-align: middle; }
              tr:last-child td { border-bottom: none; }
              tr.hidden { display: none; }
              .tag { display: inline-block; padding: .1rem .45rem; border-radius: 4px; font-size: .8rem; margin: .1rem .1rem .1rem 0; }
              .tag-gh  { background: #1f6feb33; color: #58a6ff; border: 1px solid #1f6feb55; }
              .tag-ado { background: #0078d433; color: #60baff; border: 1px solid #0078d455; }
              button.remove { background: none; border: none; color: #f87171; cursor: pointer; font-size: .8rem; padding: 0 .2rem; opacity: .6; }
              button.remove:hover { opacity: 1; }
              .f { display: flex; gap: .4rem; flex-wrap: wrap; align-items: center; }
              input[type=text], input[type=number], select {
                background: #2d2d44; border: 1px solid #4a4a6a; color: #e0e0e0;
                padding: .3rem .55rem; border-radius: 4px; font-size: .86rem;
              }
              input[type=number] { width: 70px; }
              .btn  { background: #7c3aed; color: #fff; border: none; padding: .3rem .8rem; border-radius: 4px; cursor: pointer; font-size: .86rem; }
              .btn:hover { background: #6d28d9; }
              .btn-sm { background: #44415a; color: #ccc; border: none; padding: .28rem .65rem; border-radius: 4px; cursor: pointer; font-size: .82rem; }
              .btn-sm:hover { background: #5a5575; }
              .btn-red { background: #7f1d1d; color: #fca5a5; border: none; padding: .28rem .65rem; border-radius: 4px; cursor: pointer; font-size: .82rem; }
              .btn-red:hover { background: #991b1b; }
              .no-entries { color: #555; font-size: .88rem; }
              .uname { font-weight: 500; font-size: .92rem; }
              .uid   { font-size: .7rem; color: #555; }
              .filter-bar { display: flex; align-items: center; gap: .6rem; margin-bottom: .75rem; font-size: .88rem; }
              .src { font-size: .72rem; color: #888; margin-left: .35rem; }
              .reaction-val { font-size: 1.05rem; }
              #presence-banner { display: none; background: #92400e; color: #fde68a; padding: .55rem 1rem; border-radius: 6px; margin-bottom: 1.2rem; font-size: .9rem; }
            </style>
            </head>
            <body>
            <div id="presence-banner"></div>
            <h1>Bot Config</h1>
            """);

        // ── User Mappings ───────────────────────────────────────────────────
        sb.Append("<h2>User Mappings</h2>");
        sb.Append("""<div class="filter-bar"><label>Filter by role:</label><select id="role-filter" onchange="filterByRole(this.value)"><option value="">All members</option>""");
        foreach (var role in roles.OrderBy(r => r.Name))
            sb.Append($"<option value=\"{role.Id}\">{Esc(role.Name)}</option>");
        sb.Append("</select></div>");

        sb.Append("<table><thead><tr><th>Discord User</th><th>Mappings</th><th>Add</th><th>Roulette</th></tr></thead><tbody>");
        foreach (var user in guildUsers)
        {
            map.TryGetValue(user.Id, out var entries);
            entries ??= [];
            var roleAttr = string.Join(",", user.RoleIds);
            var excluded = rouletteExclusions.Contains(user.Id);
            sb.Append($"<tr data-roles=\"{roleAttr}\"><td><div class=\"uname\">{Esc(user.Name)}</div><div class=\"uid\">{user.Id}</div></td><td>");
            foreach (var entry in entries)
            {
                var isAdo = entry.StartsWith("ado:", StringComparison.OrdinalIgnoreCase);
                var label = isAdo ? entry[4..] : entry;
                sb.Append($"<span class=\"tag {(isAdo ? "tag-ado" : "tag-gh")}\" title=\"{(isAdo ? "DevOps" : "GitHub")}\">{Esc(label)}</span>");
                sb.Append($"""<form method="post" action="/config/ui/remove" style="display:inline"><input type="hidden" name="discord_id" value="{user.Id}"><input type="hidden" name="stored_key" value="{Esc(entry)}"><button type="submit" class="remove" title="Remove">✕</button></form>""");
            }
            if (entries.Count == 0) sb.Append("<span class=\"no-entries\">—</span>");
            sb.Append($"""
                </td><td><form method="post" action="/config/ui/add" class="f">
                <input type="hidden" name="discord_id" value="{user.Id}">
                <input type="text" name="username" placeholder="username or email" style="width:160px">
                <select name="type"><option value="github">GitHub</option><option value="devops">DevOps</option></select>
                <button type="submit" class="btn">Add</button></form></td>
                <td><form method="post" action="/config/ui/setrouletteexclusion">
                <input type="hidden" name="discord_id" value="{user.Id}">
                <input type="hidden" name="excluded" value="{(excluded ? "0" : "1")}">
                <button type="submit" class="{(excluded ? "btn-red" : "btn-sm")}" title="{(excluded ? "Excluded from roulette — click to include" : "Click to exclude from roulette")}">
                {(excluded ? "Excluded" : "Include")}</button></form></td></tr>
                """);
        }
        sb.Append("</tbody></table>");

        // ── Channels ────────────────────────────────────────────────────────
        sb.Append("<h2>Channels</h2>");
        sb.Append("<table><thead><tr><th>Channel</th><th>Current</th><th>Set</th><th></th></tr></thead><tbody>");
        foreach (var ch in channelConfigs)
        {
            var resolvedName = textChannels.FirstOrDefault(c => c.Id == ch.Value)?.Name;
            var display = ch.Value == null ? "<span class=\"no-entries\">unset</span>"
                : resolvedName != null ? $"#{Esc(resolvedName)}" : $"<code>{ch.Value}</code>";
            sb.Append($"<tr><td>{Esc(ch.Label)}</td><td>{display}<span class=\"src\">[{ch.Source}]</span></td><td>");
            sb.Append($"""
                <form method="post" action="/config/ui/setchannel" class="f">
                <input type="hidden" name="key" value="{ch.Key}">
                <select name="channel_id" style="width:200px">
                  <option value="">— pick a channel —</option>
                """);
            foreach (var tc in textChannels)
                sb.Append($"<option value=\"{tc.Id}\"{(tc.Id == ch.Value ? " selected" : "")}># {Esc(tc.Name)}</option>");
            sb.Append($"""</select><button type="submit" class="btn">Set</button></form></td><td>""");
            if (ch.Source == "prefs")
                sb.Append($"""<form method="post" action="/config/ui/clearchannel"><input type="hidden" name="key" value="{ch.Key}"><button type="submit" class="btn-sm">Clear override</button></form>""");
            sb.Append("</td></tr>");
        }
        sb.Append("</tbody></table>");

        // ── Roles ───────────────────────────────────────────────────────────
        sb.Append("<h2>Roles</h2>");
        sb.Append("<table><thead><tr><th>Role</th><th>Current</th><th>Set</th><th></th></tr></thead><tbody>");

        void RoleRow(string label, string? current, string source, string setAction, string clearAction)
        {
            sb.Append($"<tr><td>{Esc(label)}</td><td>");
            if (current != null)
            {
                var roleName = roles.FirstOrDefault(r => r.Id == current)?.Name;
                sb.Append($"{(roleName != null ? $"@{Esc(roleName)}" : $"<code>{current}</code>")}<span class=\"src\">[{source}]</span>");
            }
            else
                sb.Append("<span class=\"no-entries\">unset</span>");
            sb.Append($"""</td><td><form method="post" action="{setAction}" class="f"><select name="role_id" style="width:200px"><option value="">— pick a role —</option>""");
            foreach (var role in roles.OrderBy(r => r.Name))
                sb.Append($"<option value=\"{role.Id}\"{(role.Id == current ? " selected" : "")}>@{Esc(role.Name)}</option>");
            sb.Append("""</select><button type="submit" class="btn">Set</button></form></td><td>""");
            if (source == "prefs")
                sb.Append($"""<form method="post" action="{clearAction}"><button type="submit" class="btn-sm">Clear override</button></form>""");
            sb.Append("</td></tr>");
        }

        RoleRow("PR Ping Role", currentPingRole, pingRoleSource, "/config/ui/setpingrole", "/config/ui/clearpingrole");
        RoleRow("Config UI Role", currentConfigRole, configRoleSource, "/config/ui/setconfigrole", "/config/ui/clearconfigrole");
        RoleRow("Command Role", currentCommandRole, commandRoleSource, "/config/ui/setcommandrole", "/config/ui/clearcommandrole");

        sb.Append("</tbody></table>");

        // ── Reactions ───────────────────────────────────────────────────────
        sb.Append("<h2>Reactions</h2>");
        sb.Append("<table><thead><tr><th>Event</th><th>Current</th><th>Set</th><th></th></tr></thead><tbody>");
        foreach (var r in reactions)
        {
            var display = string.IsNullOrEmpty(r.Value) ? "<span class=\"no-entries\">unset</span>" : $"<span class=\"reaction-val\">{Esc(r.Value)}</span>";
            sb.Append($"<tr><td>{Esc(r.Label)}</td><td>{display}<span class=\"src\">[{r.Source}]</span></td><td>");
            sb.Append($"""
                <form method="post" action="/config/ui/setreaction" class="f">
                <input type="hidden" name="event" value="{r.Key}">
                <input type="text" name="emoji" placeholder="emoji or emote" style="width:150px">
                <button type="submit" class="btn">Set</button></form>
                """);
            sb.Append("</td><td>");
            if (r.Source == "prefs")
                sb.Append($"""<form method="post" action="/config/ui/clearreaction"><input type="hidden" name="event" value="{r.Key}"><button type="submit" class="btn-sm">Clear</button></form>""");
            sb.Append("</td></tr>");
        }
        sb.Append("</tbody></table>");

        // ── Score Editor ────────────────────────────────────────────────────
        sb.Append("<h2>Score Editor</h2>");
        sb.Append("<table><thead><tr><th>User</th><th>PR Opened</th><th>PR Merged</th><th>Reviews</th><th>Comments</th><th>Total</th><th></th></tr></thead><tbody>");
        foreach (var (discordId, entry) in scores.OrderByDescending(s => s.Value.Total))
        {
            var name = guildUsers.FirstOrDefault(u => u.Id == discordId)?.Name ?? discordId;
            sb.Append($"""
                <tr><td><div class="uname">{Esc(name)}</div><div class="uid">{discordId}</div></td>
                <form method="post" action="/config/ui/setscore">
                <input type="hidden" name="discord_id" value="{discordId}">
                <td><input type="number" name="pr_opened" value="{entry.PrOpened}" min="0"></td>
                <td><input type="number" name="pr_merged" value="{entry.PrMerged}" min="0"></td>
                <td><input type="number" name="review" value="{entry.ReviewSubmitted}" min="0"></td>
                <td><input type="number" name="comments" value="{entry.Comments}" min="0"></td>
                <td><strong>{entry.Total}</strong></td>
                <td class="f"><button type="submit" class="btn">Save</button></form>
                <form method="post" action="/config/ui/resetscore">
                <input type="hidden" name="discord_id" value="{discordId}">
                <button type="submit" class="btn-red">Reset</button></form></td></tr>
                """);
        }
        if (scores.Count == 0)
            sb.Append("<tr><td colspan=\"7\" class=\"no-entries\">No scores recorded yet.</td></tr>");

        // Add user row
        sb.Append("""
            <tr style="background:#222238">
            <form method="post" action="/config/ui/setscore">
            <td><select name="discord_id" style="width:160px"><option value="">— pick a user —</option>
            """);
        foreach (var user in guildUsers.Where(u => !scores.ContainsKey(u.Id)))
            sb.Append($"<option value=\"{user.Id}\">{Esc(user.Name)}</option>");
        sb.Append("""
            </select></td>
            <td><input type="number" name="pr_opened" value="0" min="0"></td>
            <td><input type="number" name="pr_merged" value="0" min="0"></td>
            <td><input type="number" name="review" value="0" min="0"></td>
            <td><input type="number" name="comments" value="0" min="0"></td>
            <td></td>
            <td><button type="submit" class="btn">Add</button></td>
            </form></tr>
            """);

        sb.Append("</tbody></table>");

        // ── JS ──────────────────────────────────────────────────────────────
        sb.Append("""
            <script>
            function filterByRole(roleId) {
              document.querySelectorAll('tbody tr[data-roles]').forEach(row => {
                if (!roleId) { row.classList.remove('hidden'); return; }
                row.classList.toggle('hidden', !row.dataset.roles.split(',').includes(roleId));
              });
            }
            const others = new Set();
            function updateBanner() {
              const b = document.getElementById('presence-banner');
              if (!others.size) { b.style.display = 'none'; return; }
              const names = [...others].join(', ');
              b.textContent = `⚠️ ${names} ${others.size === 1 ? 'is' : 'are'} also editing`;
              b.style.display = 'block';
            }
            const es = new EventSource('/config/ui/events');
            es.onmessage = e => {
              const colon = e.data.indexOf(':');
              const type = e.data.slice(0, colon), name = e.data.slice(colon + 1);
              if (type === 'reload') { location.reload(); return; }
              if (type === 'join') others.add(name);
              else if (type === 'leave') others.delete(name);
              updateBanner();
            };

            // ── Ajax form interception ──────────────────────────────────────
            function flash(btn, ok) {
              const orig = btn.textContent, origBg = btn.style.background;
              btn.textContent = ok ? '✓' : '!';
              btn.style.background = ok ? '#166534' : '#7f1d1d';
              btn.disabled = true;
              setTimeout(() => { btn.textContent = orig; btn.style.background = origBg; btn.disabled = false; }, 1400);
            }

            document.addEventListener('submit', async e => {
              const form = e.target.closest('form');
              if (!form || form.method.toLowerCase() !== 'post') return;
              e.preventDefault();
              const btn = form.querySelector('button[type="submit"]') ?? form.querySelector('button');
              const fd = new FormData(form);
              try {
                const res = await fetch(form.action, { method: 'POST', body: fd });
                if (!res.ok) { flash(btn, false); return; }
                flash(btn, true);
                afterSubmit(form, fd);
              } catch { flash(btn, false); }
            });

            function afterSubmit(form, fd) {
              const action = form.action.replace(location.origin, '');

              // Remove a user mapping tag
              if (action === '/config/ui/remove') {
                const tag = form.previousElementSibling;
                if (tag?.classList.contains('tag')) tag.remove();
                form.remove();
                return;
              }

              // Toggle roulette exclusion
              if (action === '/config/ui/setrouletteexclusion') {
                const nowExcluded = fd.get('excluded') === '1';
                const btn = form.querySelector('button');
                btn.textContent = nowExcluded ? 'Excluded' : 'Include';
                btn.className = nowExcluded ? 'btn-red' : 'btn-sm';
                form.querySelector('input[name="excluded"]').value = nowExcluded ? '0' : '1';
                return;
              }

              // Set reaction — update the display cell in the same row
              if (action === '/config/ui/setreaction') {
                const emoji = fd.get('emoji')?.trim();
                if (!emoji) return;
                const row = form.closest('tr');
                const cell = row?.querySelector('td:nth-child(2)');
                if (cell) cell.innerHTML = `<span class="reaction-val">${emoji}</span><span class="src">[prefs]</span>`;
                // Show the clear button if hidden
                const clearCell = row?.querySelector('td:last-child');
                if (clearCell && !clearCell.querySelector('button'))
                  clearCell.innerHTML = `<form method="post" action="/config/ui/clearreaction"><input type="hidden" name="event" value="${fd.get('event')}"><button type="submit" class="btn-sm">Clear</button></form>`;
                return;
              }

              // Clear reaction
              if (action === '/config/ui/clearreaction') {
                const row = form.closest('tr');
                const cell = row?.querySelector('td:nth-child(2)');
                if (cell) cell.innerHTML = `<span class="no-entries">unset</span><span class="src">[unset]</span>`;
                form.closest('td').innerHTML = '';
                return;
              }

              // Set a role row (pingrole / configrole / commandrole)
              if (['/config/ui/setpingrole','/config/ui/setconfigrole','/config/ui/setcommandrole'].includes(action)) {
                const sel = form.querySelector('select[name="role_id"]');
                const roleId = sel?.value;
                const roleName = sel?.options[sel.selectedIndex]?.text;
                if (!roleId) return;
                const row = form.closest('tr');
                const cell = row?.querySelector('td:nth-child(2)');
                if (cell) cell.innerHTML = `${roleName}<span class="src">[prefs]</span>`;
                const clearCell = row?.querySelector('td:last-child');
                const clearActions = { '/config/ui/setpingrole': '/config/ui/clearpingrole', '/config/ui/setconfigrole': '/config/ui/clearconfigrole', '/config/ui/setcommandrole': '/config/ui/clearcommandrole' };
                if (clearCell && !clearCell.querySelector('button'))
                  clearCell.innerHTML = `<form method="post" action="${clearActions[action]}"><button type="submit" class="btn-sm">Clear override</button></form>`;
                return;
              }

              // Clear a role row
              if (['/config/ui/clearpingrole','/config/ui/clearconfigrole','/config/ui/clearcommandrole'].includes(action)) {
                const row = form.closest('tr');
                const cell = row?.querySelector('td:nth-child(2)');
                if (cell) cell.innerHTML = `<span class="no-entries">unset</span>`;
                form.closest('td').innerHTML = '';
                return;
              }

              // Set channel
              if (action === '/config/ui/setchannel') {
                const sel = form.querySelector('select[name="channel_id"]');
                const name = sel?.options[sel.selectedIndex]?.text?.replace(/^# /, '');
                const row = form.closest('tr');
                const cell = row?.querySelector('td:nth-child(2)');
                if (cell && name) cell.innerHTML = `#${name}<span class="src">[prefs]</span>`;
                const clearCell = row?.querySelector('td:last-child');
                const key = fd.get('key');
                if (clearCell && !clearCell.querySelector('button'))
                  clearCell.innerHTML = `<form method="post" action="/config/ui/clearchannel"><input type="hidden" name="key" value="${key}"><button type="submit" class="btn-sm">Clear override</button></form>`;
                return;
              }

              // Clear channel
              if (action === '/config/ui/clearchannel') {
                const row = form.closest('tr');
                const cell = row?.querySelector('td:nth-child(2)');
                if (cell) cell.innerHTML = `<span class="no-entries">unset</span>`;
                form.closest('td').innerHTML = '';
                return;
              }

              // Save score — update total cell
              if (action === '/config/ui/setscore') {
                const row = form.closest('tr') ?? form.closest('tr');
                // sum the inputs
                const vals = ['pr_opened','pr_merged','review','comments','bonus']
                  .map(n => parseInt(form.querySelector(`input[name="${n}"]`)?.value ?? '0') || 0);
                const total = vals.reduce((a,b) => a+b, 0);
                const totalCell = row?.querySelector('td:nth-child(6)');
                if (totalCell) totalCell.innerHTML = `<strong>${total}</strong>`;
                return;
              }

              // Reset score — zero out all inputs and total
              if (action === '/config/ui/resetscore') {
                const row = form.closest('tr');
                row?.querySelectorAll('input[type="number"]').forEach(i => i.value = '0');
                const totalCell = row?.querySelector('td:nth-child(6)');
                if (totalCell) totalCell.innerHTML = '<strong>0</strong>';
                return;
              }
            }
            </script>
            </body></html>
            """);

        return sb.ToString();
    }

    private static string Esc(string s) => System.Web.HttpUtility.HtmlEncode(s);
}
