# GithubBot

A Discord bot that posts rich notifications for GitHub Pull Request activity. When a PR is opened, the bot creates a Discord thread on the message — all subsequent comments, reviews, and the final merge/close notice are posted inside that thread, keeping everything for one PR grouped together.

## Features

- **PR opened/reopened/draft/ready** — rich embed with author, branch arrows, change stats, and description
- **PR merged** — purple embed posted into the existing thread, thread is then archived
- **PR closed without merge** — red embed posted into the existing thread, thread is then archived
- **PR comments and review comments** — posted inside the PR's thread, including file/line location for inline review comments
- **Review requested / assigned** — notification embed sent into the PR's thread
- **Azure DevOps ticket detection** — if the PR description contains a DevOps work item link, a linked **Ticket** field is added to the embed automatically
- **Comment edit/delete tracking** — edited comments update the Discord message; deleted comments mark it as deleted in red
- **Discord mentions** — if a GitHub user is mapped to a Discord user, the bot `@mentions` them instead of showing a plain username
- **GitHub + DevOps user mapping** — map Discord users to GitHub usernames and/or DevOps emails
- **Configurable reactions** — set emoji reactions for every event type via `.env` or override at runtime
- **Scoring system** — awards points for PR opens, merges, reviews, and comments; `/score` and `/leaderboard` commands
- **Web config UI** — one-time link from `/configui` opens a browser-based editor for all user mappings, channels, ping role, reactions, and scores
- **Slash commands** — manage everything directly from Discord
- **File logging** — daily rolling log files written to `logs/` via Serilog

## How it works

```
GitHub repo  ──webhook──▶  /ghwebhook   ──▶  WebhookEventDispatcher  ──▶  Discord channel/thread
Azure DevOps ──webhook──▶  /adowebhook  (stub, coming soon)
Browser      ──────────▶  /config/ui   ──▶  Web config UI
```

The bot runs an ASP.NET minimal API server alongside a Discord.Net client. GitHub sends webhook events to the server, they're dispatched to event-type handlers, and the Discord API is called to post or update messages.

---

## Setup

### 1. Create a Discord application and bot

1. Go to [discord.com/developers/applications](https://discord.com/developers/applications) and click **New Application**
2. Name it and save
3. Go to **Bot** → click **Reset Token** and copy the token — this is your `DISCORD_BOT_TOKEN`
4. On the same page, enable these **Privileged Gateway Intents**:
   - Server Members Intent
   - Message Content Intent
5. Go to **OAuth2 → URL Generator**:
   - Scopes: `bot`, `applications.commands`
   - Bot permissions: `Send Messages`, `Create Public Threads`, `Send Messages in Threads`, `Read Message History`, `Add Reactions`
   - Copy the generated URL, open it in a browser, and invite the bot to your server
6. Back on the **General Information** page, copy the **Application ID** — this is your `DISCORD_CLIENT_ID`

### 2. Get Discord IDs

Enable **Developer Mode** in Discord (User Settings → Advanced → Developer Mode), then:

- **Server ID (`DISCORD_GUILD_ID`)** — right-click your server name → Copy Server ID
- **Channel ID (`DISCORD_PULL_CHANNEL_ID`)** — right-click the channel you want PR notifications in → Copy Channel ID

### 3. Deploy the bot on your VPS

```bash
git clone https://github.com/eliasw-evergreen/GithubBot.git
cd GithubBot

cp .env.example .env
nano .env   # fill in all values (see Environment Variables below)

dotnet run --project GithubBot.csproj
```

To run as a systemd service, create `/etc/systemd/system/githubbot.service`:

```ini
[Unit]
Description=GithubBot Discord webhook bot
After=network.target

[Service]
WorkingDirectory=/home/youruser/GithubBot
ExecStart=/usr/bin/dotnet run --project GithubBot.csproj
Restart=always
User=youruser

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable --now githubbot
```

### 4. Expose the bot via nginx

The bot listens on `PORT` (default `3000`). Expose it over HTTPS using nginx + Certbot. Add a location block for each path the bot serves:

```nginx
server {
    listen 80;
    server_name your-vps-domain.com;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl;
    server_name your-vps-domain.com;

    ssl_certificate     /etc/letsencrypt/live/your-vps-domain.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/your-vps-domain.com/privkey.pem;

    # GitHub webhooks
    location /ghwebhook {
        proxy_pass http://127.0.0.1:3000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
    }

    # Azure DevOps webhooks (stub)
    location /adowebhook {
        proxy_pass http://127.0.0.1:3000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
    }

    # Web config UI
    location /config {
        proxy_pass http://127.0.0.1:3000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Accel-Buffering no;  # required for SSE presence notifications
    }
}
```

> **Note:** The `/config` block covers all UI paths (`/config`, `/config/ui`, `/config/ui/events`, etc.) since nginx matches by prefix. The `X-Accel-Buffering no` header is required for the live presence notifications to work correctly.

### 5. Configure the GitHub webhook

Do this for each repository you want to monitor:

1. Go to the repo → **Settings → Webhooks → Add webhook**
2. **Payload URL**: `https://your-vps-domain.com/ghwebhook`
3. **Content type**: `application/json`
4. **Secret**: a strong random string — copy it, you'll add it to `.env` as `GITHUB_WEBHOOK_SECRET`
5. **Which events?** → Let me select individual events:
   - Pull requests
   - Pull request reviews
   - Pull request review comments
   - Issue comments
6. Click **Add webhook**

---

## Environment Variables

Copy `.env.example` to `.env` and fill in all values:

| Variable | Required | Description |
|---|---|---|
| `DISCORD_BOT_TOKEN` | Yes | Bot token from the Discord Developer Portal |
| `DISCORD_CLIENT_ID` | Yes | Application ID from the Discord Developer Portal |
| `DISCORD_GUILD_ID` | Yes | ID of the Discord server the bot is in |
| `DISCORD_PULL_CHANNEL_ID` | Yes | Channel to post PR notifications in |
| `GITHUB_WEBHOOK_SECRET` | Yes | Secret set when creating the GitHub webhook |
| `PUBLIC_HOST` | Yes | Public base URL of the server, e.g. `https://your-vps-domain.com` — used to generate web config UI links |
| `PORT` | No | Port for the webhook server (default: `3000`) |
| `PR_PING_ROLE` | No | Discord role ID to ping when a PR is opened or merged |
| `CONFIG_ROLE` | No | Discord role ID allowed to use slash commands |
| `PRUNE_DAYS` | No | Days to keep closed PR entries before pruning (default: `14`) |
| `DISCORD_TICKET_CHANNEL_ID` | No | Channel ID for Azure DevOps notifications (future use) |
| `ADO_WEBHOOK_SECRET` | No | Secret for the Azure DevOps webhook Basic Auth |

### Reaction variables

All reactions accept a unicode emoji (`✅`), a Discord custom emote (`<:name:id>`), or an animated emote (`<a:name:id>`). All can be overridden at runtime via `/setreaction` or the web config UI.

| Variable | Event |
|---|---|
| `OPENED_REACTION` | PR opened |
| `REOPENED_REACTION` | PR reopened |
| `READY_FOR_REVIEW_REACTION` | PR marked ready for review |
| `CONVERTED_TO_DRAFT_REACTION` | PR converted to draft |
| `MERGED_REACTION` | PR merged |
| `CLOSED_REACTION` | PR closed without merge |
| `APPROVED_REACTION` | Review approved |
| `CHANGES_REQUESTED_REACTION` | Review requests changes |
| `REVIEW_REQUESTED_REACTION` | Review requested |
| `ASSIGNED_REACTION` | PR assigned |
| `COMMENT_REACTION` | PR comment or review comment |

---

## Slash Commands

Slash commands are registered automatically on first start. All responses are ephemeral (only visible to you). Commands require the `CONFIG_ROLE` if set.

| Command | Description |
|---|---|
| `/mapuser` | Map a Discord user to a GitHub username or DevOps email |
| `/unmapuser` | Remove a mapping for a Discord user |
| `/listmappings` | Show all Discord ↔ GitHub/DevOps mappings |
| `/setreaction` | Override the emoji for an event type |
| `/clearreaction` | Clear a reaction override and fall back to `.env` |
| `/listreactions` | Show all active reactions and their source |
| `/setpingrole` | Set the role to ping on PR open/merge |
| `/clearpingrole` | Clear the ping role override |
| `/score` | Show your score breakdown (or another user's) |
| `/leaderboard` | Show the top scorers; pass `verbose:True` for per-category breakdown |
| `/configui` | Generate a one-time link to the web config UI |

When a new GitHub user mapping is added, the bot automatically backfills old PR messages in the channel, replacing plain GitHub usernames with Discord mentions.

---

## Web Config UI

Run `/configui` in Discord to receive a private, one-time link. Opening it consumes the token and issues a browser session cookie — the session is valid until you close the browser tab. If a second admin opens their own link simultaneously, both sessions see a live presence banner showing who else is editing.

The UI covers:

| Section | What you can do |
|---|---|
| **User Mappings** | Add/remove GitHub usernames and DevOps emails per Discord user; filter the table by role |
| **Channels** | Set the PR notification channel and ticket channel by picking from a dropdown of guild channels |
| **Ping Role** | Pick the role to ping on PR open/merge |
| **Reactions** | Set or clear reaction overrides per event type |
| **Score Editor** | View and edit all user scores; add users to the table manually; reset scores |

Channel and role changes take effect immediately on the next webhook event — no restart needed.

---

## Scoring

Points are awarded automatically:

| Action | Points | Recipients |
|---|---|---|
| PR opened | 10 | Author + any Discord users `@mentioned` in the PR body |
| PR merged | 15 | Same as PR opened |
| Review submitted | 10 | Reviewer |
| Comment posted | 5 | Commenter |

View with `/score` (personal breakdown) or `/leaderboard`.

---

## Project Structure

```
GithubBot/
├── Program.cs                            # Entry point — ASP.NET minimal API + DI + Serilog
├── GithubBot.csproj
├── Handlers/                             # One handler per GitHub event type
│   ├── IGitHubEventHandler.cs
│   ├── PullRequestHandler.cs
│   ├── PullRequestReviewHandler.cs
│   ├── PullRequestReviewCommentHandler.cs
│   └── IssueCommentHandler.cs
├── Services/
│   ├── WebhookEventDispatcher.cs         # Routes event strings to handlers
│   ├── UserMapService.cs                 # usermap.json — GitHub + DevOps mappings
│   ├── PrMapService.cs                   # prmap.json — PR → Discord message/thread map
│   ├── CommentMapService.cs              # commentmap.json — comment ID → Discord message map
│   ├── PreferencesService.cs             # preferences.json — runtime overrides
│   ├── ScoreService.cs                   # scores.json — per-user score tracking
│   └── ConfigUiTokenService.cs           # One-time tokens + SSE presence for web UI
├── Discord/
│   ├── DiscordBotService.cs              # Discord.Net client wrapper
│   ├── EmbedBuilders.cs                  # All embed construction
│   └── SlashCommandHandler.cs            # Slash command registration + handling
├── Web/
│   └── ConfigUiHtml.cs                   # Server-rendered HTML for the config UI
├── Models/
│   ├── GitHubUser.cs
│   ├── Repository.cs
│   ├── PullRequest.cs
│   ├── Review.cs
│   └── IssueComment.cs
├── logs/                                 # Daily rolling log files (gitignored)
├── .env                                  # Your secrets (gitignored)
└── .env.example                          # Template for .env
```
