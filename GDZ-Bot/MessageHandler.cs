using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace FoxfordAnswersBot
{
    public static class MessageHandler
    {
        // Раздельные состояния
        private static Dictionary<long, AdminState> adminStates = new Dictionary<long, AdminState>();
        private static Dictionary<long, UserSubmissionState> userSubmissionStates = new Dictionary<long, UserSubmissionState>();
        private static Dictionary<long, UserSearchState> userStates = new Dictionary<long, UserSearchState>();

        public static Dictionary<long, string> adminActionStates = new Dictionary<long, string>();

        // Список предметов (для кнопок)
        public static readonly List<string> SubjectsList = new List<string>
        {
            "Алгебра", "Геометрия", "Информатика", "Физика",
            "Химия", "Биология", "Русский язык", "Литература", "История",
            "Обществознание", "География", "Английский язык", "Вероятность и статистика", "ОБЗР", "Физкультура"
        };

        public static async Task HandleMessage(ITelegramBotClient bot, Message message, long adminId)
        {
            var chatId = message.Chat.Id;
            var text = message.Text ?? "";

            DatabaseHelper.AddUser(chatId, message.From?.FirstName, message.From?.Username);

            if (message.Type == MessageType.Text)
            {
                // Команда /start
                if (text == "/start")
                {
                    // Сбрасываем все состояния при /start
                    CancelSubmission(chatId);
                    ClearUserSearchState(chatId);
                    await HandleStart(bot, chatId, adminId);
                    return;
                }

                if (chatId == adminId)
                {
                    if (text == "/admin")
                    {
                        await ShowAdminPanel(bot, chatId);
                        return;
                    }

                    // Универсальная отмена
                    if (text == "/cancel")
                    {
                        bool wasAdmin = adminStates.ContainsKey(chatId);
                        CancelSubmission(chatId);
                        await bot.SendMessage(chatId, "❌ Действие отменено, все временные файлы удалены.");
                        if (wasAdmin) await ShowAdminPanel(bot, chatId);
                        else await HandleStart(bot, chatId, adminId);
                        return;
                    }

                    // Выход из пакетного режима
                    if (text == "/exit_batch" && adminStates.ContainsKey(chatId) && adminStates[chatId].IsBatchMode)
                    {
                        adminStates.Remove(chatId);
                        await bot.SendMessage(chatId, "✅ Выход из пакетного режима.");
                        await ShowAdminPanel(bot, chatId);
                        return;
                    }
                }

                if (chatId == adminId && adminActionStates.ContainsKey(chatId))
                {
                    string state = adminActionStates[chatId];
                    adminActionStates.Remove(chatId); // Сбрасываем состояние после 1-го сообщения

                    // 1. Ожидание ID для УДАЛЕНИЯ
                    if (state == "awaiting_delete_id")
                    {
                        if (int.TryParse(text, out int idToDelete))
                        {
                            if (DatabaseHelper.DeleteTask(idToDelete))
                            {
                                await bot.SendMessage(chatId, $"✅ Задание с ID {idToDelete} успешно удалено.");
                            }
                            else
                            {
                                await bot.SendMessage(chatId, $"❌ Задание с ID {idToDelete} не найдено.");
                            }
                        }
                        else
                        {
                            await bot.SendMessage(chatId, "❌ Это не ID. Действие отменено.");
                        }
                        await ShowAdminPanel(bot, chatId); // Возвращаемся в админку
                        return;
                    }

                    // 2. Ожидание подтверждения "foxford" для ЗАМЕНЫ БД
                    if (state == "awaiting_db_replace_confirm_text")
                    {
                        if (text.Trim().ToLower() == "foxford")
                        {
                            adminActionStates[chatId] = "awaiting_db_file"; // Устанавливаем новое состояние
                            await bot.SendMessage(chatId, "✅ Подтверждение получено. Теперь отправь мне файл `.db` для замены.");
                        }
                        else
                        {
                            await bot.SendMessage(chatId, "❌ Неверное слово. Замена БД отменена.");
                            await ShowAdminPanel(bot, chatId);
                        }
                        return;
                    }
                }

                // Команды для загрузки скриншотов
                if (text == "/done")
                {
                    await FinishScreenshotUpload(bot, chatId, message.MessageId);
                    return;
                }
                if (text == "/remove")
                {
                    await RemoveLastScreenshot(bot, chatId);
                    return;
                }

                // Обработка состояния добавления (Админ)
                if (adminStates.ContainsKey(chatId))
                {
                    await HandleAdminInput(bot, chatId, text, message.MessageId);
                    return;
                }

                // Обработка состояния добавления (Пользователь)
                if (userSubmissionStates.ContainsKey(chatId))
                {
                    await HandleUserInput(bot, chatId, text, message.MessageId);
                    return;
                }

                // Обработка ссылки на задание (Поиск)
                if (text.Contains("foxford.ru"))
                {
                    await HandleFoxfordLink(bot, chatId, text);
                    return;
                }
            }

            // JSON для импорта (только админ)
            if (message.Type == MessageType.Document && message.Document.FileName?.EndsWith(".json") == true && chatId == adminId)
            {
                await HandleJsonImport(bot, chatId, message.Document);
                return;
            }

            if (message.Type == MessageType.Document && message.Document.FileName?.EndsWith(".db") == true && chatId == adminId)
            {
                if (adminActionStates.ContainsKey(chatId) && adminActionStates[chatId] == "awaiting_db_file")
                {
                    adminActionStates.Remove(chatId); // Сбрасываем состояние
                    await HandleDbUpload(bot, chatId, message.Document);
                }
                else
                {
                    await bot.SendMessage(chatId, "❌ Ты прислал файл `.db`, но я не ожидал его. Используй админ-панель для замены.");
                }
                return;
            }

            // Изображения при добавлении задания (Админ или Пользователь)
            if (message.Type == MessageType.Photo)
            {
                await HandleImageUpload(bot, chatId, message);
                return;
            }

            // По умолчанию показываем меню
            if (message.Type == MessageType.Text)
            {
                await HandleStart(bot, chatId, adminId);
            }
        }

        public static async Task HandleStart(ITelegramBotClient bot, long chatId, long adminId)
        {
            var buttons = new List<InlineKeyboardButton[]>
            {
                new[] { InlineKeyboardButton.WithCallbackData("🔍 Поиск ответов", "search_start") },
                new[] { InlineKeyboardButton.WithCallbackData("📥 Предложить задание", "user_add_start") },
                new[] { InlineKeyboardButton.WithUrl("🛜 Foxford VPN", @"https://t.me/foxford_vpn_bot") }
            };

            if (chatId == adminId)
            {
                buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("⚙️ Админка", "admin_panel") });
            }

            var keyboard = new InlineKeyboardMarkup(buttons);

            string welcomeText = @"👋 Привет! Это бот с ответами на задания Foxford.

📝 Ты можешь:
• Найти ответ через поиск
• Предложить свое задание, если его нет в базе

Или просто отправь ссылку на задание для поиска:
https://foxford.ru/lessons/475003/tasks/301386
";

            await bot.SendMessage(chatId, welcomeText, replyMarkup: keyboard,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true });
        }

        private static async Task HandleFoxfordLink(ITelegramBotClient bot, long chatId, string text)
        {
            var (lessonNum, taskNum) = ParseFoxfordLink(text);

            if (lessonNum == null || taskNum == null)
            {
                await bot.SendMessage(chatId, "❌ Не удалось распознать ссылку. Проверь формат.");
                return;
            }

            // Ищем ТОЛЬКО среди одобренных
            var task = DatabaseHelper.FindTaskByLink(lessonNum, taskNum);

            if (task == null)
            {
                await bot.SendMessage(chatId,
                    $"😔 Ответ на это задание ещё не добавлен.\n\n🔗 Урок: {lessonNum}\n📝 Задание: {taskNum}\n\nВы можете добавить его через кнопку «📥 Предложить задание»");
                return;
            }

            DatabaseHelper.UpdateLastRequest();
            await SendTaskAnswer(bot, chatId, task);
        }

        // Вынесли в отдельный метод, т.к. используется в CallbackHandler
        public static async Task SendTaskAnswer(ITelegramBotClient bot, long chatId, FoxfordTask task)
        {
            string header = $"✅ <b>Найден ответ!</b>\n\n" +
                           $"📚 {task.Grade} класс | {task.Subject}" + GetLevelTypeName(task.LevelType) + "\n" +
                           $"📖 {GetGroupTypeName(task.GroupType)}";

            // Новая логика отображения заголовка
            if (task.GroupType == TaskGroupType.Demo)
            {
                header += $" | Полугодие {task.Semester}";
            }
            else if (task.LessonOrder.HasValue)
            {
                header += $" | Урок №{task.LessonOrder}";
            }

            if (task.TaskOrder.HasValue)
                header += $" | Задание №{task.TaskOrder.Value}";

            if (task.Variant.HasValue)
                header += $" | Вариант {task.Variant.Value}";

            header += $"\n🔗 <a href='https://foxford.ru/lessons/{task.LessonNumber}/tasks/{task.TaskNumber}'>Открыть задание на Foxford</a>\n";


            if (!string.IsNullOrEmpty(task.ScreenshotPaths))
            {
                var screenshots = task.ScreenshotPaths.Split(',');

                // Если скриншот 1
                if (screenshots.Length == 1 && System.IO.File.Exists(screenshots[0]))
                {
                    using var stream = System.IO.File.OpenRead(screenshots[0]);
                    await bot.SendPhoto(chatId, new InputFileStream(stream),
                        caption: header,
                        parseMode: ParseMode.Html);
                    return;
                }

                // Если скриншотов много
                header += "\n📸 Скриншоты ответа:";
                await bot.SendMessage(chatId, header, parseMode: ParseMode.Html,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true });

                var mediaGroup = new List<IAlbumInputMedia>();
                var streamsToDispose = new List<Stream>(); // <--- ИСПРАВЛЕНИЕ (Утечка потоков)

                try
                {
                    foreach (var imgPath in screenshots)
                    {
                        if (System.IO.File.Exists(imgPath))
                        {
                            var stream = new MemoryStream(await System.IO.File.ReadAllBytesAsync(imgPath)); // Убираем using
                            streamsToDispose.Add(stream); // Добавляем в список

                            var inputFile = new InputFileStream(stream, Path.GetFileName(imgPath));
                            mediaGroup.Add(new InputMediaPhoto(inputFile));

                            if (mediaGroup.Count == 10) // Ограничение Telegram
                            {
                                await bot.SendMediaGroup(chatId, mediaGroup);
                                mediaGroup.Clear();
                                // Закрываем и очищаем отправленные
                                foreach (var s in streamsToDispose) await s.DisposeAsync();
                                streamsToDispose.Clear();
                            }
                        }
                    }
                    if (mediaGroup.Count > 0)
                    {
                        await bot.SendMediaGroup(chatId, mediaGroup);
                    }
                }
                finally
                {
                    // Закрываем все оставшиеся потоки
                    foreach (var s in streamsToDispose) await s.DisposeAsync();
                }
            }
            else
            {
                header += "\n\n❌ Скриншоты для этого задания не найдены (ошибка).";
                await bot.SendMessage(chatId, header, parseMode: ParseMode.Html,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true });
            }
        }

        // Парсер ссылок
        private static (string? lessonNum, string? taskNum) ParseFoxfordLink(string text)
        {
            var lessonMatch = Regex.Match(text, @"lessons/(\d+)");
            var taskMatch = Regex.Match(text, @"tasks/(\d+)");
            var trainingMatch = Regex.Match(text, @"trainings/(\d+)/tasks/(\d+)");

            if (trainingMatch.Success)
            {
                return (trainingMatch.Groups[1].Value, trainingMatch.Groups[2].Value);
            }
            if (lessonMatch.Success && taskMatch.Success)
            {
                // Улучшенный парсер для /lessons/X/tasks/Y
                var fullMatch = Regex.Match(text, @"lessons/(\d+)/tasks/(\d+)");
                if (fullMatch.Success)
                {
                    return (fullMatch.Groups[1].Value, fullMatch.Groups[2].Value);
                }
                return (lessonMatch.Groups[1].Value, taskMatch.Groups[1].Value);
            }
            return (null, null);
        }

        public static async Task ShowAdminPanel(ITelegramBotClient bot, long chatId)
        {
            var stats = DatabaseHelper.GetStatistics();

            string statsText = $"⚙️ <b>Админ-панель</b>\n\n" +
                              $"👥 Пользователей: {stats.TotalUsers}\n" +
                              $"📝 Заданий в базе: {stats.TotalTasks}\n" +
                              $"📬 На модерации: <b>{stats.PendingTasks}</b>\n" +
                              $"🕐 Последнее добавление: {stats.LastTaskAdded?.ToString("dd.MM.yyyy HH:mm") ?? "Нет данных"}\n" +
                              $"🔍 Последний запрос: {stats.LastTaskRequested?.ToString("dd.MM.yyyy HH:mm") ?? "Нет данных"}\n" +
                              $"🔢 Версия бота: {Program.CODE_VERSION}\n";

            var keyboard = new InlineKeyboardMarkup(new[]
                        {
                new[] { InlineKeyboardButton.WithCallbackData($"📬 Модерация ({stats.PendingTasks})", "admin_moderate") },
                new[] { InlineKeyboardButton.WithCallbackData("➕ Добавить задание", "admin_add") },
                new[] { InlineKeyboardButton.WithCallbackData("🗑 Удалить задание", "admin_delete") },
                new[] { InlineKeyboardButton.WithCallbackData("💾 Экспорт JSON", "admin_export") },
                new[] { InlineKeyboardButton.WithCallbackData("📥 Импорт JSON", "admin_import") },
                new[] { InlineKeyboardButton.WithCallbackData("💾 Экспорт БД (.db)", "admin_get_db") },
                new[] { InlineKeyboardButton.WithCallbackData("📤 Заменить БД (.db)", "admin_replace_db") },
                new[] { InlineKeyboardButton.WithCallbackData("🗂 Скачать ZIP (скрины)", "admin_get_zip") },
                new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back_main") }
            });

            await bot.SendMessage(chatId, statsText, parseMode: ParseMode.Html, replyMarkup: keyboard);
        }

        // --- НОВЫЙ МЕТОД: Обработка загрузки БД (ИСПРАВЛЕН) ---
        private static async Task HandleDbUpload(ITelegramBotClient bot, long chatId, Document document)
        {
            // Дополнительная проверка безопасности (на всякий случай)
            if (string.IsNullOrEmpty(Program.BOT_TOKEN))
            {
                await bot.SendMessage(chatId, "❌ Ошибка конфигурации: Токен не найден. Загрузка отменена.");
                return;
            }

            try
            {
                var file = await bot.GetFile(document.FileId);
                string newDbPath = "foxford_answers.db.incoming"; // Сохраняем рядом с ботом

                using (var stream = File.OpenWrite(newDbPath))
                {
                    await bot.DownloadFile(file.FilePath!, stream);
                }

                // --- ИСПРАВЛЕНИЕ: ParseMode.Html и тег <pre> ---
                string warningMessage = $@"✅ Файл `.db` получен и сохранен как <code>{newDbPath}</code>.

⚠️ <b>ДЕЙСТВИЯ ВРУЧНУЮ:</b>
Я не могу применить его автоматически, пока я запущен (файл БД заблокирован).

<b>Чтобы применить новую БД, подключись к серверу по SSH и выполни:</b>
(Предполагается, что бот в папке /var/www/gdz-bot)

<pre>
# 1. Остановить бота
sudo systemctl stop gdz-bot

# 2. Заменить старую БД новой (сделай бэкап, если нужно!)
mv /var/www/gdz-bot/foxford_answers.db.incoming /var/www/gdz-bot/foxford_answers.db

# 3. Вернуть права (если нужно)
sudo chown www-data:www-data /var/www/gdz-bot/foxford_answers.db

# 4. Запустить бота
sudo systemctl start gdz-bot
</pre>";

                await bot.SendMessage(chatId, warningMessage, ParseMode.Html); // <-- ИЗМЕНЕНО
            }
            catch (Exception ex)
            {
                await bot.SendMessage(chatId, $"❌ Ошибка загрузки файла: {ex.Message}");
            }
            await ShowAdminPanel(bot, chatId);
        }

        #region Логика состояний (Админ)

        private static async Task HandleAdminInput(ITelegramBotClient bot, long chatId, string text, int messageId)
        {
            var state = adminStates[chatId];
            FoxfordTask task = state.Task;

            // Логика пакетного режима
            if (state.IsBatchMode)
            {
                switch (state.Step)
                {
                    case 1: // Шаг 1: Ссылка
                        var (lessonNum, taskNum) = ParseFoxfordLink(text);
                        if (lessonNum != null && taskNum != null)
                        {
                            task.LessonNumber = lessonNum;
                            task.TaskNumber = taskNum;

                            // Пакетный режим: Сразу спрашиваем TaskOrder (т.к. Урок/Полугодие уже заданы)
                            await AskTaskOrderOrScreenshots(bot, chatId, state, task.GroupType, true);
                        }
                        else
                        {
                            await bot.SendMessage(chatId, "❌ Неверный формат ссылки. Попробуй снова или /exit_batch");
                        }
                        break;

                    case 6: // Шаг 6: Порядковый номер урока ИЛИ Полугодие (в пакетном режиме)
                        if (int.TryParse(text, out int order))
                        {
                            if (task.GroupType == TaskGroupType.Demo)
                                task.Semester = order;
                            else
                                task.LessonOrder = order;

                            state.Step = 1; // Возвращаемся к шагу 1 (ссылка)
                            await bot.SendMessage(chatId,
                                $"✅ <b>Настройки пакетного режима сохранены:</b>\n" +
                                $"<b>{task.Grade} класс</b>, <b>{task.Subject}</b>\n" +
                                $"<b>{GetLevelTypeName(task.LevelType, true)}</b>, " +
                                $"<b>{GetGroupTypeName(task.GroupType)}</b>, " +
                                (task.GroupType == TaskGroupType.Demo ? $"Полугодие <b>{task.Semester}</b>" : $"Урок № <b>{task.LessonOrder}</b>") + "\n\n" +
                                "Теперь просто отправляй ссылки на задания. (/exit_batch для выхода)",
                                parseMode: ParseMode.Html);
                        }
                        else
                        {
                            await bot.SendMessage(chatId, "❌ Введи число. Попробуй снова:");
                        }
                        break;

                    // БЫВШИЙ case 8
                    case 7: // Шаг 7: Порядковый номер задания (если нужен)
                        if (int.TryParse(text, out int taskOrder))
                        {
                            task.TaskOrder = taskOrder;
                        }
                        state.Step = 9;
                        await AskForScreenshots(bot, chatId);
                        break;
                }
            }
            // Логика одиночного режима
            else
            {
                switch (state.Step)
                {
                    case 1: // Шаг 1: Ссылка
                        var (lessonNum, taskNum) = ParseFoxfordLink(text);
                        if (lessonNum != null && taskNum != null)
                        {
                            task.LessonNumber = lessonNum;
                            task.TaskNumber = taskNum;
                            state.Step = 2;
                            await CallbackHandler.AskGrade(bot, chatId, "admin_grade_");
                        }
                        else
                        {
                            await bot.SendMessage(chatId, "❌ Неверный формат ссылки. Попробуй снова или /cancel");
                        }
                        break;

                    case 6: // Шаг 6: Порядковый номер урока ИЛИ Полугодие
                        if (int.TryParse(text, out int order))
                        {
                            if (task.GroupType == TaskGroupType.Demo)
                                task.Semester = order;
                            else
                                task.LessonOrder = order;

                            await AskTaskOrderOrScreenshots(bot, chatId, state, task.GroupType, false);
                        }
                        else
                        {
                            await bot.SendMessage(chatId, "❌ Введи число. Попробуй снова:");
                        }
                        break;

                    // БЫВШШИЙ case 8
                    case 7: // Шаг 7: Порядковый номер задания
                        if (int.TryParse(text, out int taskOrder))
                        {
                            task.TaskOrder = taskOrder;
                        }

                        // Для КР/ПР дубликат не проверяем, т.к. это м.б. новый вариант
                        if (task.GroupType != TaskGroupType.ControlWork && task.GroupType != TaskGroupType.Test)
                        {
                            if (DatabaseHelper.CheckForDuplicate(task))
                            {
                                await bot.SendMessage(chatId, "❌ Такое задание (по ссылке или параметрам) уже есть в базе.\n\nДействие отменено. /cancel");
                                CancelSubmission(chatId);
                                return;
                            }
                        }

                        state.Step = 9;
                        await AskForScreenshots(bot, chatId);
                        break;
                }
            }
        }

        #endregion

        #region Логика состояний (Пользователь)

        private static async Task HandleUserInput(ITelegramBotClient bot, long chatId, string text, int messageId)
        {
            var state = userSubmissionStates[chatId];
            var task = state.Task;

            switch (state.Step)
            {
                case 1: // Шаг 1: Ссылка
                    var (lessonNum, taskNum) = ParseFoxfordLink(text);
                    if (lessonNum != null && taskNum != null)
                    {
                        task.LessonNumber = lessonNum;
                        task.TaskNumber = taskNum;
                        state.Step = 2;
                        await CallbackHandler.AskGrade(bot, chatId, "user_grade_");
                    }
                    else
                    {
                        await bot.SendMessage(chatId, "❌ Неверный формат ссылки. Попробуй снова или /cancel");
                    }
                    break;

                case 6: // Шаг 6: Порядковый номер урока ИЛИ Полугодие
                    if (int.TryParse(text, out int order))
                    {
                        if (task.GroupType == TaskGroupType.Demo)
                            task.Semester = order;
                        else
                            task.LessonOrder = order;

                        await AskTaskOrderOrScreenshots(bot, chatId, state, task.GroupType, false);
                    }
                    else
                    {
                        await bot.SendMessage(chatId, "❌ Введи число. Попробуй снова:");
                    }
                    break;

                // БЫВШИЙ case 8
                case 7: // Шаг 7: Порядковый номер задания
                    if (int.TryParse(text, out int taskOrder))
                    {
                        task.TaskOrder = taskOrder;
                    }

                    // --- ПРОВЕРКА НА ДУБЛИКАТ ---
                    // Для КР/ПР не проверяем, т.к. админ сам разберется (это м.б. новый вариант)
                    // Хотя для юзеров лучше проверять, но CheckForDuplicate() вернет false для КР/ПР.
                    if (DatabaseHelper.CheckForDuplicate(task))
                    {
                        await bot.SendMessage(chatId, "❌ Такое задание (по ссылке или параметрам) уже есть в базе или ожидает модерации.\n\nДействие отменено. /cancel");
                        CancelSubmission(chatId);
                        return;
                    }
                    // -----------------------------

                    state.Step = 9;
                    await AskForScreenshots(bot, chatId);
                    break;
            }
        }

        #endregion

        #region Общие шаги добавления (Вопросы)

        // ЗАМЕНА для AskTaskOrder
        private static async Task AskTaskOrderOrScreenshots(ITelegramBotClient bot, long chatId, SubmissionState state, TaskGroupType groupType, bool isBatchMode)
        {
            // Для ДЗ, КР, ПР, Теории, Демо - нужен номер задания
            if (groupType == TaskGroupType.Homework ||
                groupType == TaskGroupType.ControlWork ||
                groupType == TaskGroupType.Test ||
                groupType == TaskGroupType.Theory ||
                groupType == TaskGroupType.Demo)
            {
                SetStep(chatId, 7); // БЫЛО 8
                await bot.SendMessage(chatId, "🔢 Введи порядковый номер задания (1, 2, 3...):");
            }
            // Для остальных (если будут) - сразу скриншоты
            else
            {
                // Для пользователя - сначала проверка на дубликат (если TaskOrder не нужен)
                if (userSubmissionStates.ContainsKey(chatId))
                {
                    if (DatabaseHelper.CheckForDuplicate(state.Task))
                    {
                        await bot.SendMessage(chatId, "❌ Такое задание (по ссылке или параметрам) уже есть в базе или ожидает модерации.\n\nДействие отменено. /cancel");
                        CancelSubmission(chatId);
                        return;
                    }
                }

                SetStep(chatId, 9);
                await AskForScreenshots(bot, chatId);
            }
        }


        private static async Task AskForScreenshots(ITelegramBotClient bot, long chatId)
        {
            string cancelCommand = adminStates.ContainsKey(chatId) ? (adminStates[chatId].IsBatchMode ? "/exit_batch" : "/cancel") : "/cancel";

            await bot.SendMessage(chatId,
                "📸 <b>Отправь скриншоты ответа</b>\n\n" +
                "Отправляй фото по одному в нужном порядке\n\n" +
                "Команды:\n" +
                "• /done - закончить и сохранить\n" +
                "• /remove - удалить последний скриншот\n" +
                $"• {cancelCommand} - отменить всё",
                parseMode: ParseMode.Html);
        }


        #endregion

        #region Общие шаги добавления (Загрузка фото)

        private static async Task HandleImageUpload(ITelegramBotClient bot, long chatId, Message message)
        {
            var (state, _) = GetActiveSubmissionState(chatId);
            if (state == null)
            {
                return;
            }

            if (state.Step != 9)
            {
                await bot.SendMessage(chatId, "❓ Скриншоты сейчас не ожидаются. Завершите текущий шаг или используйте /cancel");
                return;
            }

            var photo = message.Photo!.Last();
            var file = await bot.GetFile(photo.FileId);

            if (file.FilePath == null)
            {
                await bot.SendMessage(chatId, "❌ Ошибка получения файла");
                return;
            }

            string folder = "task_images";
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            string fileName = Path.Combine(folder, $"screenshot_{Guid.NewGuid()}.jpg");

            try
            {
                using (var stream = File.OpenWrite(fileName))
                {
                    await bot.DownloadFile(file.FilePath!, stream);
                }

                state.ScreenshotPaths.Add(fileName);

                await bot.SendMessage(chatId,
                    $"✅ Скриншот {state.ScreenshotPaths.Count} сохранён\n\n" +
                    $"• Отправь ещё фото\n" +
                    $"• /done - закончить\n" +
                    $"• /remove - удалить последний");
            }
            catch (Exception ex)
            {
                await bot.SendMessage(chatId, $"❌ Ошибка сохранения: {ex.Message}");
            }
        }

        private static async Task RemoveLastScreenshot(ITelegramBotClient bot, long chatId)
        {
            var (state, _) = GetActiveSubmissionState(chatId);
            if (state == null)
            {
                await bot.SendMessage(chatId, "❌ Нет активного добавления");
                return;
            }

            if (state.ScreenshotPaths.Count == 0)
            {
                await bot.SendMessage(chatId, "❌ Нет скриншотов для удаления");
                return;
            }

            var lastPath = state.ScreenshotPaths[state.ScreenshotPaths.Count - 1];

            if (File.Exists(lastPath))
            {
                try { File.Delete(lastPath); } catch { }
            }

            state.ScreenshotPaths.RemoveAt(state.ScreenshotPaths.Count - 1);

            await bot.SendMessage(chatId,
                $"🗑 Последний скриншот удалён\n\n" +
                $"Осталось скриншотов: {state.ScreenshotPaths.Count}");
        }

        private static async Task FinishScreenshotUpload(ITelegramBotClient bot, long chatId, int messageId)
        {
            var (state, _) = GetActiveSubmissionState(chatId);
            if (state == null)
            {
                await bot.SendMessage(chatId, "❌ Нет активного добавления");
                return;
            }

            if (state.Step != 9)
            {
                await bot.SendMessage(chatId, "❌ Сейчас не ожидается загрузка скриншотов");
                return;
            }

            if (state.ScreenshotPaths.Count == 0)
            {
                await bot.SendMessage(chatId,
                    "❌ <b>Нужен хотя бы один скриншот!</b>\n\n" +
                    "📸 Отправь фото или /cancel для отмены",
                    parseMode: ParseMode.Html);
                return;
            }

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("✅ Сохранить", "save_task") },
                new[] { InlineKeyboardButton.WithCallbackData("❌ Отмена", "cancel_task") }
            });

            await bot.SendMessage(chatId,
                $"✅ <b>Загружено скриншотов: {state.ScreenshotPaths.Count}</b>\n\n" +
                "Сохранить задание?",
                parseMode: ParseMode.Html,
                replyMarkup: keyboard);
        }

        #endregion

        #region Общие шаги добавления (Сохранение)

        private static async Task SaveTask(ITelegramBotClient bot, long chatId, long adminId, User from)
        {
            // 1. Определяем, кто сохраняет: Админ или Пользователь
            if (adminStates.ContainsKey(chatId))
            {
                var state = adminStates[chatId];
                state.Task.ScreenshotPaths = string.Join(",", state.ScreenshotPaths);
                state.Task.IsModerated = true; // Админские сразу одобрены
                state.Task.SubmittedByUserId = chatId;

                try
                {
                    DatabaseHelper.AddTask(state.Task); // AddTask теперь сам считает Variant

                    // Пакетный режим: не выходим, а сбрасываем для следующего задания
                    if (state.IsBatchMode)
                    {
                        await bot.SendMessage(chatId, $"✅ Задание {state.Task.LessonNumber}/{state.Task.TaskNumber} сохранено (Вариант: {state.Task.Variant ?? 1}).");

                        // Сбрасываем только "разовые" поля. Класс, предмет и т.д. остаются
                        var preservedTask = new FoxfordTask
                        {
                            Grade = state.Task.Grade,
                            Subject = state.Task.Subject,
                            // --- ИЗМЕНЕНО ---
                            LevelType = state.Task.LevelType,
                            GroupType = state.Task.GroupType,
                            LessonOrder = state.Task.LessonOrder,
                            Semester = state.Task.Semester
                        };

                        state.Task = preservedTask;
                        state.ScreenshotPaths.Clear();
                        state.Step = 1; // Возвращаемся к шагу "Отправь ссылку"

                        await bot.SendMessage(chatId,
                            "⚡️ <b>Пакетный режим</b>\n" +
                            /* --- ИЗМЕНЕНО --- */ $"<b>Параметры:</b> {state.Task.Grade} кл, {state.Task.Subject}, {GetLevelTypeName(state.Task.LevelType, true)}, {GetGroupTypeName(state.Task.GroupType)}, " +
                            (state.Task.GroupType == TaskGroupType.Demo ? $"Полугодие {state.Task.Semester}" : $"Урок №{state.Task.LessonOrder}") + "\n\n" +
                            "Отправь следующую ссылку или /exit_batch для выхода.",
                            parseMode: ParseMode.Html);
                    }
                    // Одиночный режим: выходим
                    else
                    {
                        adminStates.Remove(chatId);
                        await bot.SendMessage(chatId, $"✅ Задание успешно добавлено! (Вариант: {state.Task.Variant ?? 1})");
                        await ShowAdminPanel(bot, chatId);
                    }
                }
                catch (Exception ex)
                {
                    await bot.SendMessage(chatId, $"❌ Ошибка сохранения: {ex.Message}");
                }
            }
            else if (userSubmissionStates.ContainsKey(chatId))
            {
                var state = userSubmissionStates[chatId];
                state.Task.ScreenshotPaths = string.Join(",", state.ScreenshotPaths);
                state.Task.IsModerated = false; // Пользовательские уходят на модерацию
                state.Task.SubmittedByUserId = chatId;

                try
                {
                    DatabaseHelper.AddTask(state.Task);
                    userSubmissionStates.Remove(chatId);

                    await bot.SendMessage(chatId, "✅ Спасибо! Задание отправлено на модерацию.");

                    // Уведомление админу
                    string taskDesc = state.Task.GroupType == TaskGroupType.Demo
                        ? $"Урок {state.Task.Semester} (Демо)"
                        : $"Урок {state.Task.LessonOrder}";

                    await bot.SendMessage(adminId, $"📬 <b>Новое задание на модерацию!</b>\n" +
                                                   $"От пользователя: {from.FirstName} (@{from.Username})\n" +
                                                   $"Задание: {state.Task.Grade} кл, {state.Task.Subject}, {taskDesc}\n\n" +
                                                   "Нажми /admin, чтобы проверить.", parseMode: ParseMode.Html);
                }
                catch (Exception ex)
                {
                    await bot.SendMessage(chatId, $"❌ Ошибка сохранения: {ex.Message}");
                }
            }
            else
            {
                await bot.SendMessage(chatId, "❌ Ошибка: не найдено активного состояния добавления.");
            }
        }

        #endregion

        #region Импорт JSON

        private static async Task HandleJsonImport(ITelegramBotClient bot, long chatId, Document document)
        {
            try
            {
                var file = await bot.GetFile(document.FileId);
                using var stream = new MemoryStream();
                await bot.DownloadFile(file.FilePath!, stream);
                stream.Position = 0;

                using var reader = new StreamReader(stream);
                string json = await reader.ReadToEndAsync();

                int count = DatabaseHelper.ImportFromJson(json);
                await bot.SendMessage(chatId, $"✅ Импортировано новых заданий: {count}");
            }
            catch (Exception ex)
            {
                await bot.SendMessage(chatId, $"❌ Ошибка импорта: {ex.Message}");
            }
        }

        #endregion

        #region Вспомогательные методы (управление состоянием)

        // Публичные методы, вызываемые из CallbackHandler

        public static void StartAddingTask(long chatId, bool isBatchMode)
        {
            var state = new AdminState { IsBatchMode = isBatchMode };
            if (isBatchMode)
            {
                state.Step = 2; // Начинаем с выбора класса
            }
            else
            {
                state.Step = 1; // Начинаем с ссылки
            }
            adminStates[chatId] = state;
        }

        public static void StartUserSubmission(long chatId)
        {
            userSubmissionStates[chatId] = new UserSubmissionState { Step = 1 };
        }

        public static void SetGrade(long chatId, int grade)
        {
            var (state, _) = GetActiveSubmissionState(chatId);
            if (state != null)
            {
                state.Task.Grade = grade;
                state.Step = 3; // Переход к выбору предмета
            }
        }

        public static void SetSubject(long chatId, string subject)
        {
            var (state, _) = GetActiveSubmissionState(chatId);
            if (state != null)
            {
                state.Task.Subject = subject;
                state.Step = 4; // Переход к выбору уровня
            }
        }

        // --- НОВЫЙ МЕТОД ---
        public static void SetLevelType(long chatId, TaskLevelType level)
        {
            var (state, _) = GetActiveSubmissionState(chatId);
            if (state != null)
            {
                state.Task.LevelType = level;
                state.Step = 5; // Переход к выбору типа группы
            }
        }

        public static void SetGroupType(long chatId, TaskGroupType type)
        {
            var (state, _) = GetActiveSubmissionState(chatId);
            if (state != null)
            {
                state.Task.GroupType = type;
                state.Step = 6; // Переход к вводу номера урока
            }
        }

        public static TaskLevelType? GetCurrentLevelType(long chatId)
        {
            var (state, _) = GetActiveSubmissionState(chatId);
            if (state != null) return state.Task.LevelType;
            return null;
        }


        // Вызов сохранения из CallbackHandler
        public static async Task SaveTaskFromCallback(ITelegramBotClient bot, long chatId, long adminId, User from)
        {
            await SaveTask(bot, chatId, adminId, from);
        }

        // Универсальная отмена
        public static void CancelSubmission(long chatId)
        {
            var (state, _) = GetActiveSubmissionState(chatId);
            if (state != null)
            {
                // Удаляем временные файлы
                foreach (var path in state.ScreenshotPaths)
                {
                    if (File.Exists(path))
                    {
                        try { File.Delete(path); } catch { }
                    }
                }
            }
            adminStates.Remove(chatId);
            userSubmissionStates.Remove(chatId);
        }

        // Поиск состояния
        private static (SubmissionState? state, bool isAdmin) GetActiveSubmissionState(long chatId)
        {
            if (adminStates.ContainsKey(chatId))
                return (adminStates[chatId], true);
            if (userSubmissionStates.ContainsKey(chatId))
                return (userSubmissionStates[chatId], false);
            return (null, false);
        }

        // Установка шага
        private static void SetStep(long chatId, int step)
        {
            var (state, _) = GetActiveSubmissionState(chatId);
            if (state != null) state.Step = step;
        }

        #region Отправка Галереи Урока

        public static async Task SendFullLessonGallery(ITelegramBotClient bot, long chatId, List<FoxfordTask> tasks)
        {
            if (tasks.Count == 0) return;

            var firstTask = tasks[0];

            // 1. Отправляем общий заголовок
            string header = $"✅ <b>Все задания Урока №{firstTask.LessonOrder}</b>\n\n" +
                           $"📚 {firstTask.Grade} класс | {firstTask.Subject}" + GetLevelTypeName(firstTask.LevelType) + "\n" +
                           $"📖 {GetGroupTypeName(firstTask.GroupType)}";

            await bot.SendMessage(chatId, header, parseMode: ParseMode.Html,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true });

            // 2. Перебираем задания и отправляем их
            foreach (var task in tasks)
            {
                // Формируем заголовок для конкретного задания
                string taskHeader = $"--- <b>Задание №{task.TaskOrder ?? task.Id}</b> ---";

                if (string.IsNullOrEmpty(task.ScreenshotPaths))
                {
                    await bot.SendMessage(chatId, $"{taskHeader}\n\n❌ Скриншоты для этого задания не найдены.", parseMode: ParseMode.Html);
                    continue; // Пропускаем это задание
                }

                var screenshots = task.ScreenshotPaths.Split(',');
                var validScreenshots = screenshots
                    .Select(p => p.Replace('\\', Path.DirectorySeparatorChar))
                    .Where(System.IO.File.Exists).ToList();

                if (validScreenshots.Count == 0)
                {
                    await bot.SendMessage(chatId, $"{taskHeader}\n\n❌ Файлы скриншотов не найдены.", parseMode: ParseMode.Html);
                    continue;
                }

                // --- Логика отправки ---

                // Если 1 скриншот, отправляем с подписью
                if (validScreenshots.Count == 1)
                {
                    using var stream = System.IO.File.OpenRead(validScreenshots[0]);
                    await bot.SendPhoto(chatId, new InputFileStream(stream),
                        caption: taskHeader, // Помещаем "Задание №1" в подпись
                        parseMode: ParseMode.Html);
                }
                // Если 2-10 скриншотов
                else if (validScreenshots.Count > 1)
                {
                    // Сначала отправляем заголовок задания текстом
                    await bot.SendMessage(chatId, taskHeader, parseMode: ParseMode.Html);

                    var mediaGroup = new List<IAlbumInputMedia>();
                    var streamsToDispose = new List<Stream>();

                    try
                    {
                        foreach (var imgPath in validScreenshots)
                        {
                            var stream = new MemoryStream(await System.IO.File.ReadAllBytesAsync(imgPath));
                            streamsToDispose.Add(stream);
                            var inputFile = new InputFileStream(stream, Path.GetFileName(imgPath));

                            mediaGroup.Add(new InputMediaPhoto(inputFile));

                            // Если набрали 10, отправляем
                            if (mediaGroup.Count == 10)
                            {
                                await bot.SendMediaGroup(chatId, mediaGroup);
                                mediaGroup.Clear();
                                foreach (var s in streamsToDispose) await s.DisposeAsync();
                                streamsToDispose.Clear();
                            }
                        }
                        // Отправляем остаток (если есть)
                        if (mediaGroup.Count > 0)
                        {
                            await bot.SendMediaGroup(chatId, mediaGroup);
                        }
                    }
                    finally
                    {
                        // Гарантированно закрываем все потоки
                        foreach (var s in streamsToDispose) await s.DisposeAsync();
                    }
                }
            }
        }

        #endregion

        // Поиск
        public static void SetUserSearchState(long chatId, UserSearchState state) => userStates[chatId] = state;
        public static UserSearchState? GetUserSearchState(long chatId) => userStates.ContainsKey(chatId) ? userStates[chatId] : null;
        public static void ClearUserSearchState(long chatId) => userStates.Remove(chatId);

        // Вспомогательное
        public static string GetGroupTypeName(TaskGroupType type)
        {
            return type switch
            {
                TaskGroupType.Homework => "Домашняя работа",
                TaskGroupType.Test => "Проверочная",
                TaskGroupType.Demo => "Демоверсия",
                TaskGroupType.ControlWork => "Контрольная",
                TaskGroupType.Theory => "Задачи по теории",
                _ => "Неизвестно"
            };
        }

        // --- НОВЫЙ МЕТОД ---
        public static string GetLevelTypeName(TaskLevelType level, bool forBatch = false)
        {
            // forBatch нужен для пакетного режима, чтобы "Обычный" не был пустым
            if (forBatch)
            {
                return level switch
                {
                    TaskLevelType.Regular => "Обычный",
                    TaskLevelType.Profile => "Профильный",
                    TaskLevelType.Inverted => "Перевернутый",
                    _ => ""
                };
            }

            // Для обычного отображения
            return level switch
            {
                TaskLevelType.Regular => "", // Не пишем "(Обычный)", т.к. это по умолч.
                TaskLevelType.Profile => " (Профиль)",
                TaskLevelType.Inverted => " (Перевернутый)",
                _ => ""
            };
        }

        #endregion
    }
}