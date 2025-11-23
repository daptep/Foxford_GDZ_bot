using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Ionic.Zip;

namespace FoxfordAnswersBot
{
    public static class CallbackHandler
    {
        public static async Task HandleCallback(ITelegramBotClient bot, CallbackQuery callback, long adminId)
        {
            var chatId = callback.Message!.Chat.Id;
            var data = callback.Data!;
            var messageId = callback.Message.MessageId;

            // КРИТИЧНО: Отвечаем НЕМЕДЛЕННО, не дожидаясь обработки
            _ = bot.AnswerCallbackQuery(callback.Id);

            try
            {
                // --- АДМИН-ПАНЕЛЬ ---
                if (data == "admin_panel" && chatId == adminId)
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

                    await EditMessageTextSafe(bot, chatId, messageId, statsText, keyboard, ParseMode.Html);
                    return;
                }

                // --- Модерация (Админ) ---
                if (data == "admin_moderate" && chatId == adminId)
                {
                    await ShowNextModerationTask(bot, chatId, messageId);
                    return;
                }

                if (data.StartsWith("approve_") && chatId == adminId)
                {
                    int taskId = int.Parse(data.Replace("approve_", ""));
                    var task = DatabaseHelper.GetTaskById(taskId);
                    if (task == null)
                    {
                        await EditMessageTextSafe(bot, chatId, messageId, "❌ Ошибка: Задание уже обработано.");
                        return;
                    }

                    DatabaseHelper.ApproveTask(taskId);

                    // Уведомляем пользователя асинхронно
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            string taskDesc = task.GroupType == TaskGroupType.Demo
                                ? $"Демо, Полугодие {task.Semester}"
                                : $"{task.Subject}, Урок {task.LessonOrder}";
                            await bot.SendMessage(task.SubmittedByUserId, $"🎉 Ваше задание «{taskDesc}» одобрено!");
                        }
                        catch { }
                    });

                    await ShowNextModerationTask(bot, chatId, messageId);
                    return;
                }

                if (data.StartsWith("decline_") && chatId == adminId)
                {
                    int taskId = int.Parse(data.Replace("decline_", ""));
                    var task = DatabaseHelper.GetTaskById(taskId);
                    if (task == null)
                    {
                        await EditMessageTextSafe(bot, chatId, messageId, "❌ Ошибка: Задание уже обработано.");
                        return;
                    }

                    DatabaseHelper.DeclineTask(taskId);

                    // Уведомляем пользователя асинхронно
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            string taskDesc = task.GroupType == TaskGroupType.Demo
                                ? $"Демо, Полугодие {task.Semester}"
                                : $"{task.Subject}, Урок {task.LessonOrder}";
                            await bot.SendMessage(task.SubmittedByUserId, $"😔 К сожалению, ваше задание «{taskDesc}» было отклонено модератором.");
                        }
                        catch { }
                    });

                    await ShowNextModerationTask(bot, chatId, messageId);
                    return;
                }

                // --- Добавление задания (Админ) ---
                if (data == "admin_add" && chatId == adminId)
                {
                    var keyboard = new InlineKeyboardMarkup(new[]
                    {
                        new[] { InlineKeyboardButton.WithCallbackData("➕ Одиночное добавление", "admin_add_single") },
                        new[] { InlineKeyboardButton.WithCallbackData("⚡️ Пакетное добавление", "admin_add_batch") },
                        new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "admin_panel") }
                    });
                    await EditMessageTextSafe(bot, chatId, messageId, "⚙️ Выбери режим добавления:", keyboard);
                    return;
                }

                if (data == "admin_add_single" && chatId == adminId)
                {
                    MessageHandler.StartAddingTask(chatId, isBatchMode: false);
                    await EditMessageTextSafe(bot, chatId, messageId,
                        "➕ <b>Одиночное добавление</b>\n\nОтправь ссылку на задание:\n\nПример:\nhttps://foxford.ru/lessons/475003/tasks/301386\n\n(/cancel для отмены)",
                        parseMode: ParseMode.Html);
                    return;
                }

                if (data == "admin_add_batch" && chatId == adminId)
                {
                    MessageHandler.StartAddingTask(chatId, isBatchMode: true);
                    await bot.DeleteMessage(chatId, messageId);
                    await bot.SendMessage(chatId,
                        "⚡️ <b>Пакетное добавление</b>\n\n" +
                        "Сначала настроим \"прилипающие\" параметры для всей сессии.",
                        parseMode: ParseMode.Html);
                    await AskGrade(bot, chatId, "admin_grade_");
                    return;
                }

                // --- Шаги добавления (Админ) ---
                if (data.StartsWith("admin_grade_") && chatId == adminId)
                {
                    int grade = int.Parse(data.Replace("admin_grade_", ""));
                    MessageHandler.SetGrade(chatId, grade);
                    await AskSubject(bot, chatId, messageId, "admin_subj_");
                    return;
                }
                if (data.StartsWith("admin_subj_") && chatId == adminId)
                {
                    string subject = data.Replace("admin_subj_", "");
                    MessageHandler.SetSubject(chatId, subject);
                    await AskLevelType(bot, chatId, messageId, "admin_level_");
                    return;
                }

                if (data.StartsWith("admin_level_") && chatId == adminId)
                {
                    var level = (TaskLevelType)int.Parse(data.Replace("admin_level_", ""));
                    MessageHandler.SetLevelType(chatId, level);
                    await AskGroupType(bot, chatId, messageId, "admin_group_");
                    return;
                }

                if (data.StartsWith("admin_group_") && chatId == adminId)
                {
                    var type = (TaskGroupType)int.Parse(data.Replace("admin_group_", ""));
                    MessageHandler.SetGroupType(chatId, type);

                    if (type == TaskGroupType.Demo || type == TaskGroupType.ControlWork)
                    {
                        await EditMessageTextSafe(bot, chatId, messageId, "🔢 Введи полугодие (1 или 2):");
                    }
                    else
                    {
                        await EditMessageTextSafe(bot, chatId, messageId, "🔢 Введи порядковый номер урока (для всей сессии):");
                    }
                    return;
                }

                // --- Добавление задания (Пользователь) ---
                if (data == "user_add_start")
                {
                    MessageHandler.StartUserSubmission(chatId);
                    await EditMessageTextSafe(bot, chatId, messageId,
                        "📥 <b>Предложить задание</b>\n\nОтправь ссылку на задание:\n\nПример:\nhttps://foxford.ru/lessons/475003/tasks/301386\n\n(/cancel для отмены)",
                        parseMode: ParseMode.Html);
                    return;
                }

                // --- Шаги добавления (Пользователь) ---
                if (data.StartsWith("user_grade_"))
                {
                    int grade = int.Parse(data.Replace("user_grade_", ""));
                    MessageHandler.SetGrade(chatId, grade);
                    await AskSubject(bot, chatId, messageId, "user_subj_");
                    return;
                }
                if (data.StartsWith("user_subj_"))
                {
                    string subject = data.Replace("user_subj_", "");
                    MessageHandler.SetSubject(chatId, subject);
                    await AskLevelType(bot, chatId, messageId, "user_level_");
                    return;
                }

                if (data.StartsWith("user_level_"))
                {
                    var level = (TaskLevelType)int.Parse(data.Replace("user_level_", ""));
                    MessageHandler.SetLevelType(chatId, level);
                    await AskGroupType(bot, chatId, messageId, "user_group_");
                    return;
                }

                if (data.StartsWith("user_group_"))
                {
                    var type = (TaskGroupType)int.Parse(data.Replace("user_group_", ""));
                    MessageHandler.SetGroupType(chatId, type);

                    if (type == TaskGroupType.Demo || type == TaskGroupType.ControlWork)
                    {
                        await EditMessageTextSafe(bot, chatId, messageId, "🔢 Введи полугодие (1 или 2):");
                    }
                    else
                    {
                        await EditMessageTextSafe(bot, chatId, messageId, "🔢 Введи порядковый номер урока:");
                    }
                    return;
                }

                // --- Общие колбэки для добавления ---
                if (data == "cancel_task")
                {
                    MessageHandler.CancelSubmission(chatId);
                    await EditMessageTextSafe(bot, chatId, messageId, "❌ Добавление задания отменено, все скриншоты удалены");
                    return;
                }

                if (data == "save_task")
                {
                    await bot.DeleteMessage(chatId, messageId);
                    await MessageHandler.SaveTaskFromCallback(bot, chatId, adminId, callback.From);
                    return;
                }

                // --- Экспорт / Импорт (Админ) ---
                if (data == "admin_export" && chatId == adminId)
                {
                    try
                    {
                        string json = DatabaseHelper.ExportToJson();
                        string fileName = $"foxford_backup_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                        await System.IO.File.WriteAllTextAsync(fileName, json);

                        using var stream = System.IO.File.OpenRead(fileName);
                        await bot.SendDocument(chatId, new InputFileStream(stream, fileName));
                        System.IO.File.Delete(fileName);
                        await bot.SendMessage(chatId, "✅ Экспорт завершён!");
                    }
                    catch (Exception ex)
                    {
                        await bot.SendMessage(chatId, $"❌ Ошибка: {ex.Message}");
                    }
                    return;
                }
                if (data == "admin_import" && chatId == adminId)
                {
                    await EditMessageTextSafe(bot, chatId, messageId, "📥 Отправь JSON-файл для импорта");
                    return;
                }
                if (data == "admin_delete" && chatId == adminId)
                {
                    // Устанавливаем состояние ожидания ID
                    MessageHandler.adminActionStates[chatId] = "awaiting_delete_id";
                    await EditMessageTextSafe(bot, chatId, messageId,
                        "🗑 <b>Удаление задания</b>\n\n" +
                        "Отправь ID задания, которое нужно удалить.\n\n" +
                        "(ID можно увидеть при поиске задания)",
                        parseMode: ParseMode.Html,
                        keyboard: new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("◀️ Отмена", "admin_panel")));
                    return;
                }

                if (data == "admin_get_db" && chatId == adminId)
                {
                    if (!System.IO.File.Exists(DatabaseHelper.DB_PATH))
                    {
                        await bot.SendMessage(chatId, "❌ Файл базы данных не найден.");
                        return;
                    }
                    try
                    {
                        using var stream = System.IO.File.OpenRead(DatabaseHelper.DB_PATH);
                        await bot.SendDocument(chatId, new InputFileStream(stream, DatabaseHelper.DB_PATH));
                        await bot.SendMessage(chatId, "✅ Файл базы данных отправлен.");
                    }
                    catch (Exception ex)
                    {
                        await bot.SendMessage(chatId, $"❌ Ошибка: {ex.Message}");
                    }
                    return;
                }
                if (data == "admin_replace_db" && chatId == adminId)
                {
                    // Устанавливаем состояние ожидания подтверждения
                    MessageHandler.adminActionStates[chatId] = "awaiting_db_replace_confirm_text";
                    await EditMessageTextSafe(bot, chatId, messageId,
                        "⚠️ <b>ЗАМЕНА БАЗЫ ДАННЫХ</b> ⚠️\n\n" +
                        "Это опасное действие, которое приведет к полной замене всех данных.\n\n" +
                        "Для подтверждения, пожалуйста, отправь в чат слово `foxford`",
                        parseMode: ParseMode.Html,
                        keyboard: new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("◀️ Отмена", "admin_panel")));
                    return;
                }

                if (data == "admin_get_zip" && chatId == adminId)
                {
                    string imagesPath = DatabaseHelper.IMAGES_FOLDER;

                    // Временный базовый путь для архива
                    string archiveBaseName = $"task_images_backup_{DateTime.Now:yyyyMMdd_HHmmss}";
                    string tempDir = Path.GetTempPath(); // Используем временную папку ОС
                    string archivePath = Path.Combine(tempDir, archiveBaseName + ".zip");

                    List<string> createdZipFiles = new List<string>();

                    try
                    {
                        if (!Directory.Exists(imagesPath) || !Directory.EnumerateFiles(imagesPath).Any())
                        {
                            await bot.SendMessage(chatId, $"❌ Папка `{imagesPath}` пуста или не найдена.");
                            return;
                        }

                        await bot.SendMessage(chatId, "⏳ Начинаю архивацию... Это может занять несколько минут, если скриншотов много.");

                        // 1. Создаем архив в отдельном потоке
                        await Task.Run(() =>
                        {
                            using (Ionic.Zip.ZipFile zip = new Ionic.Zip.ZipFile())
                            {
                                // Используем Zip64 для поддержки больших архивов
                                zip.UseZip64WhenSaving = Zip64Option.AsNecessary;
                                // Добавляем папку task_images ВНУТРЬ архива
                                zip.AddDirectory(imagesPath, "task_images");

                                // --- ГЛАВНОЕ: Устанавливаем лимит 45 МБ ---
                                // (45 * 1024 * 1024 = 47,185,920 байт)
                                zip.MaxOutputSegmentSize = 45 * 1024 * 1024;

                                zip.Save(archivePath); // Библиотека сама создаст .z01, .z02 и т.д.
                            }
                        });

                        // 2. Находим все созданные части
                        // DotNetZip создает .zip, .z01, .z02...
                        createdZipFiles = Directory.EnumerateFiles(tempDir, $"{archiveBaseName}.*")
                                                    .OrderBy(f => f) // Сортируем, чтобы .zip был первым
                                                    .ToList();

                        if (createdZipFiles.Count == 0)
                        {
                            throw new Exception("Архив не был создан.");
                        }

                        await bot.SendMessage(chatId, $"✅ Готово! Отправляю {createdZipFiles.Count} частей архива...");

                        // 3. Отправляем все части по очереди
                        foreach (var file in createdZipFiles)
                        {
                            await using (var stream = System.IO.File.OpenRead(file))
                            {
                                // Отправляем файл
                                await bot.SendDocument(chatId, new InputFileStream(stream, Path.GetFileName(file)));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        await bot.SendMessage(chatId, $"❌ Ошибка при создании архива: {ex.Message}");
                    }
                    finally
                    {
                        // 4. Гарантированно удаляем все временные ZIP-файлы
                        foreach (var file in createdZipFiles)
                        {
                            if (System.IO.File.Exists(file))
                            {
                                try { System.IO.File.Delete(file); } catch { }
                            }
                        }
                    }
                    return;
                }

                // --- ДОНАТ ---
                if (data.StartsWith("donat_start"))
                {
                    string statsText = @"Прогресс на 3D-принтер: [█░░░░░░░░] 5,3 / 45 тыс.

Автор бота копит на 3D - принтер, если хотите поддержать проект напишите: https://t.me/foxford_gdz_channel?direct

Список донатов:

11.11.2025 - 301 руб.
11.11.2025 - 100 руб.
";

                    var keyboard = new InlineKeyboardMarkup(new[]
                    {
                        new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back_main") }
                    });

                    await EditMessageTextSafe(bot, chatId, messageId, statsText, keyboard, ParseMode.Html);
                    return;
                }

                if (data == "search_start")
                {
                    var grades = DatabaseHelper.GetGrades();
                    if (grades.Count == 0)
                    {
                        await EditMessageTextSafe(bot, chatId, messageId, "😔 В базе пока нет заданий");
                        return;
                    }

                    var buttons = grades.Select(g => new[] {
                        InlineKeyboardButton.WithCallbackData($"{g} класс", $"search_grade_{g}")
                    }).ToList();
                    buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back_main") });

                    var keyboard = new InlineKeyboardMarkup(buttons);
                    await EditMessageTextSafe(bot, chatId, messageId, "📚 Выбери класс:", keyboard);
                    return;
                }

                if (data.StartsWith("search_grade_"))
                {
                    int grade = int.Parse(data.Replace("search_grade_", ""));
                    var state = MessageHandler.GetUserSearchState(chatId) ?? new UserSearchState();
                    if (state != null) state.Grade = grade;
                    else return;
                    MessageHandler.SetUserSearchState(chatId, state);

                    var subjects = DatabaseHelper.GetSubjects(grade);
                    var buttons = subjects.Select(s => new[] {
                        InlineKeyboardButton.WithCallbackData(s, $"search_subj_{s}")
                    }).ToList();
                    buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "search_start") });

                    var keyboard = new InlineKeyboardMarkup(buttons);
                    await EditMessageTextSafe(bot, chatId, messageId, "📖 Выбери предмет:", keyboard);
                    return;
                }

                if (data.StartsWith("search_subj_"))
                {
                    string subject = data.Replace("search_subj_", "");
                    var state = MessageHandler.GetUserSearchState(chatId)!;
                    if (state != null) state.Subject = subject;
                    else return;

                    var keyboard = new InlineKeyboardMarkup(new[]
                    {
                        new[] { InlineKeyboardButton.WithCallbackData("Обычный", "search_level_0") },
                        new[] { InlineKeyboardButton.WithCallbackData("Профильный", "search_level_1") },
                        new[] { InlineKeyboardButton.WithCallbackData("Перевернутый", "search_level_2") },
                        new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", $"search_grade_{state.Grade}") }
                    });

                    await EditMessageTextSafe(bot, chatId, messageId, "📚 Тип (уровень):", keyboard);
                    return;
                }

                if (data.StartsWith("search_level_"))
                {
                    var level = (TaskLevelType)int.Parse(data.Replace("search_level_", ""));
                    var state = MessageHandler.GetUserSearchState(chatId)!;
                    if (state != null) state.LevelType = level;
                    else return;

                    InlineKeyboardMarkup keyboard;

                    // Если выбран ПЕРЕВЕРНУТЫЙ класс -> показываем Теорию и ДЗ
                    if (level == TaskLevelType.Inverted)
                    {
                        keyboard = new InlineKeyboardMarkup(new[]
                        {
                            new[] { InlineKeyboardButton.WithCallbackData("📚 Задачи по теории", "search_group_4") }, // 4 = Theory
                            new[] { InlineKeyboardButton.WithCallbackData("📝 Домашняя работа", "search_group_0") }, // 0 = Homework
                            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", $"search_subj_{state.Subject}") }
                        });
                    }
                    // Для Обычного и Профильного -> стандартное меню
                    else
                    {
                        keyboard = new InlineKeyboardMarkup(new[]
                        {
                            new[] { InlineKeyboardButton.WithCallbackData("📝 Домашняя работа", "search_group_0") },
                            new[] { InlineKeyboardButton.WithCallbackData("📊 Проверочная", "search_group_1") },
                            new[] { InlineKeyboardButton.WithCallbackData("🎯 Демоверсия", "search_group_2") },
                            new[] { InlineKeyboardButton.WithCallbackData("📋 Контрольная", "search_group_3") },
                            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", $"search_subj_{state.Subject}") }
                        });
                    }

                    await EditMessageTextSafe(bot, chatId, messageId, "📚 Тип группы:", keyboard);
                    return;
                }

                if (data.StartsWith("search_group_"))
                {
                    var type = (TaskGroupType)int.Parse(data.Replace("search_group_", ""));
                    var state = MessageHandler.GetUserSearchState(chatId)!;
                    if (state != null) state.GroupType = type;
                    else return;

                    // Обнуляем следующие шаги
                    state.LessonOrder = null;
                    state.Semester = null;

                    var keyboardBack = new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", $"search_level_{(int)state.LevelType!.Value}") } });

                    // --- Логика для ДЕМО ---
                    if (type == TaskGroupType.Demo || type == TaskGroupType.ControlWork)
                    {
                        var semesters = DatabaseHelper.GetSemesters(state.Grade!.Value, state.Subject!, state.LevelType!.Value);
                        if (semesters.Count == 0)
                        {
                            await EditMessageTextSafe(bot, chatId, messageId, "😔 Заданий по этим параметрам не найдено", keyboardBack);
                            return;
                        }

                        var buttons = semesters.Select(s => new[] {
                            InlineKeyboardButton.WithCallbackData($"Полугодие {s}", $"search_semester_{s}")
                        }).ToList();
                        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", $"search_level_{(int)state.LevelType!.Value}") });

                        await EditMessageTextSafe(bot, chatId, messageId, "📖 Выбери полугодие:", new InlineKeyboardMarkup(buttons));
                    }
                    // --- Логика для Остальных (ДЗ, ПР, Теория) ---
                    else
                    {
                        var lessons = DatabaseHelper.GetLessonOrders(state.Grade!.Value, state.Subject!, state.LevelType!.Value, type);

                        if (lessons.Count == 0)
                        {
                            await EditMessageTextSafe(bot, chatId, messageId, "😔 Заданий по этим параметрам не найдено", keyboardBack);
                            return;
                        }

                        var buttons = lessons.Select(l => new[] {
                            InlineKeyboardButton.WithCallbackData($"Урок №{l}", $"search_lesson_{l}")
                        }).ToList();
                        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", $"search_level_{(int)state.LevelType!.Value}") });

                        await EditMessageTextSafe(bot, chatId, messageId, "📖 Выбери урок:", new InlineKeyboardMarkup(buttons));
                    }
                    return;
                }

                if (data.StartsWith("search_semester_"))
                {
                    int semester = int.Parse(data.Replace("search_semester_", ""));
                    var state = MessageHandler.GetUserSearchState(chatId)!;
                    if (state != null) state.Semester = semester;
                    else return;
                    state.TaskOrder = null;

                    var taskOrders = DatabaseHelper.GetTaskOrders(state.Grade!.Value, state.Subject!,
                        state.LevelType!.Value, state.GroupType!.Value, null, semester);

                    var keyboardBack = new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", $"search_group_{(int)state.GroupType!.Value}") } });

                    if (taskOrders.Count == 0)
                    {
                        await EditMessageTextSafe(bot, chatId, messageId, "😔 Заданий не найдено", keyboardBack);
                        return;
                    }

                    var tasks = DatabaseHelper.SearchTasks(state.Grade, state.Subject, state.LevelType, state.GroupType,
                                                            null, semester);

                    var buttons = tasks.OrderBy(t => t.TaskOrder).Select(t => new[] {
                        InlineKeyboardButton.WithCallbackData($"Задание №{t.TaskOrder}", $"show_task_{t.Id}")
                    }).ToList();
                    buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", $"search_group_{(int)state.GroupType!.Value}") });

                    await EditMessageTextSafe(bot, chatId, messageId, $"✅ Найдено заданий: {tasks.Count}\n\nВыбери нужное:", new InlineKeyboardMarkup(buttons));
                    return;
                }

                // --- ИЗМЕНЕНИЕ: search_lesson_ ---
                if (data.StartsWith("search_lesson_"))
                {
                    int lessonOrder = int.Parse(data.Replace("search_lesson_", ""));
                    var state = MessageHandler.GetUserSearchState(chatId)!;
                    state.LessonOrder = lessonOrder;
                    state.TaskOrder = null;

                    var keyboardBack = new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", $"search_group_{(int)state.GroupType!.Value}") } });

                    // --- Логика для КР/ПР (показываем список Номеров Заданий) ---
                    if (state.GroupType == TaskGroupType.ControlWork || state.GroupType == TaskGroupType.Test)
                    {
                        var taskOrders = DatabaseHelper.GetTaskOrders(state.Grade!.Value, state.Subject!,
                            state.LevelType!.Value, state.GroupType!.Value, lessonOrder, null);

                        if (taskOrders.Count == 0)
                        {
                            await EditMessageTextSafe(bot, chatId, messageId, "😔 Заданий не найдено", keyboardBack);
                            return;
                        }

                        var buttons = taskOrders.Select(to => new[] {
                            InlineKeyboardButton.WithCallbackData($"Задание №{to}", $"search_taskorder_{to}")
                        }).ToList();
                        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", $"search_group_{(int)state.GroupType!.Value}") });

                        await EditMessageTextSafe(bot, chatId, messageId, $"📖 Урок №{lessonOrder}\n\nВыбери задание:", new InlineKeyboardMarkup(buttons));
                    }
                    // --- Логика для ДЗ/Теории (показываем список Заданий/Ответов + НОВАЯ КНОПКА) ---
                    else
                    {
                        var tasks = DatabaseHelper.SearchTasks(state.Grade, state.Subject,
                            state.LevelType, state.GroupType, lessonOrder);

                        if (tasks.Count == 0)
                        {
                            await EditMessageTextSafe(bot, chatId, messageId, "😔 Заданий не найдено", keyboardBack);
                            return;
                        }

                        var buttons = new List<InlineKeyboardButton[]>();

                        // --- НОВАЯ КНОПКА ---
                        if (tasks.Count > 1) // Показываем, только если заданий > 1
                        {
                            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🚀 Отправить все задания урока", $"show_lesson_all_{lessonOrder}") });
                        }
                        // ------------------

                        foreach (var task in tasks.OrderBy(t => t.TaskOrder))
                        {
                            string buttonText = task.TaskOrder.HasValue
                                ? $"Задание №{task.TaskOrder.Value}"
                                : $"Задание (ID: {task.Id})";
                            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData(buttonText, $"show_task_{task.Id}") });
                        }
                        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", $"search_group_{(int)state.GroupType!.Value}") });

                        var keyboard = new InlineKeyboardMarkup(buttons);
                        await EditMessageTextSafe(bot, chatId, messageId, $"✅ Найдено заданий: {tasks.Count}\n\nВыбери нужное или отправь все разом:", keyboard);
                    }
                    return;
                }

                // --- НОВЫЙ ОБРАБОТЧИК ДЛЯ ВСЕХ ЗАДАНИЙ УРОКА ---
                if (data.StartsWith("show_lesson_all_"))
                {
                    int lessonOrder = int.Parse(data.Replace("show_lesson_all_", ""));
                    var state = MessageHandler.GetUserSearchState(chatId);

                    if (state == null || !state.Grade.HasValue || string.IsNullOrEmpty(state.Subject) ||
                        !state.LevelType.HasValue || !state.GroupType.HasValue)
                    {
                        await bot.AnswerCallbackQuery(callback.Id, "❌ Ошибка: Состояние поиска утеряно. Попробуй заново.");
                        return;
                    }

                    var tasks = DatabaseHelper.SearchTasks(state.Grade, state.Subject, state.LevelType, state.GroupType, lessonOrder)
                                              .OrderBy(t => t.TaskOrder)
                                              .ToList();

                    if (tasks.Count == 0)
                    {
                        await bot.AnswerCallbackQuery(callback.Id, "😔 Задания для этого урока не найдены.");
                        return;
                    }

                    try
                    {
                        // Удаляем меню, чтобы не мешало
                        await bot.DeleteMessage(chatId, messageId);
                    }
                    catch { }

                    // Вызываем новый метод в MessageHandler
                    await MessageHandler.SendFullLessonGallery(bot, chatId, tasks);
                    return;
                }

                if (data.StartsWith("search_taskorder_"))
                {
                    int taskOrder = int.Parse(data.Replace("search_taskorder_", ""));
                    var state = MessageHandler.GetUserSearchState(chatId)!;
                    if (state != null) state.TaskOrder = taskOrder;
                    else return;
                    state.Variant = null;

                    var variants = DatabaseHelper.GetVariants(state.Grade!.Value, state.Subject!,
                        state.LevelType!.Value, state.GroupType!.Value, state.LessonOrder!.Value, null, taskOrder);

                    var keyboardBack = new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", $"search_lesson_{state.LessonOrder}") } });

                    if (variants.Count == 0)
                    {
                        await EditMessageTextSafe(bot, chatId, messageId, "😔 Вариантов не найдено", keyboardBack);
                        return;
                    }

                    // Если вариант 1, сразу показываем
                    if (variants.Count == 1)
                    {
                        var task = DatabaseHelper.SearchTasks(state.Grade, state.Subject, state.LevelType, state.GroupType,
                            state.LessonOrder, null, taskOrder, variants[0]).FirstOrDefault();

                        if (task != null)
                        {
                            // Эмулируем нажатие show_task_
                            await HandleShowTask(bot, chatId, messageId, callback, $"show_task_{task.Id}");
                        }
                        return;
                    }

                    // Если вариантов много
                    var buttons = variants.Select(v => new[] {
                        InlineKeyboardButton.WithCallbackData($"Вариант {v}", $"search_variant_{v}")
                    }).ToList();
                    buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", $"search_lesson_{state.LessonOrder}") });

                    await EditMessageTextSafe(bot, chatId, messageId, $"Задание №{taskOrder}\n\nВыбери вариант:", new InlineKeyboardMarkup(buttons));
                    return;
                }

                if (data.StartsWith("search_variant_"))
                {
                    int variant = int.Parse(data.Replace("search_variant_", ""));
                    var state = MessageHandler.GetUserSearchState(chatId)!;
                    if (state != null) state.Variant = variant;
                    else return;

                    var task = DatabaseHelper.SearchTasks(state.Grade, state.Subject, state.LevelType, state.GroupType,
                            state.LessonOrder, null, state.TaskOrder, variant).FirstOrDefault();

                    if (task != null)
                    {
                        await HandleShowTask(bot, chatId, messageId, callback, $"show_task_{task.Id}");
                    }
                    else
                    {
                        await EditMessageTextSafe(bot, chatId, messageId, "😔 Ошибка: Задание не найдено");
                    }
                    return;
                }


                // --- БЛОК НАВИГАЦИИ И ПОКАЗА ЗАДАНИЙ ---
                if (data.StartsWith("show_task_"))
                {
                    await HandleShowTask(bot, chatId, messageId, callback, data);
                    return;
                }

                // --- Назад в главное меню ---
                if (data == "back_main")
                {
                    MessageHandler.ClearUserSearchState(chatId);
                    MessageHandler.CancelSubmission(chatId);

                    var buttons = new List<InlineKeyboardButton[]>
                    {
                        new[] { InlineKeyboardButton.WithCallbackData("🔍 Поиск ответов", "search_start") },
                        new[] { InlineKeyboardButton.WithCallbackData("📥 Предложить задание", "user_add_start") },
                        new[] { InlineKeyboardButton.WithCallbackData("💵 Поддержать проект", "donat_start") }
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
https://foxford.ru/lessons/475003/tasks/301386";

                    await EditMessageTextSafe(bot, chatId, messageId, welcomeText, keyboard);
                    return;
                }
            }
            catch (Exception ex)
            {
                if (!ex.Message.Contains("message to edit not found") && !ex.Message.Contains("message is not modified"))
                {
                    Console.WriteLine($"❌ Ошибка в CallbackHandler: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        // НОВЫЙ МЕТОД: Безопасное редактирование сообщений
        private static async Task EditMessageTextSafe(ITelegramBotClient bot, long chatId, int messageId, string text,
            InlineKeyboardMarkup? keyboard = null, ParseMode parseMode = ParseMode.None)
        {
            try
            {
                await bot.EditMessageText(chatId, messageId, text,
                    parseMode: parseMode,
                    replyMarkup: keyboard,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true });
            }
            catch (Exception ex) when (ex.Message.Contains("message is not modified"))
            {
                // Игнорируем - сообщение уже в нужном состоянии
            }
            catch (Exception ex) when (ex.Message.Contains("message to edit not found"))
            {
                // Сообщение не найдено (удалено), просто отправляем новое
                await bot.SendMessage(chatId, text,
                    parseMode: parseMode,
                    replyMarkup: keyboard,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true });
            }
            catch (Exception ex) when (ex.Message.Contains("no text in the message"))
            {
                // ОШИБКА: Попытка отредактировать текст у медиа-сообщения (фото).
                // РЕШЕНИЕ: Удаляем старое медиа-сообщение и отправляем новое текстовое.
                try
                {
                    await bot.DeleteMessage(chatId, messageId);
                }
                catch { }

                await bot.SendMessage(chatId, text,
                    parseMode: parseMode,
                    replyMarkup: keyboard,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true });
            }
        }

        // ВЫНЕСЕННЫЕ МЕТОДЫ ДЛЯ УПРОЩЕНИЯ
        private static async Task HandleShowTask(ITelegramBotClient bot, long chatId, int messageId, CallbackQuery callback, string data)
        {
            int taskId = int.Parse(data.Replace("show_task_", ""));
            var task = DatabaseHelper.GetTaskById(taskId);

            if (task == null || !task.IsModerated)
            {
                await EditMessageTextSafe(bot, chatId, messageId, "❌ Ошибка: Задание не найдено или удалено.");
                return;
            }

            // Обновляем состояние
            var state = MessageHandler.GetUserSearchState(chatId) ?? new UserSearchState();
            state.Grade = task.Grade;
            state.Subject = task.Subject;
            state.LevelType = task.LevelType;
            state.GroupType = task.GroupType;
            state.LessonOrder = task.LessonOrder;
            state.Semester = task.Semester;
            state.TaskOrder = task.TaskOrder;
            state.Variant = task.Variant;
            MessageHandler.SetUserSearchState(chatId, state);

            // Очистка старых медиа
            if (state.MediaGroupMessageIds.Count > 0)
            {
                _ = Task.Run(async () => {
                    foreach (var msgId in state.MediaGroupMessageIds)
                    {
                        try { await bot.DeleteMessage(chatId, msgId); } catch { }
                    }
                });
                state.MediaGroupMessageIds.Clear();
            }

            DatabaseHelper.UpdateLastRequest();
            Console.WriteLine($"{chatId} - Открыл задание {task.Id}");

            // --- 1. Формируем Заголовок ---
            string header = $"✅ <b>Найден ответ!</b>\n\n" +
                           $"📚 {task.Grade} класс | {task.Subject}" + MessageHandler.GetLevelTypeName(task.LevelType) + "\n" +
                           $"📖 {MessageHandler.GetGroupTypeName(task.GroupType)}";

            if (chatId == Program.ADMIN_ID)
            {
                header += $"\n<b>🆔 ID Задания: {task.Id}</b> (для админки)\n";
            }

            if (task.GroupType == TaskGroupType.Demo)
                header += $" | Полугодие {task.Semester}";
            else if (task.LessonOrder.HasValue)
                header += $" | Урок №{task.LessonOrder}";

            if (task.TaskOrder.HasValue)
                header += $" | Задание №{task.TaskOrder.Value}";

            if (task.Variant.HasValue)
                header += $" | Вариант {task.Variant.Value}";

            header += $"\n🔗 <a href='https://foxford.ru/lessons/{task.LessonNumber}/tasks/{task.TaskNumber}'>Открыть задание на Foxford</a>\n";

            // --- 2. Формируем Кнопки Навигации ---
            var navigationButtons = new List<InlineKeyboardButton>();

            // --- 2.1 Логика для ПР (Навигация по Вариантам и Заданиям) ---
            if (task.GroupType == TaskGroupType.Test)
            {
                // Получаем все Задания этого Урока
                var allTaskOrders = DatabaseHelper.GetTaskOrders(task.Grade, task.Subject, task.LevelType, task.GroupType, task.LessonOrder, null);
                // Получаем все Варианты этого Задания
                var allVariants = DatabaseHelper.GetVariants(task.Grade, task.Subject, task.LevelType, task.GroupType, task.LessonOrder!.Value, null, task.TaskOrder!.Value);

                int currentTaskOrderIndex = allTaskOrders.IndexOf(task.TaskOrder!.Value);
                int currentVariantIndex = allVariants.IndexOf(task.Variant!.Value);

                header += $"\n📊 Задание {currentTaskOrderIndex + 1} из {allTaskOrders.Count} | Вариант {currentVariantIndex + 1} из {allVariants.Count}";

                // Кнопка "Пред. Задание"
                if (currentTaskOrderIndex > 0)
                    navigationButtons.Add(InlineKeyboardButton.WithCallbackData("⬅️ Пред. Зад.", $"search_taskorder_{allTaskOrders[currentTaskOrderIndex - 1]}"));
                // Кнопка "Пред. Вариант"
                if (currentVariantIndex > 0)
                    navigationButtons.Add(InlineKeyboardButton.WithCallbackData("◀️ Пред. Вар.", $"search_variant_{allVariants[currentVariantIndex - 1]}"));

                // Кнопка "К Списку" (возврат к списку заданий)
                navigationButtons.Add(InlineKeyboardButton.WithCallbackData("📋 К списку", $"search_lesson_{task.LessonOrder}"));

                // Кнопка "След. Вариант"
                if (currentVariantIndex < allVariants.Count - 1)
                    navigationButtons.Add(InlineKeyboardButton.WithCallbackData("▶️ След. Вар.", $"search_variant_{allVariants[currentVariantIndex + 1]}"));
                // Кнопка "След. Задание"
                if (currentTaskOrderIndex < allTaskOrders.Count - 1)
                    navigationButtons.Add(InlineKeyboardButton.WithCallbackData("➡️ След. Зад.", $"search_taskorder_{allTaskOrders[currentTaskOrderIndex + 1]}"));
            }
            // --- 2.2 Логика для Демо (Навигация по Заданиям) ---
            else if (task.GroupType == TaskGroupType.Demo)
            {
                var allTasks = DatabaseHelper.SearchTasks(task.Grade, task.Subject, task.LevelType, task.GroupType, null, task.Semester).OrderBy(t => t.TaskOrder).ToList();
                int currentIndex = allTasks.FindIndex(t => t.Id == taskId);

                header += $"\n📊 Задание {currentIndex + 1} из {allTasks.Count}";

                if (currentIndex > 0)
                    navigationButtons.Add(InlineKeyboardButton.WithCallbackData("⬅️ Назад", $"show_task_{allTasks[currentIndex - 1].Id}"));

                navigationButtons.Add(InlineKeyboardButton.WithCallbackData("📋 К списку", $"search_semester_{task.Semester}"));

                if (currentIndex < allTasks.Count - 1)
                    navigationButtons.Add(InlineKeyboardButton.WithCallbackData("➡️ Дальше", $"show_task_{allTasks[currentIndex + 1].Id}"));
            }
            // --- 2.2 Логика для КР (Навигация по Заданиям) ---
            else if (task.GroupType == TaskGroupType.ControlWork)
            {
                // Получаем все Задания этого Урока
                var allTaskOrders = DatabaseHelper.GetTaskOrders(task.Grade, task.Subject, task.LevelType, task.GroupType, null, task.Semester);
                // Получаем все Варианты этого Задания
                var allVariants = DatabaseHelper.GetVariants(task.Grade, task.Subject, task.LevelType, task.GroupType, null, task.Semester, task.TaskOrder!.Value);

                int currentTaskOrderIndex = allTaskOrders.IndexOf(task.TaskOrder!.Value);
                int currentVariantIndex = allVariants.IndexOf(task.Variant!.Value);

                header += $"\n📊 Задание {currentTaskOrderIndex + 1} из {allTaskOrders.Count} | Вариант {currentVariantIndex + 1} из {allVariants.Count}";

                // Кнопка "Пред. Задание"
                if (currentTaskOrderIndex > 0)
                    navigationButtons.Add(InlineKeyboardButton.WithCallbackData("⬅️ Пред. Зад.", $"search_taskorder_{allTaskOrders[currentTaskOrderIndex - 1]}"));
                // Кнопка "Пред. Вариант"
                if (currentVariantIndex > 0)
                    navigationButtons.Add(InlineKeyboardButton.WithCallbackData("◀️ Пред. Вар.", $"search_variant_{allVariants[currentVariantIndex - 1]}"));

                // Кнопка "К Списку" (возврат к списку заданий)
                navigationButtons.Add(InlineKeyboardButton.WithCallbackData("📋 К списку", $"search_lesson_{task.LessonOrder}"));

                // Кнопка "След. Вариант"
                if (currentVariantIndex < allVariants.Count - 1)
                    navigationButtons.Add(InlineKeyboardButton.WithCallbackData("▶️ След. Вар.", $"search_variant_{allVariants[currentVariantIndex + 1]}"));
                // Кнопка "След. Задание"
                if (currentTaskOrderIndex < allTaskOrders.Count - 1)
                    navigationButtons.Add(InlineKeyboardButton.WithCallbackData("➡️ След. Зад.", $"search_taskorder_{allTaskOrders[currentTaskOrderIndex + 1]}"));
            }
            // --- 2.3 Логика для ДЗ / Теории (Навигация по Заданиям и Урокам) ---
            else
            {
                var allTasks = DatabaseHelper.SearchTasks(task.Grade, task.Subject, task.LevelType, task.GroupType, task.LessonOrder).OrderBy(t => t.TaskOrder).ToList();
                int currentIndex = allTasks.FindIndex(t => t.Id == taskId);

                header += $"\n📊 Задание {currentIndex + 1} из {allTasks.Count}";

                // Навигация по УРОКАМ
                var allLessons = DatabaseHelper.GetLessonOrders(task.Grade, task.Subject, task.LevelType, task.GroupType);
                int currentLessonIndex = allLessons.IndexOf(task.LessonOrder!.Value);

                if (currentLessonIndex > 0)
                    navigationButtons.Add(InlineKeyboardButton.WithCallbackData("⬅️ Пред. Урок", $"search_lesson_{allLessons[currentLessonIndex - 1]}"));

                if (currentIndex > 0)
                    navigationButtons.Add(InlineKeyboardButton.WithCallbackData("◀️ Пред. Зад.", $"show_task_{allTasks[currentIndex - 1].Id}"));

                navigationButtons.Add(InlineKeyboardButton.WithCallbackData("📋 К списку", $"search_lesson_{task.LessonOrder}"));

                if (currentIndex < allTasks.Count - 1)
                    navigationButtons.Add(InlineKeyboardButton.WithCallbackData("▶️ След. Зад.", $"show_task_{allTasks[currentIndex + 1].Id}"));

                if (currentLessonIndex < allLessons.Count - 1)
                    navigationButtons.Add(InlineKeyboardButton.WithCallbackData("➡️ След. Урок", $"search_lesson_{allLessons[currentLessonIndex + 1]}"));
            }


            var keyboard = new InlineKeyboardMarkup(new[] { navigationButtons.ToArray() });

            // --- 3. Отправка Медиа ---
            await SendTaskMedia(bot, chatId, callback.Message.MessageId, task, header, keyboard, state);
        }

        private static async Task SendTaskMedia(ITelegramBotClient bot, long chatId, int messageId, FoxfordTask task,
            string header, InlineKeyboardMarkup keyboard, UserSearchState state)
        {
            if (string.IsNullOrEmpty(task.ScreenshotPaths))
            {
                await EditMessageTextSafe(bot, chatId, messageId,
                    header + "\n\n❌ Скриншоты не найдены", keyboard, ParseMode.Html);
                state.LastAnswerMessageId = messageId;
                return;
            }

            var screenshots = task.ScreenshotPaths.Split(',').Select(p => p.Replace('\\', Path.DirectorySeparatorChar))
                .Where(System.IO.File.Exists).ToList();

            if (screenshots.Count == 1 && System.IO.File.Exists(screenshots[0]))
            {
                try
                {
                    using var stream = System.IO.File.OpenRead(screenshots[0]);
                    await bot.EditMessageMedia(chatId, messageId,
                        new InputMediaPhoto(new InputFileStream(stream))
                        {
                            Caption = header,
                            ParseMode = ParseMode.Html
                        },
                        replyMarkup: keyboard);
                    state.LastAnswerMessageId = messageId;
                }
                catch
                {
                    try { await bot.DeleteMessage(chatId, messageId); } catch { }
                    using var stream = System.IO.File.OpenRead(screenshots[0]);
                    var sentMsg = await bot.SendPhoto(chatId, new InputFileStream(stream),
                        caption: header, parseMode: ParseMode.Html, replyMarkup: keyboard);
                    state.LastAnswerMessageId = sentMsg.MessageId;
                }
            }
            else
            {
                try { await bot.DeleteMessage(chatId, messageId); } catch { }

                var mediaGroup = new List<IAlbumInputMedia>();
                var streams = new List<MemoryStream>();

                for (int i = 0; i < Math.Min(screenshots.Count, 10); i++)
                {
                    if (System.IO.File.Exists(screenshots[i]))
                    {
                        var bytes = await System.IO.File.ReadAllBytesAsync(screenshots[i]);
                        var stream = new MemoryStream(bytes);
                        streams.Add(stream);
                        var inputFile = new InputFileStream(stream, $"photo{i}.jpg");

                        if (i == 0)
                            mediaGroup.Add(new InputMediaPhoto(inputFile) { Caption = header, ParseMode = ParseMode.Html });
                        else
                            mediaGroup.Add(new InputMediaPhoto(inputFile) { Caption = $"📄 {i + 1}/{Math.Min(screenshots.Count, 10)}" });
                    }
                }

                if (mediaGroup.Count > 0)
                {
                    var sentMessages = await bot.SendMediaGroup(chatId, mediaGroup);
                    state.MediaGroupMessageIds.Clear();
                    foreach (var msg in sentMessages)
                        state.MediaGroupMessageIds.Add(msg.MessageId);

                    var navMsg = await bot.SendMessage(chatId, "⬇️ Навигация:", replyMarkup: keyboard);
                    state.LastAnswerMessageId = navMsg.MessageId;

                    // Закрываем стримы асинхронно
                    _ = Task.Run(async () =>
                    {
                        foreach (var stream in streams)
                        {
                            try { await stream.DisposeAsync(); } catch { }
                        }
                    });
                }
            }
        }

        private static async Task ShowNextModerationTask(ITelegramBotClient bot, long chatId, int messageId)
        {
            var tasks = DatabaseHelper.GetTasksForModeration();
            if (tasks.Count == 0)
            {
                await EditMessageTextSafe(bot, chatId, messageId, "📬 Нет заданий на модерацию.",
                    new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("◀️ Назад", "admin_panel")));
                return;
            }

            var task = tasks[0];
            await SendTaskForModeration(bot, chatId, messageId, task, tasks.Count);
        }

        private static async Task SendTaskForModeration(ITelegramBotClient bot, long chatId, int messageId, FoxfordTask task, int pendingCount)
        {
            string header = $"📬 <b>Модерация (Осталось: {pendingCount})</b>\n\n" +
                           $"От: {task.SubmittedByUserId} | {task.CreatedAt:dd.MM.yyyy}\n" +
                           $"🔗 <a href='https://foxford.ru/lessons/{task.LessonNumber}/tasks/{task.TaskNumber}'>Ссылка</a>\n\n" +
                           $"📚 {task.Grade} класс | {task.Subject}" + MessageHandler.GetLevelTypeName(task.LevelType) + "\n" +
                           $"📖 {MessageHandler.GetGroupTypeName(task.GroupType)}";

            if (task.GroupType == TaskGroupType.Demo)
                header += $" | Полугодие {task.Semester}";
            else if (task.LessonOrder.HasValue)
                header += $" | Урок №{task.LessonOrder}";

            if (task.TaskOrder.HasValue)
                header += $" | Задание №{task.TaskOrder.Value}";

            if (task.Variant.HasValue)
                header += $" | Вариант {task.Variant.Value}";

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("✅ Одобрить", $"approve_{task.Id}") },
                new[] { InlineKeyboardButton.WithCallbackData("❌ Отклонить", $"decline_{task.Id}") },
                new[] { InlineKeyboardButton.WithCallbackData("▶️ Пропустить", "admin_moderate") },
                new[] { InlineKeyboardButton.WithCallbackData("◀️ В админку", "admin_panel") }
            });

            if (!string.IsNullOrEmpty(task.ScreenshotPaths))
            {
                var screenshots = task.ScreenshotPaths.Split(',')
                    .Select(p => p.Replace('\\', Path.DirectorySeparatorChar))
                    .Where(System.IO.File.Exists).ToList();

                var mediaGroup = new List<IAlbumInputMedia>();
                var streams = new List<MemoryStream>();

                for (int i = 0; i < Math.Min(screenshots.Count, 10); i++)
                {
                    if (System.IO.File.Exists(screenshots[i]))
                    {
                        var bytes = await System.IO.File.ReadAllBytesAsync(screenshots[i]);
                        var stream = new MemoryStream(bytes);
                        streams.Add(stream);
                        var inputFile = new InputFileStream(stream, $"photo{i}.jpg");

                        if (i == 0)
                            mediaGroup.Add(new InputMediaPhoto(inputFile) { Caption = header, ParseMode = ParseMode.Html });
                        else
                            mediaGroup.Add(new InputMediaPhoto(inputFile));
                    }
                }

                if (mediaGroup.Count > 0)
                {
                    try { await bot.DeleteMessage(chatId, messageId); } catch { }
                    await bot.SendMediaGroup(chatId, mediaGroup);
                    await bot.SendMessage(chatId, "⬇️ Выбери действие:", replyMarkup: keyboard);

                    // Закрываем стримы асинхронно
                    _ = Task.Run(async () =>
                    {
                        foreach (var stream in streams)
                        {
                            try { await stream.DisposeAsync(); } catch { }
                        }
                    });
                }
                else
                {
                    await EditMessageTextSafe(bot, chatId, messageId, header + "\n\n❌ Файлы скриншотов не найдены!",
                        keyboard, ParseMode.Html);
                }
            }
            else
            {
                await EditMessageTextSafe(bot, chatId, messageId, header + "\n\n❌ Скриншоты не приложены!",
                    keyboard, ParseMode.Html);
            }
        }

        public static async Task AskSubject(ITelegramBotClient bot, long chatId, int messageId, string callbackPrefix)
        {
            var buttons = MessageHandler.SubjectsList.Select(s =>
                new[] { InlineKeyboardButton.WithCallbackData(s, $"{callbackPrefix}{s}") }
            ).ToList();

            var keyboard = new InlineKeyboardMarkup(buttons);
            await EditMessageTextSafe(bot, chatId, messageId, "📖 Выбери предмет:", keyboard);
        }

        public static async Task AskLevelType(ITelegramBotClient bot, long chatId, int messageId, string callbackPrefix)
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("Обычный", $"{callbackPrefix}0") },
                new[] { InlineKeyboardButton.WithCallbackData("Профильный", $"{callbackPrefix}1") },
                new[] { InlineKeyboardButton.WithCallbackData("Перевернутый", $"{callbackPrefix}2") }
            });

            await EditMessageTextSafe(bot, chatId, messageId, "📚 Тип (уровень):", keyboard);
        }

        public static async Task AskGroupType(ITelegramBotClient bot, long chatId, int messageId, string callbackPrefix)
        {
            var level = MessageHandler.GetCurrentLevelType(chatId);
            InlineKeyboardMarkup keyboard;

            if (level == TaskLevelType.Inverted)
            {
                keyboard = new InlineKeyboardMarkup(new[]
               {
                    new[] { InlineKeyboardButton.WithCallbackData("📚 Задачи по теории", $"{callbackPrefix}4") }, // Theory
                    new[] { InlineKeyboardButton.WithCallbackData("📝 Домашняя работа", $"{callbackPrefix}0") }   // Homework
                });
            }
            else
            {
                keyboard = new InlineKeyboardMarkup(new[]
               {
                    new[] { InlineKeyboardButton.WithCallbackData("📝 Домашняя работа", $"{callbackPrefix}0") },
                    new[] { InlineKeyboardButton.WithCallbackData("📊 Проверочная", $"{callbackPrefix}1") },
                    new[] { InlineKeyboardButton.WithCallbackData("🎯 Демоверсия", $"{callbackPrefix}2") },
                    new[] { InlineKeyboardButton.WithCallbackData("📋 Контрольная", $"{callbackPrefix}3") }
                });
            }

            await EditMessageTextSafe(bot, chatId, messageId, "📚 Выбери тип группы:", keyboard);
        }

        public static async Task AskGrade(ITelegramBotClient bot, long chatId, string callbackPrefix)
        {
            var buttons = new List<InlineKeyboardButton[]>();
            var row = new List<InlineKeyboardButton>();
            for (int g = 5; g <= 11; g++)
            {
                row.Add(InlineKeyboardButton.WithCallbackData($"{g}", $"{callbackPrefix}{g}"));
                if (row.Count == 4)
                {
                    buttons.Add(row.ToArray());
                    row.Clear();
                }
            }
            if (row.Count > 0) buttons.Add(row.ToArray());

            var keyboard = new InlineKeyboardMarkup(buttons);
            await bot.SendMessage(chatId, "📚 Выбери класс:", replyMarkup: keyboard);
        }
    }
}