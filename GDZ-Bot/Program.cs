using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace FoxfordAnswersBot
{
    class Program
    {
        // Убедись, что токен и ID здесь правильные, или ты читаешь их из файла
        public static string BOT_TOKEN = "8558881398:AAGvC6haknvCSqq4siPbdavp1g5_xUsOUyY";
        public static long ADMIN_ID = 1283430447;
        public static string CODE_VERSION = "0.1.43";

        private static TelegramBotClient? botClient;

        static async Task Main(string[] args)
        {
            Console.WriteLine("🤖 Запуск бота для ответов Foxford...\n");

            DatabaseHelper.InitializeDatabase();

            botClient = new TelegramBotClient(BOT_TOKEN);

            var me = await botClient.GetMe();
            Console.WriteLine($"✅ Бот запущен: @{me.Username}\n");
            Console.WriteLine($"👤 Админ ID: {ADMIN_ID}");
            Console.WriteLine($"📊 Заданий в базе: {DatabaseHelper.GetTotalTasksCount()}\n");
            Console.WriteLine($"🔢 Версия бота: {CODE_VERSION}\n");
            // --- УДАЛЯЕМ СТРОКУ ПРО CTRL+C ---
            // Console.WriteLine("Бот работает. Нажми Ctrl+C для остановки.\n");

            var cts = new CancellationTokenSource();
            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = Array.Empty<UpdateType>()
            };

            botClient.StartReceiving(
                updateHandler: HandleUpdateAsync,
                errorHandler: HandlePollingErrorAsync,
                receiverOptions: receiverOptions,
                cancellationToken: cts.Token
            );

            await botClient.SetMyShortDescription("✅ Ищу ответы и принимаю задания. Бот включен 🟢", cancellationToken: cts.Token);
            await botClient.SetMyCommands(new[]
            {
                new BotCommand { Command = "start", Description = "Перезапустить бота / Главное меню" },
                new BotCommand { Command = "cancel", Description = "Отменить текущее действие" }
            }, cancellationToken: cts.Token);

            await botClient.SetMyCommands(new[]
            {
                new BotCommand { Command = "start", Description = "Перезапустить бота" },
                new BotCommand { Command = "cancel", Description = "Отменить текущее действие" },
                new BotCommand { Command = "admin", Description = "Панель администратора" }
            }, scope: new BotCommandScopeChat { ChatId = ADMIN_ID }, cancellationToken: cts.Token);

            // --- НОВЫЙ КОД ВМЕСТО Console.ReadLine() ---

            Console.WriteLine("Бот успешно запущен и работает в режиме службы.");

            // Этот обработчик будет "ловить" Ctrl+C (локально)
            // или SIGTERM (сигнал остановки от systemd)
            Console.CancelKeyPress += (sender, e) =>
            {
                Console.WriteLine("🤖 Получен сигнал остановки (SIGTERM/Ctrl+C)...");
                e.Cancel = true; // Не даем процессу умереть сразу
                cts.Cancel();    // Отправляем сигнал отмены в StartReceiving
            };

            // Ждем, пока CancellationTokenSource не будет отменен
            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (TaskCanceledException)
            {
                // Это ожидаемое исключение при остановке
                Console.WriteLine("...Ожидание завершено.");
            }
            // --- КОНЕЦ НОВОГО КОДА ---


            // Этот код теперь выполнится при корректной остановке
            Console.WriteLine("Устанавливаем статус 'Выключен'...");
            // Даем 1 секунду на отправку, но используем CancellationToken.None
            try
            {
                await botClient.SetMyShortDescription("Бот выключен на техническое обслуживание 🔴", cancellationToken: CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Не удалось установить статус: {ex.Message}");
            }

            Console.WriteLine("Бот корректно остановлен.");
            // cts.Cancel() был выше, здесь не нужен
        }

        static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            try
            {
                if (update.Type == UpdateType.Message)
                {
                    await MessageHandler.HandleMessage(botClient, update.Message!, ADMIN_ID);
                }
                else if (update.Type == UpdateType.CallbackQuery)
                {
                    await CallbackHandler.HandleCallback(botClient, update.CallbackQuery!, ADMIN_ID);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка обработки: {ex.Message}\n{ex.StackTrace}");
            }
        }

        static Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            Console.WriteLine($"❌ Ошибка Telegram API: {exception.Message}");
            return Task.CompletedTask;
        }
    }
}