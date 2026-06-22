require('dotenv').config();
const { Client, GatewayIntentBits, EmbedBuilder, Colors, REST, Routes, SlashCommandBuilder } = require('discord.js');
const express = require('express');
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

const client = new Client({ intents: [GatewayIntentBits.Guilds] });
const app = express();

// Map of PR node_id -> { messageId, threadId }, so comments can reply in a thread
const prMessageMap = new Map();

// ── User map (Discord ID -> GitHub username) ──────────────────────────────────

const USER_MAP_FILE = path.join(__dirname, 'usermap.json');

function loadUserMap() {
  try {
    return JSON.parse(fs.readFileSync(USER_MAP_FILE, 'utf8'));
  } catch {
    return {};
  }
}

function saveUserMap(map) {
  fs.writeFileSync(USER_MAP_FILE, JSON.stringify(map, null, 2));
}

// githubToDiscord: reverse lookup — values are arrays of github usernames
function githubToDiscord(userMap, githubLogin) {
  const login = githubLogin.toLowerCase();
  return Object.entries(userMap).find(([, names]) => names.some(n => n.toLowerCase() === login))?.[0] ?? null;
}

// ── Slash command registration ────────────────────────────────────────────────

const commands = [
  new SlashCommandBuilder()
    .setName('mapuser')
    .setDescription('Add a GitHub username mapping for a Discord user (multiple allowed)')
    .addUserOption(o => o.setName('discord_user').setDescription('The Discord user').setRequired(true))
    .addStringOption(o => o.setName('github_username').setDescription('Their GitHub username').setRequired(true)),

  new SlashCommandBuilder()
    .setName('unmapuser')
    .setDescription('Remove a GitHub username from a Discord user (omit github_username to remove all)')
    .addUserOption(o => o.setName('discord_user').setDescription('The Discord user').setRequired(true))
    .addStringOption(o => o.setName('github_username').setDescription('Specific GitHub username to remove (leave blank to remove all)').setRequired(false)),

  new SlashCommandBuilder()
    .setName('listmappings')
    .setDescription('Show all Discord <-> GitHub user mappings'),
].map(c => c.toJSON());

async function registerCommands() {
  const rest = new REST().setToken(process.env.DISCORD_BOT_TOKEN);
  try {
    await rest.put(
      Routes.applicationGuildCommands(process.env.DISCORD_CLIENT_ID, process.env.DISCORD_GUILD_ID),
      { body: commands },
    );
    console.log('Slash commands registered');
  } catch (err) {
    console.error('Failed to register slash commands:', err);
  }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

function verifySignature(req) {
  const secret = process.env.GITHUB_WEBHOOK_SECRET;
  if (!secret) return true;
  const sig = req.headers['x-hub-signature-256'];
  if (!sig) return false;
  const hmac = crypto.createHmac('sha256', secret);
  hmac.update(req.rawBody);
  return crypto.timingSafeEqual(Buffer.from(sig), Buffer.from(`sha256=${hmac.digest('hex')}`));
}

function mentionForGithubUser(githubLogin) {
  const userMap = loadUserMap();
  const discordId = githubToDiscord(userMap, githubLogin);
  return discordId ? `<@${discordId}>` : `**${githubLogin}**`;
}

function prEmbed(pr, repo, action) {
  const colorMap = {
    opened:              Colors.Green,
    reopened:            Colors.Orange,
    ready_for_review:    Colors.Green,
    converted_to_draft:  Colors.Grey,
    closed_merged:       Colors.Purple,
    closed_unmerged:     Colors.Red,
  };

  const titleMap = {
    opened:              '🔀 Pull Request Opened',
    reopened:            '🔁 Pull Request Reopened',
    ready_for_review:    '✅ PR Ready for Review',
    converted_to_draft:  '📝 PR Converted to Draft',
    closed_merged:       '🟣 Pull Request Merged',
    closed_unmerged:     '🔴 Pull Request Closed',
  };

  const draftTag = pr.draft ? ' *(Draft)*' : '';
  const description = pr.body ? pr.body.slice(0, 1024) : '*No description provided.*';
  const author = mentionForGithubUser(pr.user.login);

  const embed = new EmbedBuilder()
    .setTitle(titleMap[action])
    .setURL(pr.html_url)
    .setColor(colorMap[action])
    .setAuthor({ name: pr.user.login, url: `https://github.com/${pr.user.login}`, iconURL: pr.user.avatar_url })
    .addFields(
      { name: 'Pull Request', value: `[#${pr.number} — ${pr.title}${draftTag}](${pr.html_url})` },
      { name: 'Author', value: author, inline: true },
      { name: 'Branches', value: `\`${pr.head.ref}\` → \`${pr.base.ref}\``, inline: true },
      { name: 'Changes', value: `+${pr.additions ?? 0} / -${pr.deletions ?? 0} in ${pr.changed_files ?? 0} file(s)`, inline: true },
    )
    .setFooter({ text: repo.full_name });

  if (['opened', 'reopened', 'ready_for_review'].includes(action)) {
    embed.addFields({ name: 'Description', value: description });
  }

  if (action === 'closed_merged' && pr.merged_by) {
    const mergedBy = mentionForGithubUser(pr.merged_by.login);
    embed.addFields({ name: 'Merged by', value: mergedBy });
  }

  return embed;
}

function extractGithubMentions(text) {
  if (!text) return [];
  const userMap = loadUserMap();
  const matches = [...text.matchAll(/@([a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?)/g)];
  const pings = [];
  for (const match of matches) {
    const discordId = githubToDiscord(userMap, match[1]);
    if (discordId) {
      const ping = `<@${discordId}>`;
      if (!pings.includes(ping)) pings.push(ping);
    }
  }
  return pings;
}

function commentEmbed(comment, pr, repo, isReview) {
  const body = comment.body ? comment.body.slice(0, 1024) : '*No content.*';
  const author = mentionForGithubUser(comment.user.login);

  const embed = new EmbedBuilder()
    .setTitle(isReview ? '💬 Review Comment' : '💬 PR Comment')
    .setURL(comment.html_url)
    .setColor(Colors.Blue)
    .setAuthor({ name: comment.user.login, url: `https://github.com/${comment.user.login}`, iconURL: comment.user.avatar_url })
    .addFields(
      { name: 'Author', value: author, inline: true },
      { name: 'Link', value: `[View comment](${comment.html_url})`, inline: true },
      { name: 'Message', value: body },
    )
    .setFooter({ text: repo.full_name });

  if (isReview && comment.path) {
    embed.addFields({ name: 'Location', value: `\`${comment.path}\` line ${comment.line ?? comment.original_line ?? '?'}`, inline: true });
  }

  return embed;
}

// ── Slash command handler ─────────────────────────────────────────────────────

client.on('interactionCreate', async interaction => {
  if (!interaction.isChatInputCommand()) return;

  const userMap = loadUserMap();

  if (interaction.commandName === 'mapuser') {
    const discordUser = interaction.options.getUser('discord_user');
    const githubUsername = interaction.options.getString('github_username').trim();
    const existing = userMap[discordUser.id] ?? [];
    if (existing.some(n => n.toLowerCase() === githubUsername.toLowerCase())) {
      await interaction.reply({ content: `**${githubUsername}** is already mapped to <@${discordUser.id}>.`, ephemeral: true });
      return;
    }
    userMap[discordUser.id] = [...existing, githubUsername];
    saveUserMap(userMap);
    const all = userMap[discordUser.id].map(n => `**[${n}](https://github.com/${n})**`).join(', ');
    await interaction.reply({
      embeds: [new EmbedBuilder()
        .setColor(Colors.Green)
        .setDescription(`<@${discordUser.id}> is now mapped to: ${all}`)],
    });

  } else if (interaction.commandName === 'unmapuser') {
    const discordUser = interaction.options.getUser('discord_user');
    const githubUsername = interaction.options.getString('github_username')?.trim();
    const existing = userMap[discordUser.id];
    if (!existing || existing.length === 0) {
      await interaction.reply({ content: 'No mapping found for that user.', ephemeral: true });
      return;
    }
    if (githubUsername) {
      const updated = existing.filter(n => n.toLowerCase() !== githubUsername.toLowerCase());
      if (updated.length === existing.length) {
        await interaction.reply({ content: `**${githubUsername}** was not mapped to <@${discordUser.id}>.`, ephemeral: true });
        return;
      }
      if (updated.length === 0) {
        delete userMap[discordUser.id];
      } else {
        userMap[discordUser.id] = updated;
      }
      saveUserMap(userMap);
      await interaction.reply({
        embeds: [new EmbedBuilder()
          .setColor(Colors.Orange)
          .setDescription(`Removed **${githubUsername}** from <@${discordUser.id}>'s mappings`)],
      });
    } else {
      delete userMap[discordUser.id];
      saveUserMap(userMap);
      await interaction.reply({
        embeds: [new EmbedBuilder()
          .setColor(Colors.Orange)
          .setDescription(`Removed all GitHub mappings for <@${discordUser.id}>`)],
      });
    }

  } else if (interaction.commandName === 'listmappings') {
    const entries = Object.entries(userMap);
    if (entries.length === 0) {
      await interaction.reply({ content: 'No mappings configured yet.', ephemeral: true });
      return;
    }
    const lines = entries.map(([discordId, names]) => {
      const ghLinks = names.map(n => `**[${n}](https://github.com/${n})**`).join(', ');
      return `<@${discordId}> → ${ghLinks}`;
    });
    await interaction.reply({
      embeds: [new EmbedBuilder()
        .setTitle('Discord ↔ GitHub Mappings')
        .setColor(Colors.Blurple)
        .setDescription(lines.join('\n'))],
    });
  }
});

// ── Webhook server ────────────────────────────────────────────────────────────

app.use(express.json({
  verify: (req, _res, buf) => { req.rawBody = buf; }
}));

app.post('/ghwebhook', async (req, res) => {
  const event = req.headers['x-github-event'];
  const payload = req.body;
  console.log(`[${event}] Received webhook (action: ${payload?.action ?? 'unknown'})`);

  if (!verifySignature(req)) return res.status(401).send('Invalid signature');
  res.sendStatus(200);

  const channel = client.channels.cache.get(process.env.DISCORD_CHANNEL_ID);
  if (!channel) return;

  try {
    if (event === 'pull_request') {
      const { action, pull_request: pr, repository: repo } = payload;

      if (['opened', 'reopened', 'ready_for_review', 'converted_to_draft'].includes(action)) {
        const embed = prEmbed(pr, repo, action);
        const mention = mentionForGithubUser(pr.user.login);
        const rolePing = process.env.PR_PING_ROLE ? `<@&${process.env.PR_PING_ROLE}> ` : '';
        const msg = await channel.send({ content: `${rolePing}${mention} opened a PR`, embeds: [embed] });

        const thread = await msg.startThread({
          name: `PR #${pr.number} — ${pr.title}`.slice(0, 100),
          autoArchiveDuration: 10080,
        });
        prMessageMap.set(pr.node_id, { messageId: msg.id, threadId: thread.id });

      } else if (action === 'closed') {
        const actionKey = pr.merged ? 'closed_merged' : 'closed_unmerged';
        const embed = prEmbed(pr, repo, actionKey);

        const stored = prMessageMap.get(pr.node_id);
        if (stored) {
          const originalMsg = await channel.messages.fetch(stored.messageId).catch(() => null);
          if (originalMsg) {
            if (pr.merged && process.env.MERGED_REACTION) {
              await originalMsg.react(process.env.MERGED_REACTION);
            } else if (!pr.merged && process.env.CLOSED_REACTION) {
              await originalMsg.react(process.env.CLOSED_REACTION);
            }
          }
          const thread = channel.threads.cache.get(stored.threadId)
            ?? await channel.threads.fetch(stored.threadId).catch(() => null);
          if (thread) {
            await thread.send({ embeds: [embed] });
            await thread.setArchived(true);
          } else {
            await channel.send({ embeds: [embed] });
          }
        } else {
          await channel.send({ embeds: [embed] });
        }
      }

    } else if (event === 'pull_request_review_comment') {
      if (payload.action !== 'created') return;
      const { comment, pull_request: pr, repository: repo } = payload;
      const embed = commentEmbed(comment, pr, repo, true);

      const mentionedPings = extractGithubMentions(comment.body);

      const stored = prMessageMap.get(pr.node_id);
      const target = stored
        ? (channel.threads.cache.get(stored.threadId) ?? await channel.threads.fetch(stored.threadId).catch(() => null) ?? channel)
        : channel;
      await target.send({ content: mentionedPings.join(' ') || undefined, embeds: [embed] });

      if (stored && process.env.COMMENT_REACTION) {
        const originalMsg = await channel.messages.fetch(stored.messageId).catch(() => null);
        if (originalMsg) await originalMsg.react(process.env.COMMENT_REACTION);
      }

    } else if (event === 'issue_comment') {
      if (payload.action !== 'created' || !payload.issue.pull_request) return;
      const { comment, issue, repository: repo } = payload;
      const embed = commentEmbed(comment, issue, repo, false);

      const mentionedPings = extractGithubMentions(comment.body);

      const stored = prMessageMap.get(issue.node_id);
      const target = stored
        ? (channel.threads.cache.get(stored.threadId) ?? await channel.threads.fetch(stored.threadId).catch(() => null) ?? channel)
        : channel;
      await target.send({ content: mentionedPings.join(' ') || undefined, embeds: [embed] });

      if (stored && process.env.COMMENT_REACTION) {
        const originalMsg = await channel.messages.fetch(stored.messageId).catch(() => null);
        if (originalMsg) await originalMsg.react(process.env.COMMENT_REACTION);
      }
    }
  } catch (err) {
    console.error(`[${event}] Error:`, err);
  }
});

// ── Bot startup ───────────────────────────────────────────────────────────────

client.once('ready', async () => {
  console.log(`Logged in as ${client.user.tag}`);
  await registerCommands();
  app.listen(process.env.PORT ?? 3000, () => {
    console.log(`Webhook server listening on port ${process.env.PORT ?? 3000}`);
  });
});

client.login(process.env.DISCORD_BOT_TOKEN);
