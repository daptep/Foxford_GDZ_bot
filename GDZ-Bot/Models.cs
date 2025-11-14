using System;
using System.Collections.Generic;

namespace FoxfordAnswersBot
{
    // Тип группы задач
    public enum TaskGroupType
    {
        Homework,      // Домашняя работа 0
        Test,          // Проверочная 1
        Demo,          // Демоверсия 2
        ControlWork,   // Контрольная 3
        Theory         // Задачи по теории 4
    }

    // Тип уровня (сложности)
    public enum TaskLevelType
    {
        Regular,   // Обычный
        Profile,   // Профильный
        Inverted   // Перевернутый
    }

    // Модель задания
    public class FoxfordTask
    {
        public int Id { get; set; }

        // Ссылка на задание
        public string LessonNumber { get; set; } = "";  // Номер урока из ссылки
        public string TaskNumber { get; set; } = "";    // Номер задания из ссылки

        // Параметры
        public int Grade { get; set; }                  // Класс
        public string Subject { get; set; } = "";       // Предмет
        public TaskLevelType LevelType { get; set; }    // Уровень (Обычный/Проф/Переверн)
        public TaskGroupType GroupType { get; set; }    // Тип группы

        public int? LessonOrder { get; set; }            // Порядковый номер урока (для ДЗ/КР/ПР/Теории)
        public int? TaskOrder { get; set; }             // Порядковый номер задания (для ДЗ/КР/ПР/Теории/Демо)

        public int? Variant { get; set; }
        public int? Semester { get; set; }

        public string ScreenshotPaths { get; set; } = ""; // Пути через запятую

        public bool IsModerated { get; set; } = false;
        public long SubmittedByUserId { get; set; } = 0;

        // Метаданные
        public DateTime CreatedAt { get; set; }
    }

    // Модель для статистики
    public class BotStatistics
    {
        public int TotalUsers { get; set; }
        public int TotalTasks { get; set; }
        public int PendingTasks { get; set; }
        public DateTime? LastTaskAdded { get; set; }
        public DateTime? LastTaskRequested { get; set; }
    }

    // Базовый класс для состояния добавления
    public abstract class SubmissionState
    {
        public int Step { get; set; }
        public FoxfordTask Task { get; set; } = new FoxfordTask();
        public List<string> ScreenshotPaths { get; set; } = new List<string>();
    }

    // Состояние админа при добавлении задания
    public class AdminState : SubmissionState
    {
        public bool IsBatchMode { get; set; } = false;
    }

    // Состояние пользователя при добавлении задания
    public class UserSubmissionState : SubmissionState
    {
    }

    // Состояние пользователя при поиске
    public class UserSearchState
    {
        public int? Grade { get; set; }
        public string? Subject { get; set; }
        public TaskLevelType? LevelType { get; set; }
        public TaskGroupType? GroupType { get; set; }
        public int? LessonOrder { get; set; }
        public int? Semester { get; set; }
        public int? TaskOrder { get; set; }
        public int? Variant { get; set; }
        public int? LastAnswerMessageId { get; set; }
        public List<int> MediaGroupMessageIds { get; set; } = new List<int>();
    }
}