using System.Text;
using GithubBot.Services;

namespace GithubBot.Web;

public static class ConfigUiHtml
{
    public static string Render(
        List<(string Id, string Name)> guildUsers,
        Dictionary<string, List<string>> map)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>User Mappings</title>
            <style>
              body { font-family: system-ui, sans-serif; max-width: 860px; margin: 2rem auto; padding: 0 1rem; background: #1a1a2e; color: #e0e0e0; }
              h1 { color: #a78bfa; margin-bottom: 1.5rem; }
              table { width: 100%; border-collapse: collapse; margin-bottom: 2rem; }
              th { text-align: left; padding: .5rem .75rem; background: #2d2d44; color: #a78bfa; font-size: .85rem; text-transform: uppercase; letter-spacing: .05em; }
              td { padding: .5rem .75rem; border-bottom: 1px solid #2d2d44; vertical-align: top; }
              tr:last-child td { border-bottom: none; }
              .tag { display: inline-block; padding: .15rem .5rem; border-radius: 4px; font-size: .85rem; margin: .1rem .1rem .1rem 0; }
              .tag-gh { background: #1f6feb33; color: #58a6ff; border: 1px solid #1f6feb66; }
              .tag-ado { background: #0078d433; color: #60baff; border: 1px solid #0078d466; }
              button.remove { background: none; border: none; color: #f87171; cursor: pointer; font-size: .8rem; padding: 0 .25rem; opacity: .7; }
              button.remove:hover { opacity: 1; }
              .add-form { display: flex; gap: .5rem; flex-wrap: wrap; align-items: center; }
              .add-form input, .add-form select { background: #2d2d44; border: 1px solid #4a4a6a; color: #e0e0e0; padding: .35rem .6rem; border-radius: 4px; font-size: .9rem; }
              .add-form button[type=submit] { background: #7c3aed; color: #fff; border: none; padding: .35rem .9rem; border-radius: 4px; cursor: pointer; font-size: .9rem; }
              .add-form button[type=submit]:hover { background: #6d28d9; }
              .no-entries { color: #666; font-size: .9rem; }
              .user-name { font-weight: 500; }
              .user-id { font-size: .75rem; color: #666; }
            </style>
            </head>
            <body>
            <h1>User Mappings</h1>
            <table>
            <thead><tr><th>Discord User</th><th>Mappings</th><th>Add</th></tr></thead>
            <tbody>
            """);

        // Rows for users already in the guild
        foreach (var (id, name) in guildUsers)
        {
            map.TryGetValue(id, out var entries);
            entries ??= [];
            sb.Append($"<tr><td><div class=\"user-name\">{Esc(name)}</div><div class=\"user-id\">{id}</div></td><td>");

            foreach (var entry in entries)
            {
                var isAdo = entry.StartsWith("ado:", StringComparison.OrdinalIgnoreCase);
                var label = isAdo ? entry[4..] : entry;
                var cls = isAdo ? "tag-ado" : "tag-gh";
                var title = isAdo ? "DevOps" : "GitHub";
                sb.Append($"<span class=\"tag {cls}\" title=\"{title}\">{Esc(label)}</span>");
                sb.Append($"""
                    <form method="post" action="/config/ui/remove" style="display:inline">
                    <input type="hidden" name="discord_id" value="{id}">
                    <input type="hidden" name="stored_key" value="{Esc(entry)}">
                    <button type="submit" class="remove" title="Remove">✕</button>
                    </form>
                    """);
            }

            if (entries.Count == 0)
                sb.Append("<span class=\"no-entries\">—</span>");

            sb.Append("</td><td>");
            sb.Append($"""
                <form method="post" action="/config/ui/add" class="add-form">
                <input type="hidden" name="discord_id" value="{id}">
                <input type="text" name="username" placeholder="username or email" style="width:180px">
                <select name="type">
                  <option value="github">GitHub</option>
                  <option value="devops">DevOps</option>
                </select>
                <button type="submit">Add</button>
                </form>
                """);

            sb.Append("</td></tr>");
        }

        sb.Append("""
            </tbody>
            </table>
            </body>
            </html>
            """);

        return sb.ToString();
    }

    private static string Esc(string s) => System.Web.HttpUtility.HtmlEncode(s);
}
