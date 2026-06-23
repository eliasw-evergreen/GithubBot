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
        var description = !string.IsNullOrEmpty(pr.Body) ? Truncate(pr.Body, 1024) : "*No description provided.*";
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

        if (action == "closed_merged" && pr.MergedBy != null)
        {
            var mergedBy = Mention(userMap, pr.MergedBy.Login);
            embed.AddField("Merged by", mergedBy);
        }

        return embed.Build();
    }

    public static Embed CommentEmbed(IssueComment comment, PullRequest pr, Repository repo, bool isReview, UserMapService userMap, string? commentReaction = null)
    {
        var body = !string.IsNullOrEmpty(comment.Body) ? Truncate(comment.Body, 1024) : "*No content.*";
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

        return embed.Build();
    }

    public static Embed DeletedCommentEmbed(IssueComment comment, PullRequest pr, Repository repo, bool isReview, UserMapService userMap, string? commentReaction = null)
    {
        var body = !string.IsNullOrEmpty(comment.Body) ? Truncate(comment.Body, 1024) : "*No content.*";
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
        var body = !string.IsNullOrEmpty(review.Body) ? Truncate(review.Body, 1024) : "*No comment.*";
        var reviewer = Mention(userMap, review.User.Login);

        return new EmbedBuilder()
            .WithTitle(stateLabel)
            .WithUrl(review.HtmlUrl)
            .WithColor(stateColor)
            .WithAuthor(review.User.Login, review.User.AvatarUrl, $"https://github.com/{review.User.Login}")
            .AddField("Pull Request", $"[#{pr.Number} — {pr.Title}]({pr.HtmlUrl})")
            .AddField("Reviewer", reviewer, inline: true)
            .AddField("Review", body)
            .WithFooter(repo.FullName)
            .WithCurrentTimestamp()
            .Build();
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

    // Converts a reaction config value to something usable in embed text.
    // <:name:id> → :name:  |  unicode → as-is  |  null/empty → null
    private static string? ReactionEmoji(string? reaction)
    {
        if (string.IsNullOrEmpty(reaction)) return null;
        return reaction.Trim();
    }
}
