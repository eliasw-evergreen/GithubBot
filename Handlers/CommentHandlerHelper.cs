using Discord;
using GithubBot.Discord;
using GithubBot.Services;

namespace GithubBot.Handlers;

internal static class CommentHandlerHelper
{
    internal static async Task<bool> HandleCommentDeleteOrEdit(
        string action, long commentId, Embed embed,
        DiscordBotService discord, CommentMapService commentMap)
    {
        var entry = commentMap.Get(commentId);
        if (entry == null) return false;

        if (action == "deleted")
        {
            var original = await discord.GetMessageAsync(entry.ChannelId, entry.MessageId);
            if (original?.Embeds.FirstOrDefault() is IEmbed existing)
                await discord.EditMessageAsync(entry.ChannelId, entry.MessageId, null,
                    EmbedBuilders.MarkCommentDeleted(existing));
            commentMap.Remove(commentId);
            return true;
        }

        if (action == "edited")
        {
            await discord.EditMessageAsync(entry.ChannelId, entry.MessageId, null, embed);
            return true;
        }

        return false;
    }
}
