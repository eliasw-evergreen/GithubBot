# GithubBot

A Discord bot that posts rich notifications for GitHub Pull Request activity. When a PR is opened, the bot creates a Discord thread on the message — all subsequent comments, reviews, and the final merge/close notice are posted inside that thread, keeping everything for one PR grouped together.

## Features

- **PR opened/reopened/draft/ready** — rich embed with author, branch arrows, change stats, and description
- **PR merged** — purple embed posted into the existing thread, thread is then archived
- **PR closed without merge** — red embed posted into the existing thread, thread is then archived
- **PR comments and review comments** — posted inside the PR's thread, including file/line location for inline review comments
- **Discord mentions** — if a GitHub user is mapped to a Discord user, the bot `@mentions` them instead of showing a plain username
- **Slash commands** — manage GitHub ↔ Discord user mappings directly from Discord

## How it works

```
GitHub repo  ──webhook──▶  Express server (port 3000)  ──▶  discord.js bot  ──▶  Discord channel
```

The bot runs an Express HTTP server alongside the Discord client. GitHub sends webhook events to the server, the server parses them and calls the Discord API to post or update messages.

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
   - Bot permissions: `Send Messages`, `Create Public Threads`, `Send Messages in Threads`, `Read Message History`
   - Copy the generated URL, open it in a browser, and invite the bot to your server
6. Back on the **General Information** page, copy the **Application ID** — this is your `DISCORD_CLIENT_ID`

### 2. Get Discord IDs

Enable **Developer Mode** in Discord (User Settings → Advanced → Developer Mode), then:

- **Server ID (`DISCORD_GUILD_ID`)** — right-click your server name → Copy Server ID
- **Channel ID (`DISCORD_CHANNEL_ID`)** — right-click the channel you want notifications in → Copy Channel ID

### 3. Deploy the bot on your VPS

```bash
# Clone the repo
git clone https://github.com/eliasw-evergreen/GithubBot.git
cd GithubBot

# Install dependencies
npm install

# Create your environment file
cp .env.example .env
nano .env   # fill in all values (see Environment Variables section below)

# Start the bot
npm start
```

To keep it running after you disconnect, use [PM2](https://pm2.keymetrics.io/):

```bash
npm install -g pm2
pm2 start index.js --name githubbot
pm2 save
pm2 startup   # follow the printed command to enable auto-start on reboot
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

| Variable | Description |
|---|---|
| `DISCORD_BOT_TOKEN` | Bot token from the Discord Developer Portal |
| `DISCORD_CLIENT_ID` | Application ID from the Discord Developer Portal |
| `DISCORD_GUILD_ID` | ID of the Discord server the bot is in |
| `DISCORD_CHANNEL_ID` | ID of the channel to post PR notifications in |
| `GITHUB_WEBHOOK_SECRET` | Secret you set when creating the GitHub webhook |
| `PORT` | Port for the Express server (default: `3000`) |

---

## Slash Commands

Slash commands are registered automatically to the configured guild when the bot starts.

### `/mapuser`

Map a Discord user to their GitHub username. Once mapped, the bot will `@mention` them in Discord whenever their GitHub account appears in a PR notification.

```
/mapuser discord_user:@Elias github_username:eliasw-evergreen
```

### `/unmapuser`

Remove the GitHub mapping for a Discord user.

```
/unmapuser discord_user:@Elias
```

### `/listmappings`

Show all current Discord ↔ GitHub mappings.

```
/listmappings
```

Mappings are stored in `usermap.json` on disk and persist across restarts. The file is read on every incoming webhook event, so changes take effect immediately without restarting the bot.

---

## Notification Examples

| Event | Discord output |
|---|---|
| PR opened | Green embed in the channel + a thread created on the message |
| Comment added | Blue embed posted inside the PR's thread |
| Inline review comment | Blue embed inside the thread, with file and line number |
| PR merged | Purple embed inside the thread, thread archived |
| PR closed (no merge) | Red embed inside the thread, thread archived |

---

## Project Structure

```
GithubBot/
├── index.js          # Bot entry point — Express webhook server + discord.js client
├── usermap.json      # Persisted Discord <-> GitHub mappings (auto-created, gitignored)
├── .env              # Your secrets (gitignored)
├── .env.example      # Template for .env
└── package.json
```
