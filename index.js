require('dotenv').config();
const { Client, GatewayIntentBits, EmbedBuilder, Colors } = require('discord.js');
const express = require('express');
const crypto = require('crypto');

const client = new Client({ intents: [GatewayIntentBits.Guilds] });
const app = express();

// Map of PR node_id -> Discord message id, so comments can reply in a thread
const prMessageMap = new Map();

// ── Helpers ──────────────────────────────────────────────────────────────────

function verifySignature(req) {
  const secret = process.env.GITHUB_WEBHOOK_SECRET;
  if (!secret) return true; // skip verification if no secret set
  const sig = req.headers['x-hub-signature-256'];
  if (!sig) return false;
  const hmac = crypto.createHmac('sha256', secret);
  hmac.update(req.rawBody);
  return crypto.timingSafeEqual(Buffer.from(sig), Buffer.from(`sha256=${hmac.digest('hex')}`));
}

function prEmbed(pr, repo, action) {
  const colorMap = {
    opened:               Colors.Green,
    reopened:             Colors.Orange,
    ready_for_review:     Colors.Green,
    converted_to_draft:   Colors.Grey,
    closed_merged:        Colors.Purple,
    closed_unmerged:      Colors.Red,
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

  const embed = new EmbedBuilder()
    .setTitle(titleMap[action])
    .setURL(pr.html_url)
    .setColor(colorMap[action])
    .setAuthor({ name: pr.user.login, url: `https://github.com/${pr.user.login}`, iconURL: pr.user.avatar_url })
    .addFields(
      { name: 'Pull Request', value: `[#${pr.number} — ${pr.title}${draftTag}](${pr.html_url})` },
      { name: 'Branches', value: `\`${pr.head.ref}\` → \`${pr.base.ref}\``, inline: true },
      { name: 'Changes', value: `+${pr.additions ?? 0} / -${pr.deletions ?? 0} in ${pr.changed_files ?? 0} file(s)`, inline: true },
    )
    .setFooter({ text: repo.full_name });

  if (action === 'opened' || action === 'reopened' || action === 'ready_for_review') {
    embed.addFields({ name: 'Description', value: description });
  }

  if (action === 'closed_merged' && pr.merged_by) {
    embed.addFields({ name: 'Merged by', value: `[${pr.merged_by.login}](https://github.com/${pr.merged_by.login})` });
  }

  return embed;
}

function commentEmbed(comment, pr, repo, isReview) {
  const body = comment.body ? comment.body.slice(0, 1024) : '*No content.*';
  const embed = new EmbedBuilder()
    .setTitle(isReview ? '💬 Review Comment' : '💬 PR Comment')
    .setURL(comment.html_url)
    .setColor(Colors.Blue)
    .setAuthor({ name: comment.user.login, url: `https://github.com/${comment.user.login}`, iconURL: comment.user.avatar_url })
    .addFields({ name: 'Message', value: body })
    .setFooter({ text: repo.full_name });

  if (isReview && comment.path) {
    embed.addFields({ name: 'Location', value: `\`${comment.path}\` line ${comment.line ?? comment.original_line ?? '?'}`, inline: true });
  }

  embed.addFields({ name: 'Link', value: `[View comment](${comment.html_url})`, inline: true });

  return embed;
}

// ── Webhook server ────────────────────────────────────────────────────────────

app.use(express.json({
  verify: (req, _res, buf) => { req.rawBody = buf; }
}));

app.post('/webhook', async (req, res) => {
  if (!verifySignature(req)) return res.status(401).send('Invalid signature');

  const event = req.headers['x-github-event'];
  const payload = req.body;
  res.sendStatus(200); // ack immediately

  const channel = client.channels.cache.get(process.env.DISCORD_CHANNEL_ID);
  if (!channel) return;

  try {
    if (event === 'pull_request') {
      const { action, pull_request: pr, repository: repo } = payload;

      if (['opened', 'reopened', 'ready_for_review', 'converted_to_draft'].includes(action)) {
        const embed = prEmbed(pr, repo, action);
        const msg = await channel.send({ embeds: [embed] });

        // Create a thread on the PR message so comments can live there
        const thread = await msg.startThread({
          name: `PR #${pr.number} — ${pr.title}`.slice(0, 100),
          autoArchiveDuration: 10080, // 7 days
        });
        prMessageMap.set(pr.node_id, { messageId: msg.id, threadId: thread.id });

      } else if (action === 'closed') {
        const key = action === 'closed' ? pr.node_id : null;
        const actionKey = pr.merged ? 'closed_merged' : 'closed_unmerged';
        const embed = prEmbed(pr, repo, actionKey);

        const stored = prMessageMap.get(pr.node_id);
        if (stored) {
          // Post the close/merge notice into the existing thread
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
        prMessageMap.delete(pr.node_id);
      }

    } else if (event === 'pull_request_review_comment') {
      if (payload.action !== 'created') return;
      const { comment, pull_request: pr, repository: repo } = payload;
      const embed = commentEmbed(comment, pr, repo, true);

      const stored = prMessageMap.get(pr.node_id);
      if (stored) {
        const thread = channel.threads.cache.get(stored.threadId)
          ?? await channel.threads.fetch(stored.threadId).catch(() => null);
        await (thread ?? channel).send({ embeds: [embed] });
      } else {
        await channel.send({ embeds: [embed] });
      }

    } else if (event === 'issue_comment') {
      if (payload.action !== 'created' || !payload.issue.pull_request) return;
      const { comment, issue, repository: repo } = payload;
      // Reconstruct a minimal pr-like object from the issue
      const prNodeId = issue.node_id;
      const embed = commentEmbed(comment, issue, repo, false);

      const stored = prMessageMap.get(prNodeId);
      if (stored) {
        const thread = channel.threads.cache.get(stored.threadId)
          ?? await channel.threads.fetch(stored.threadId).catch(() => null);
        await (thread ?? channel).send({ embeds: [embed] });
      } else {
        await channel.send({ embeds: [embed] });
      }
    }
  } catch (err) {
    console.error(`[${event}] Error:`, err);
  }
});

// ── Bot startup ───────────────────────────────────────────────────────────────

client.once('ready', () => {
  console.log(`Logged in as ${client.user.tag}`);
  app.listen(process.env.PORT ?? 3000, () => {
    console.log(`Webhook server listening on port ${process.env.PORT ?? 3000}`);
  });
});

client.login(process.env.DISCORD_BOT_TOKEN);
