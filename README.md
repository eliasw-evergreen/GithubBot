# GithubBot

A Discord bot that posts rich notifications for GitHub Pull Request activity. When a PR is opened, the bot creates a Discord thread on the message — all subsequent comments, reviews, and the final merge/close notice are posted inside that thread, keeping everything for one PR grouped together.

## Features

- **PR opened/reopened/draft/ready** — rich embed with author, branch arrows, change stats, and description
- **PR merged** — purple embed posted into the existing thread, thread is then archived
- **PR closed without merge** — red embed posted into the existing thread, thread is then archived
- **PR comments and review comments** — posted inside the PR's thread, including file/line location for inline review comments
- **Review requested / assigned** — notification embed sent into the PR's thread
- **Azure DevOps ticket detection** — if the PR description contains a DevOps work item link, a linked **Ticket** field is added to the embed automatically
- **Discord mentions** — if a GitHub user is mapped to a Discord user, the bot `@mentions` them instead of showing a plain username
- **Configurable reactions** — set emoji reactions for every event type via `.env` or override at runtime with `/setreaction`
- **Slash commands** — manage user mappings and reaction preferences directly from Discord
- **File logging** — daily rolling log files written to `logs/` in the repo root via Serilog

## How it works

```
GitHub repo  ──webhook──▶  /ghwebhook  ──▶  WebhookEventDispatcher  ──▶  Discord channel/thread
Azure DevOps ──webhook──▶  /adowebhook (stub, coming soon)
```

The bot runs an ASP.NET minimal API server alongside a Discord.Net client. GitHub sends webhook events to the server, they're dispatched to event-type handlers via `WebhookEventDispatcher`, and the Discord API is called to post or update messages.

---

## Setup

### 1. Create a Discord application and bot

1. Go to [discord.com/developers/applications](https://discord.com/developers/applications) and click **New Application**
2. Name it (e.g. `GithubBot`) and save
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
# Clone the repo
git clone https://github.com/eliasw-evergreen/GithubBot.git
cd GithubBot

# Create your environment file
cp .env.example .env
nano .env   # fill in all values (see Environment Variables section below)

# Run
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

### 4. Expose the webhook endpoint

The bot listens on `PORT` (default `3000`). GitHub needs to reach it over HTTPS. Two options:

**Option A — Reverse proxy with nginx + Certbot (recommended)**

```nginx
server {
    listen 443 ssl;
    server_name your-vps-domain.com;

    ssl_certificate     /etc/letsencrypt/live/your-vps-domain.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/your-vps-domain.com/privkey.pem;

    location /ghwebhook {
        proxy_pass http://localhost:3000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
    }

    location /adowebhook {
        proxy_pass http://localhost:3000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
    }
}
```

**Option B — Direct port (HTTP only, not recommended for production)**

Open port 3000 in your firewall. GitHub supports HTTP webhooks but your secret will travel unencrypted.

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
| `DISCORD_PULL_CHANNEL_ID` | Yes | ID of the channel to post PR notifications in |
| `GITHUB_WEBHOOK_SECRET` | Yes | Secret you set when creating the GitHub webhook |
| `PORT` | No | Port for the webhook server (default: `3000`) |
| `PR_PING_ROLE` | No | Discord role ID to ping when a new PR is opened |
| `CONFIG_ROLE` | No | Discord role ID allowed to use slash commands |
| `PRUNE_DAYS` | No | Days to keep closed PR entries before pruning |
| `DISCORD_TICKET_CHANNEL_ID` | No | Channel ID for Azure DevOps notifications (future use) |
| `ADO_WEBHOOK_SECRET` | No | Secret for the Azure DevOps webhook (future use) |

### Reaction variables

All reactions accept a unicode emoji (`✅`), a full Discord custom emote (`<:name:id>`), or an animated emote (`<a:name:id>`). All can also be overridden at runtime via `/setreaction`.

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

Slash commands are registered automatically to the configured guild when the bot starts. All responses are ephemeral (only visible to you).

| Command | Description |
|---|---|
| `/mapuser` | Map a Discord user to a GitHub username |
| `/unmapuser` | Remove a GitHub mapping for a Discord user |
| `/listmappings` | Show all Discord ↔ GitHub mappings |
| `/setreaction` | Override the emoji for an event type |
| `/clearreaction` | Clear an override and fall back to `.env` |
| `/listreactions` | Show all active reactions and their source (prefs / .env / unset) |

When a new user mapping is added, the bot automatically backfills old PR messages in the channel, replacing the plain GitHub username with the Discord mention.

---

## Notification Examples

| Event | Discord output |
|---|---|
| PR opened | Green embed in the channel + a thread created on the message |
| PR reopened | Thread unarchived, reopened embed posted inside thread |
| Comment added | Blue embed posted inside the PR's thread |
| Inline review comment | Blue embed inside the thread, with file and line number |
| Review approved | Green embed inside the thread, PR author pinged |
| Changes requested | Red embed inside the thread, PR author pinged |
| PR merged | Purple embed inside the thread, thread archived |
| PR closed (no merge) | Red embed inside the thread, thread archived |

---

## Project Structure

```
GithubBot/
├── Program.cs                        # Entry point — ASP.NET minimal API + DI + Serilog
├── GithubBot.csproj                  # .NET 8 project file
├── Handlers/                         # One handler per GitHub event type
│   ├── IGitHubEventHandler.cs
│   ├── PullRequestHandler.cs
│   ├── PullRequestReviewHandler.cs
│   ├── PullRequestReviewCommentHandler.cs
│   └── IssueCommentHandler.cs
├── Services/
│   ├── WebhookEventDispatcher.cs     # Routes event strings to handlers
│   ├── UserMapService.cs             # usermap.json persistence
│   ├── PrMapService.cs               # prmap.json persistence + prune
│   └── PreferencesService.cs        # preferences.json — runtime reaction overrides
├── Discord/
│   ├── DiscordBotService.cs          # Discord.Net client wrapper
│   ├── EmbedBuilders.cs              # All embed construction
│   └── SlashCommandHandler.cs        # Slash command registration + handling
├── Models/
│   ├── GitHubUser.cs
│   ├── Repository.cs
│   ├── PullRequest.cs
│   ├── Review.cs
│   └── IssueComment.cs
├── logs/                             # Daily rolling log files (gitignored)
├── .env                              # Your secrets (gitignored)
└── .env.example                      # Template for .env
```
