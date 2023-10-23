﻿using System.Text.Json;
using System.Text.RegularExpressions;

using Discord;
using Discord.Interactions;
using Discord.Rest;

using GeodeDiscord.Database;
using GeodeDiscord.Database.Entities;

using JetBrains.Annotations;

namespace GeodeDiscord.Modules;

public partial class QuoteModule {
    [Group("import", "Import quotes."), RequireUserPermission(GuildPermission.Administrator)]
    public class ImportModule : InteractionModuleBase<SocketInteractionContext> {
        private readonly ApplicationDbContext _db;

        public ImportModule(ApplicationDbContext db) => _db = db;

        [SlashCommand("manual-quoter", "Sets the quoter of a quote."), EnabledInDm(false),
         UsedImplicitly]
        public async Task ManualQuoter([Autocomplete(typeof(QuoteAutocompleteHandler))] string name, IUser newQuoter) {
            Quote? quote = await _db.quotes.FindAsync(name);
            if (quote is null) {
                await RespondAsync($"❌ Quote not found!", ephemeral: true);
                return;
            }
            _db.Remove(quote);
            _db.Add(quote.WithQuoter(newQuoter.Id));
            try { await _db.SaveChangesAsync(); }
            catch (Exception ex) {
                Console.WriteLine(ex.ToString());
                await RespondAsync("❌ Failed to change quote!", ephemeral: true);
                return;
            }
            await RespondAsync($"Quote **{quote.name}** quoter changed to `{newQuoter.Id}`!");
        }

        private readonly record struct UberBotQuote
            (string id, string nick, string channel, string messageId, string text, long time);

        [SlashCommand("uber-bot", "Imports quotes from UB3R-B0T's API response."), EnabledInDm(false),
         UsedImplicitly]
        public async Task UberBot(Attachment attachment) {
            await DeferAsync();
            await FollowupAsync($"Importing quotes from {attachment.Filename}: downloading attachment");

            string data;
            using (HttpClient client = new()) { data = await client.GetStringAsync(attachment.Url); }

            UberBotQuote[]? toImport;
            try {
                await ModifyOriginalResponseAsync(prop =>
                    prop.Content = $"Importing quotes from {attachment.Filename}: deserializing JSON");
                toImport = JsonSerializer.Deserialize<UberBotQuote[]>(data);
            }
            catch (JsonException) {
                await FollowupAsync("❌ Failed to import quotes! (failed to deserialize JSON)");
                return;
            }
            if (toImport is null) {
                await FollowupAsync("❌ Failed to import quotes! (Deserialize returned null)");
                return;
            }

            int importedQuotes = 0;
            for (int i = 0; i < toImport.Length; i++) {
                if (i % 10 == 0)
                    await ModifyOriginalResponseAsync(prop =>
                        prop.Content =
                            $"Importing quotes from {attachment.Filename}: {i}/{toImport.Length.ToString()}");
                if (await UberBotSingle(toImport[i]))
                    importedQuotes++;
            }

            try { await _db.SaveChangesAsync(); }
            catch (Exception ex) {
                Console.WriteLine(ex.ToString());
                await FollowupAsync("❌ Failed to import quotes! (error when writing to the database)");
                return;
            }

            await ModifyOriginalResponseAsync(prop =>
                prop.Content = $"Imported {importedQuotes} quotes from {attachment.Filename}.");
        }
        private async Task<bool> UberBotSingle(UberBotQuote oldQuote) {
            (string id, string nick, string channelName, string messageIdStr, string text, long time) = oldQuote;
            if (!ulong.TryParse(messageIdStr, out ulong messageId)) {
                await FollowupAsync($"⚠️ Failed to import quote {id}! (invalid message ID)");
                return false;
            }
            DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeSeconds(time);

            if (string.IsNullOrWhiteSpace(channelName)) {
                await UberBotInfer(nick, id, messageId, timestamp, channelName, text);
                return true;
            }

            try {
                IMessageChannel? channel =
                    Context.Guild.TextChannels.FirstOrDefault(ch => ch.Name == channelName) ??
                    Context.Guild.StageChannels.FirstOrDefault(ch => ch.Name == channelName) ??
                    Context.Guild.VoiceChannels.FirstOrDefault(ch => ch.Name == channelName);
                if (channel is null) {
                    await FollowupAsync($"⚠️ Failed to import quote {id}! (channel {channelName} not found)");
                    return false;
                }

                IMessage? message = await channel.GetMessageAsync(messageId);
                if (message is null) {
                    await FollowupAsync($"⚠️ Failed to import quote {id}! (message {messageId} not found)");
                    return false;
                }

                IUser? quoter = await message.GetReactionUsersAsync(new Emoji("\ud83d\udcac"), 20).Flatten()
                    .FirstOrDefaultAsync(user => !user.IsBot);
                if (quoter is null) {
                    const ulong uberBotUserId = 85614143951892480;
                    IMessage? uberResponse = await channel.GetMessagesAsync(message, Direction.After, 40)
                        .Flatten()
                        .Where(msg =>
                            msg.Author.Id == uberBotUserId &&
                            msg.Content.StartsWith("New quote added by ", StringComparison.Ordinal) &&
                            msg.Content.Contains($" as #{id} ")).FirstOrDefaultAsync();
                    if (uberResponse is not null) {
                        Regex regex = new($"New quote added by (.*?) as #{id} ");
                        quoter = await UberBotInferUser("quoter", regex.Match(uberResponse.Content).Groups[1].Value, id);
                    }
                }
                if (quoter is null) {
                    await FollowupAsync($"⚠️ Failed to find quoter of quote {id}!");
                }

                _db.Add(await Util.MessageToQuote(quoter?.Id ?? 0, id, message, timestamp));
                return true;
            }
            catch (Exception ex) {
                Console.WriteLine(ex.ToString());
                await FollowupAsync($"⚠️ Failed to import quote {id}! (failed to access channel or message)");
                return false;
            }
        }
        private async Task UberBotInfer(string nick, string id, ulong messageId, DateTimeOffset timestamp,
            string channelName, string text) {
            RestGuildUser? user = await UberBotInferUser("author", nick, id);

            await FollowupAsync($"⚠️ Quote {id} imported with potentially missing data!");

            _db.Add(new Quote {
                name = id,
                messageId = messageId,
                channelId = 0,
                createdAt = timestamp,
                lastEditedAt = timestamp,
                quoterId = 0,

                authorId = user?.Id ?? 0,
                replyAuthorId = 0,
                jumpUrl = string.IsNullOrWhiteSpace(channelName) ? null : $"#{channelName}",

                images = "",
                extraAttachments = 0,

                content = text
            });
        }
        private async Task<RestGuildUser?> UberBotInferUser(string who, string nick, string id) {
            RestGuildUser? user;
            try {
                string searchNick = nick.ToLowerInvariant();
                user = (await Context.Guild.SearchUsersAsync(searchNick)).FirstOrDefault();
                if (user is null) {
                    searchNick = searchNick[..(searchNick.Length / 2)];
                    user = (await Context.Guild.SearchUsersAsync(searchNick)).FirstOrDefault();
                }
                if (user is null)
                    await FollowupAsync($"⚠️ Couldn't get {who} {nick} for quote {id}! (user is null)");
                else
                    await FollowupAsync($"🗒️ Quote {id} {who} inferred as <@{user.Id}>",
                        allowedMentions: AllowedMentions.None);
            }
            catch (Exception ex) {
                Console.WriteLine(ex.ToString());
                await FollowupAsync($"⚠️ Couldn't get {who} {nick} for quote {id}!");
                user = null;
            }
            return user;
        }
    }
}
