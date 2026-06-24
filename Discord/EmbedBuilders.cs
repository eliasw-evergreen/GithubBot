using System.Text.RegularExpressions;
using Discord;
using GithubBot.Models;
using GithubBot.Services;

namespace GithubBot.Discord;

public static class EmbedBuilders
{
    public static Embed PrEmbed(PullRequest pr, Repository repo, string action, UserMapService userMap,
        string? openedReaction = null, string? reopenedReaction = null,
        string? readyForReviewReaction = null, string? convertedToDraftReaction = null,
        string? mergedReaction = null, string? closedReaction = null)
    {
        var openedEmoji   = ReactionEmoji(openedReaction)           ?? "🔀";
        var reopenedEmoji = ReactionEmoji(reopenedReaction)         ?? "🔁";
        var readyEmoji    = ReactionEmoji(readyForReviewReaction)   ?? "✅";
        var draftEmoji    = ReactionEmoji(convertedToDraftReaction) ?? "📝";
        var mergedEmoji   = ReactionEmoji(mergedReaction)           ?? "🟣";
        var closedEmoji   = ReactionEmoji(closedReaction)           ?? "🔴";

        var (title, color) = action switch
        {
            "opened"             => ($"{openedEmoji} Pull Request Opened",   Color.Green),
            "reopened"           => ($"{reopenedEmoji} Pull Request Reopened", Color.Orange),
            "ready_for_review"   => ($"{readyEmoji} PR Ready for Review",    Color.Green),
            "converted_to_draft" => ($"{draftEmoji} PR Converted to Draft",  Color.LightGrey),
            "closed_merged"      => ($"{mergedEmoji} Pull Request Merged",   Color.Purple),
            "closed_unmerged"    => ($"{closedEmoji} Pull Request Closed",   Color.Red),
            _                    => ("Pull Request",                         Color.Default),
        };

        var draftTag = pr.Draft ? " *(Draft)*" : "";
        var cleaned = CleanBody(StripDevOpsLinks(pr.Body));
        var description = !string.IsNullOrWhiteSpace(cleaned.Text) ? Truncate(cleaned.Text, 1024) : "*No description provided.*";
        var author = Mention(userMap, pr.User.Login);

        var embed = new EmbedBuilder()
            .WithTitle(title)
            .WithUrl(pr.HtmlUrl)
            .WithColor(color)
            .WithAuthor(pr.User.Login, pr.User.AvatarUrl, $"https://github.com/{pr.User.Login}")
            .AddField("Pull Request", $"[#{pr.Number} — {pr.Title}{draftTag}]({pr.HtmlUrl})")
            .AddField("Author", author, inline: true)
            .AddField("Branches", $"`{pr.Head.Ref}` → `{pr.Base.Ref}`", inline: true)
            .AddField("Changes", $"+{pr.Additions ?? 0} / -{pr.Deletions ?? 0} in {pr.ChangedFiles ?? 0} file(s)", inline: true)
            .WithFooter(repo.FullName)
            .WithCurrentTimestamp();

        if (action is "opened" or "reopened" or "ready_for_review")
            embed.AddField("Description", description);

        var ticket = ExtractDevOpsTicket(pr.Body);
        if (ticket != null)
            embed.AddField("Ticket", $"[#{ticket.Id}]({ticket.Url})", inline: true);

        if (cleaned.ImageUrl != null)
            embed.WithImageUrl(cleaned.ImageUrl);

        if (action == "closed_merged" && pr.MergedBy != null)
        {
            var mergedBy = Mention(userMap, pr.MergedBy.Login);
            embed.AddField("Merged by", mergedBy);
        }

        return embed.Build();
    }

    public static Embed CommentEmbed(IssueComment comment, PullRequest pr, Repository repo, bool isReview, UserMapService userMap, string? commentReaction = null)
    {
        var cleaned = CleanBody(comment.Body);
        var body = !string.IsNullOrWhiteSpace(cleaned.Text) ? Truncate(cleaned.Text, 1024) : "*No content.*";
        var author = Mention(userMap, comment.User.Login);
        var emoji = ReactionEmoji(commentReaction) ?? "💬";

        var embed = new EmbedBuilder()
            .WithTitle(isReview ? $"{emoji} Review Comment" : $"{emoji} PR Comment")
            .WithUrl(comment.HtmlUrl)
            .WithColor(Color.Blue)
            .WithAuthor(comment.User.Login, comment.User.AvatarUrl, $"https://github.com/{comment.User.Login}")
            .AddField("Author", author, inline: true)
            .AddField("Link", $"[View comment]({comment.HtmlUrl})", inline: true)
            .AddField("Message", body)
            .WithFooter(repo.FullName)
            .WithCurrentTimestamp();

        if (isReview && !string.IsNullOrEmpty(comment.Path))
        {
            var line = comment.Line ?? comment.OriginalLine;
            embed.AddField("Location", $"`{comment.Path}` line {line?.ToString() ?? "?"}", inline: true);
        }

        if (cleaned.ImageUrl != null)
            embed.WithImageUrl(cleaned.ImageUrl);

        return embed.Build();
    }

    public static Embed DeletedCommentEmbed(IssueComment comment, PullRequest pr, Repository repo, bool isReview, UserMapService userMap, string? commentReaction = null)
    {
        var cleaned = CleanBody(comment.Body);
        var body = !string.IsNullOrWhiteSpace(cleaned.Text) ? Truncate(cleaned.Text, 1024) : "*No content.*";
        var author = Mention(userMap, comment.User.Login);
        var emoji = ReactionEmoji(commentReaction) ?? "💬";

        var embed = new EmbedBuilder()
            .WithTitle(isReview ? $"🗑️ Review Comment Removed" : $"🗑️ PR Comment Removed")
            .WithUrl(pr.HtmlUrl)
            .WithColor(Color.Red)
            .WithAuthor(comment.User.Login, comment.User.AvatarUrl, $"https://github.com/{comment.User.Login}")
            .AddField("Author", author, inline: true)
            .AddField("Content", body)
            .WithFooter(repo.FullName)
            .WithCurrentTimestamp();

        if (isReview && !string.IsNullOrEmpty(comment.Path))
        {
            var line = comment.Line ?? comment.OriginalLine;
            embed.AddField("Location", $"`{comment.Path}` line {line?.ToString() ?? "?"}", inline: true);
        }

        if (cleaned.ImageUrl != null)
            embed.WithImageUrl(cleaned.ImageUrl);

        return embed.Build();
    }

    public static Embed ReviewRequestEmbed(PullRequest pr, Repository repo, List<GitHubUser> reviewers, GitHubUser sender, UserMapService userMap, string? reviewRequestedReaction = null)
    {
        var reviewerNames = string.Join(", ", reviewers.Select(r => $"**{r.Login}**"));
        var requester = Mention(userMap, sender.Login);
        var emoji = ReactionEmoji(reviewRequestedReaction) ?? "👀";

        return new EmbedBuilder()
            .WithTitle($"{emoji} Review Requested")
            .WithUrl(pr.HtmlUrl)
            .WithColor(Color.Gold)
            .AddField("Pull Request", $"[#{pr.Number} — {pr.Title}]({pr.HtmlUrl})")
            .AddField("Requested by", requester, inline: true)
            .AddField("Reviewers", reviewerNames, inline: true)
            .WithFooter(repo.FullName)
            .WithCurrentTimestamp()
            .Build();
    }

    public static Embed ReviewSubmittedEmbed(Review review, PullRequest pr, Repository repo, UserMapService userMap,
        string? approvedReaction = null, string? changesReaction = null)
    {
        var approvedEmoji = ReactionEmoji(approvedReaction) ?? "✅";
        var changesEmoji  = ReactionEmoji(changesReaction)  ?? "🔄";

        var stateLabel = review.State switch
        {
            "approved"          => $"{approvedEmoji} Approved",
            "changes_requested" => $"{changesEmoji} Changes Requested",
            "commented"         => "💬 Reviewed",
            _                   => $"📝 Review {review.State}",
        };
        var stateColor = review.State switch
        {
            "approved"          => Color.Green,
            "changes_requested" => Color.Red,
            "commented"         => Color.Blue,
            _                   => Color.LightGrey,
        };

        var cleaned = CleanBody(review.Body);
        var body = !string.IsNullOrWhiteSpace(cleaned.Text) ? Truncate(cleaned.Text, 1024) : "*No comment.*";
        var reviewer = Mention(userMap, review.User.Login);

        var embed = new EmbedBuilder()
            .WithTitle(stateLabel)
            .WithUrl(review.HtmlUrl)
            .WithColor(stateColor)
            .WithAuthor(review.User.Login, review.User.AvatarUrl, $"https://github.com/{review.User.Login}")
            .AddField("Pull Request", $"[#{pr.Number} — {pr.Title}]({pr.HtmlUrl})")
            .AddField("Reviewer", reviewer, inline: true)
            .AddField("Review", body)
            .WithFooter(repo.FullName)
            .WithCurrentTimestamp();

        if (cleaned.ImageUrl != null)
            embed.WithImageUrl(cleaned.ImageUrl);

        return embed.Build();
    }

    public static Embed AssignedEmbed(PullRequest pr, Repository repo, GitHubUser assignee, UserMapService userMap, string? assignedReaction = null)
    {
        var emoji = ReactionEmoji(assignedReaction) ?? "👤";
        return new EmbedBuilder()
            .WithTitle($"{emoji} Assigned")
            .WithUrl(pr.HtmlUrl)
            .WithColor(new Color(0x5865F2))
            .AddField("Pull Request", $"[#{pr.Number} — {pr.Title}]({pr.HtmlUrl})")
            .AddField("Assignee", Mention(userMap, assignee.Login), inline: true)
            .WithFooter(repo.FullName)
            .WithCurrentTimestamp()
            .Build();
    }

    private static string Mention(UserMapService userMap, string githubLogin)
    {
        var discordId = userMap.GitHubToDiscord(githubLogin);
        return discordId != null ? $"<@{discordId}>" : $"**{githubLogin}**";
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static string? ReactionEmoji(string? reaction)
    {
        if (string.IsNullOrEmpty(reaction)) return null;
        return reaction.Trim();
    }

    // ── Image handling ────────────────────────────────────────────────────────

    private record CleanedBody(string Text, string? ImageUrl);

    // Matches <img ... src="url" ... /> and markdown ![alt](url)
    private static readonly Regex HtmlImagePattern = new(
        @"<img\b[^>]*\bsrc=""([^""]+)""[^>]*/?>",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    private static readonly Regex MarkdownImagePattern = new(
        @"!\[[^\]]*\]\(([^)]+)\)",
        RegexOptions.None, TimeSpan.FromMilliseconds(100));

    private static CleanedBody CleanBody(string? body)
    {
        if (string.IsNullOrEmpty(body)) return new CleanedBody("", null);

        // Extract first image URL from either syntax
        string? imageUrl = null;
        var htmlMatch = HtmlImagePattern.Match(body);
        var mdMatch   = MarkdownImagePattern.Match(body);

        if (htmlMatch.Success && (!mdMatch.Success || htmlMatch.Index <= mdMatch.Index))
            imageUrl = htmlMatch.Groups[1].Value;
        else if (mdMatch.Success)
            imageUrl = mdMatch.Groups[1].Value;

        // Strip all image tags from text
        var text = HtmlImagePattern.Replace(body, "");
        text = MarkdownImagePattern.Replace(text, "");
        text = text.Trim();

        return new CleanedBody(text, imageUrl);
    }

    // ── DevOps ticket handling ────────────────────────────────────────────────

    private record DevOpsTicket(string Url, string Id);

    private static readonly Regex DevOpsPattern = new(
        @"https://(?:dev\.azure\.com/[\w\-]+|[\w\-]+\.visualstudio\.com)/[\w\-]+/_workitems/edit/(\d+)/?",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    private static DevOpsTicket? ExtractDevOpsTicket(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var m = DevOpsPattern.Match(body);
        if (!m.Success) return null;
        return new DevOpsTicket(m.Value.TrimEnd('/'), m.Groups[1].Value);
    }

    private static readonly Regex DevOpsLinkPattern = new(
        @"\[([^\]]*)\]\(https://(?:dev\.azure\.com/[\w\-]+|[\w\-]+\.visualstudio\.com)/[\w\-]+/_workitems/edit/\d+/?\)|https://(?:dev\.azure\.com/[\w\-]+|[\w\-]+\.visualstudio\.com)/[\w\-]+/_workitems/edit/\d+/?",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    private static string StripDevOpsLinks(string? body)
    {
        if (string.IsNullOrEmpty(body)) return "";
        return DevOpsLinkPattern.Replace(body, "").Trim();
    }
}
