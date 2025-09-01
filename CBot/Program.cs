using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using DotNetEnv;
using static CBot.DataAccess;

namespace CBot
{
    internal class Program
    {
        private static TelegramBotClient? _bot;
        private static ConcurrentDictionary<string, (long chatId, int messageId, long userId, ConcurrentDictionary<long, string> votes)> _activePolls = new();

        static async Task Main()
        {
            try
            {
                Env.Load("C:\\Users\\Efreitor2001\\RiderProjects\\CBot\\CBot\\.env");
                string? botToken = Environment.GetEnvironmentVariable("BOT_TOKEN");

                if (string.IsNullOrEmpty(botToken))
                {
                    Console.WriteLine("BOT_TOKEN not found in environment variables");
                    return;
                }

                _bot = new TelegramBotClient(botToken);

                var receiverOptions = new ReceiverOptions
                {
                    AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery }
                };

                using var cts = new CancellationTokenSource();

                _bot.StartReceiving(HandleUpdateAsync, HandlePollingErrorAsync, receiverOptions, cts.Token);

                var me = await _bot.GetMe();
                Console.WriteLine($"@{me.Username} is running... Press Enter to terminate");
                Console.ReadLine();
                cts.Cancel();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in Main: {ex}");
            }
        }

        private static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            try
            {
                switch (update.Type)
                {
                    case UpdateType.Message:
                        await OnMessage(update.Message!);
                        break;
                    case UpdateType.CallbackQuery:
                        await OnCallbackQuery(update.CallbackQuery!);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка в обработчике: {ex}");
            }
        }

        private static Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            Console.WriteLine(exception);
            return Task.CompletedTask;
        }

        private static async Task OnMessage(Message msg)
        {
            if (msg.Text == null) return;

            var command = msg.Text.ToLower();

            if (command == "/start")
            {
                if (await IsAdmin(msg))
                {
                    var chats = await GetAllChatSettings();
                    if (chats.Any(a => (long)a["chat_id"] == msg.Chat.Id))
                    {
                        Console.WriteLine("Чат есть в БД");
                    }
                    else
                    {
                        AddOrUpdateChatSettings(msg.Chat.Id);
                    }

                    await _bot!.SendMessage(msg.Chat.Id, "Бот активирован!");
                }
                return;
            }

            if (command == "/ban" && msg.ReplyToMessage != null)
            {
                await HandleBanCommand(msg);
                return;
            }

            if (command == "/menu" && await IsAdmin(msg))
            {
                await HandleMenuCommand(msg);
                return;
            }
        }

        private static async Task HandleBanCommand(Message msg)
        {
            if (msg.From!.Id == msg.ReplyToMessage!.From!.Id)
            {
                await _bot!.SendMessage(msg.Chat.Id, "Самоубийство - не выход!");
                return;
            }

            var admins = await _bot!.GetChatAdministrators(msg.Chat.Id);
            if (admins.Any(a => a.User.Id == msg.ReplyToMessage.From.Id))
            {
                await _bot!.SendMessage(msg.Chat.Id, "Нельзя банить администраторов!");
                return;
            }

            var userToBan = msg.ReplyToMessage.From;

            var pollMessage = await _bot!.SendMessage(
                chatId: msg.Chat.Id,
                text: $"Пользователь {msg.From.FirstName} начал голосование за бан {userToBan.FirstName}",
                replyMarkup: new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData($"👿 Забанить [0]", "poll:Yes") },
                    new[] { InlineKeyboardButton.WithCallbackData($"😇 Пощадить [0]", "poll:No") }
                }));

            var votes = new ConcurrentDictionary<long, string>();
            _activePolls.TryAdd($"{msg.Chat.Id}_{pollMessage.MessageId}",
                (msg.Chat.Id, pollMessage.MessageId, userToBan.Id, votes));
            _ = ProcessPollAsync(msg.Chat.Id, pollMessage.MessageId, userToBan, msg.From);
        }

        private static async Task HandleMenuCommand(Message msg)
        {
            await _bot!.SendMessage(
                chatId: msg.Chat.Id,
                text: "Меню бота. Здесь вы можете поменять настройки.",
                replyMarkup: new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData($"🚫 Настройки бана", "menu:BanSettings") },
                    new[] { InlineKeyboardButton.WithCallbackData($"🤐 Настройки мута", "menu:MuteSettings") }
                }));
        }

        private static async Task<bool> IsAdmin(Message msg)
        {
            var admins = await _bot!.GetChatAdministrators(msg.Chat.Id);
            return admins.Any(a => a.User.Id == msg.From!.Id);
        }

        private static async Task OnCallbackQuery(CallbackQuery callbackQuery)
        {
            try
            {
                if (callbackQuery.Data == null) return;

                if (callbackQuery.Data.StartsWith("poll:"))
                {
                    await HandlePollCallback(callbackQuery);
                }
                else if (callbackQuery.Data.StartsWith("menu:"))
                {
                    await HandleMenuCallback(callbackQuery);
                }
                else
                {
                    await _bot!.AnswerCallbackQuery(callbackQuery.Id, "Неизвестная команда");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка обработки callback: {ex}");
                await _bot!.AnswerCallbackQuery(callbackQuery.Id, "Произошла ошибка");
            }
        }

        private static async Task HandlePollCallback(CallbackQuery callbackQuery)
        {
            var key = $"{callbackQuery.Message!.Chat.Id}_{callbackQuery.Message.MessageId}";

            if (_activePolls.TryGetValue(key, out var pollData))
            {
                var (chatId, messageId, userId, votes) = pollData;
                var userVote = callbackQuery.Data.Split(':')[1];
                var voterId = callbackQuery.From.Id;

                votes.TryGetValue(voterId, out var previousVote);

                if (previousVote == userVote)
                {
                    votes.TryRemove(voterId, out _);
                }
                else
                {
                    votes[voterId] = userVote;
                }

                var yesCount = votes.Values.Count(v => v == "Yes");
                var noCount = votes.Values.Count(v => v == "No");

                await _bot!.EditMessageReplyMarkup(
                    chatId: chatId,
                    messageId: messageId,
                    replyMarkup: new InlineKeyboardMarkup(new[]
                    {
                        new[] { InlineKeyboardButton.WithCallbackData($"👿 Забанить [{yesCount}]", "poll:Yes") },
                        new[] { InlineKeyboardButton.WithCallbackData($"😇 Пощадить [{noCount}]", "poll:No") }
                    }));

                await _bot.AnswerCallbackQuery(callbackQuery.Id);
            }
            else
            {
                await _bot!.AnswerCallbackQuery(callbackQuery.Id, "Голосование завершено или не найдено");
            }
        }

        private static async Task HandleMenuCallback(CallbackQuery callbackQuery)
        {
            var menuAction = callbackQuery.Data!.Split(':')[1];
            
            switch (menuAction)
            {
                case "BanSettings":
                    await _bot!.AnswerCallbackQuery(callbackQuery.Id, "Настройки бана");
                    // Добавьте логику для настроек бана
                    break;
                case "MuteSettings":
                    await _bot!.AnswerCallbackQuery(callbackQuery.Id, "Настройки мута");
                    // Добавьте логику для настроек мута
                    break;
                default:
                    await _bot!.AnswerCallbackQuery(callbackQuery.Id, "Неизвестная команда меню");
                    break;
            }
        }

        private static async Task ProcessPollAsync(long chatId, int messageId, User ban, User from)
        {
            await Task.Delay(60000);

            var key = $"{chatId}_{messageId}";
            if (_activePolls.TryRemove(key, out var pollData))
            {
                var (_, _, _, votes) = pollData;
                var yesCount = votes.Values.Count(v => v == "Yes");
                var noCount = votes.Values.Count(v => v == "No");

                string resultText;
                if (yesCount > noCount)
                {
                    await _bot!.BanChatMember(chatId, ban.Id);
                    resultText = $"Пользователь {ban.FirstName} отправляется учить PHP!";
                }
                else
                {
                    resultText = "Голосование провалилось";
                }

                await _bot!.EditMessageText(
                    chatId: chatId,
                    messageId: messageId,
                    text: $"Голосование {from.FirstName} VS {ban.FirstName} завершено.\nЗа: {yesCount}, Против: {noCount}\n{resultText}",
                    replyMarkup: null);
            }
        }
    }
}