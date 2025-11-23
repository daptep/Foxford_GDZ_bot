using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FoxfordAnswersBot
{
    public static class DatabaseHelper
    {
        public static string DB_PATH = "foxford_answers.db";
        public static string IMAGES_FOLDER = "task_images";

        // КРИТИЧНО: Пул подключений + WAL режим для параллельных операций
        private static readonly string ConnectionString = $"Data Source={DB_PATH};Mode=ReadWriteCreate;Cache=Shared;Pooling=True;";

        public static void InitializeDatabase()
        {
            if (!Directory.Exists(IMAGES_FOLDER))
                Directory.CreateDirectory(IMAGES_FOLDER);

            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            // КРИТИЧНО: Включаем WAL режим для быстрых чтений
            new SqliteCommand("PRAGMA journal_mode=WAL;", conn).ExecuteNonQuery();
            new SqliteCommand("PRAGMA synchronous=NORMAL;", conn).ExecuteNonQuery();
            new SqliteCommand("PRAGMA cache_size=10000;", conn).ExecuteNonQuery();
            new SqliteCommand("PRAGMA temp_store=MEMORY;", conn).ExecuteNonQuery();

            string createTableQuery = @"
                CREATE TABLE IF NOT EXISTS Tasks (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    LessonNumber TEXT NOT NULL,
                    TaskNumber TEXT NOT NULL,
                    Grade INTEGER NOT NULL,
                    Subject TEXT NOT NULL,
                    LevelType INTEGER NOT NULL, 
                    GroupType INTEGER NOT NULL,
                    LessonOrder INTEGER,
                    TaskOrder INTEGER,
                    
                    Variant INTEGER,
                    Semester INTEGER,

                    ScreenshotPaths TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    IsModerated INTEGER NOT NULL DEFAULT 0,
                    SubmittedByUserId INTEGER NOT NULL DEFAULT 0
                );
                
                CREATE TABLE IF NOT EXISTS Users (
                    UserId INTEGER PRIMARY KEY,
                    FirstName TEXT,
                    Username TEXT,
                    FirstSeenAt TEXT NOT NULL
                );
                
                CREATE TABLE IF NOT EXISTS Statistics (
                    Id INTEGER PRIMARY KEY,
                    LastTaskRequestedAt TEXT
                );";

            using (var cmd = new SqliteCommand(createTableQuery, conn))
            {
                cmd.ExecuteNonQuery();
            }

            // КРИТИЧНО: Создаем индексы для быстрого поиска
            try
            {
                new SqliteCommand("DROP INDEX IF EXISTS idx_tasks_search;", conn).ExecuteNonQuery();
                new SqliteCommand("DROP INDEX IF EXISTS idx_tasks_lesson;", conn).ExecuteNonQuery();
                new SqliteCommand("DROP INDEX IF EXISTS idx_tasks_moderated;", conn).ExecuteNonQuery();

                new SqliteCommand("CREATE INDEX IF NOT EXISTS idx_tasks_search_main ON Tasks(Grade, Subject, LevelType, GroupType, LessonOrder, IsModerated);", conn).ExecuteNonQuery();
                new SqliteCommand("CREATE INDEX IF NOT EXISTS idx_tasks_search_demo ON Tasks(Grade, Subject, LevelType, GroupType, Semester, IsModerated);", conn).ExecuteNonQuery();
                new SqliteCommand("CREATE INDEX IF NOT EXISTS idx_tasks_link ON Tasks(LessonNumber, TaskNumber, IsModerated);", conn).ExecuteNonQuery();
                Console.WriteLine("DB: Индексы успешно созданы/обновлены.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DB Error: Не удалось создать индексы - {ex.Message}");
            }

            // Миграция старых таблиц
            try
            {
                new SqliteCommand("ALTER TABLE Tasks RENAME COLUMN IsProfile TO LevelType", conn).ExecuteNonQuery();
                Console.WriteLine("DB: Колонка 'IsProfile' переименована в 'LevelType'.");
            }
            catch (SqliteException ex) when (ex.Message.Contains("no such column") || ex.Message.Contains("already has a column"))
            {
                // OK
            }

            try
            {
                new SqliteCommand("ALTER TABLE Tasks ADD COLUMN IsModerated INTEGER NOT NULL DEFAULT 0", conn).ExecuteNonQuery();
                new SqliteCommand("ALTER TABLE Tasks ADD COLUMN SubmittedByUserId INTEGER NOT NULL DEFAULT 0", conn).ExecuteNonQuery();
                new SqliteCommand("UPDATE Tasks SET IsModerated = 1 WHERE IsModerated = 0", conn).ExecuteNonQuery();
                Console.WriteLine("DB: Колонки модерации добавлены.");
            }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column name"))
            {
                // OK
            }

            // НОВЫЕ МИГРАЦИИ
            try
            {
                new SqliteCommand("ALTER TABLE Tasks ADD COLUMN Variant INTEGER", conn).ExecuteNonQuery();
                new SqliteCommand("ALTER TABLE Tasks ADD COLUMN Semester INTEGER", conn).ExecuteNonQuery();
                Console.WriteLine("DB: Колонки Variant и Semester добавлены.");
            }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column name"))
            {
                // OK
            }

            // TODO: Требуется ручная миграция для изменения LessonOrder NOT NULL -> NULL,
            // если база уже была заполнена.
            // В текущей схеме createTableQuery колонка LessonOrder уже nullable (INTEGER)

            using var initCmd = new SqliteCommand("INSERT OR IGNORE INTO Statistics (Id) VALUES (1)", conn);
            initCmd.ExecuteNonQuery();
        }

        public static FoxfordTask? GetTaskById(int id)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            string query = "SELECT * FROM Tasks WHERE Id = @id LIMIT 1";
            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@id", id);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return ReadTaskFromReader(reader);
            }

            return null;
        }

        public static void AddTask(FoxfordTask task)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            // --- НОВАЯ ЛОГИКА ВАРИАНТОВ ---
            // Если это КР или ПР, и у нее есть номер задания
            if ((task.GroupType == TaskGroupType.ControlWork || task.GroupType == TaskGroupType.Test) && task.TaskOrder.HasValue)
            {
                string variantQuery = @"SELECT MAX(Variant) FROM Tasks WHERE
                                        Grade = @grade AND Subject = @subject AND LevelType = @level AND
                                        GroupType = @group AND LessonOrder = @order AND TaskOrder = @taskorder";

                using var vCmd = new SqliteCommand(variantQuery, conn);
                vCmd.Parameters.AddWithValue("@grade", task.Grade);
                vCmd.Parameters.AddWithValue("@subject", task.Subject);
                vCmd.Parameters.AddWithValue("@level", (int)task.LevelType);
                vCmd.Parameters.AddWithValue("@group", (int)task.GroupType);
                // Для КР/ПР LessonOrder должен быть
                vCmd.Parameters.AddWithValue("@order", task.LessonOrder.HasValue ? (object)task.LessonOrder.Value : DBNull.Value);
                vCmd.Parameters.AddWithValue("@taskorder", task.TaskOrder.Value);

                var result = vCmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                {
                    task.Variant = Convert.ToInt32(result) + 1;
                }
                else
                {
                    task.Variant = 1; // Первый вариант
                }
            }
            // -------------------------------


            string query = @"INSERT INTO Tasks 
                (LessonNumber, TaskNumber, Grade, Subject, LevelType, GroupType, LessonOrder, TaskOrder, Variant, Semester, ScreenshotPaths, CreatedAt, IsModerated, SubmittedByUserId)
                VALUES (@lesson, @task, @grade, @subject, @level, @group, @order, @taskorder, @variant, @semester, @screenshots, @created, @moderated, @submitter)";

            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@lesson", task.LessonNumber);
            cmd.Parameters.AddWithValue("@task", task.TaskNumber);
            cmd.Parameters.AddWithValue("@grade", task.Grade);
            cmd.Parameters.AddWithValue("@subject", task.Subject);
            cmd.Parameters.AddWithValue("@level", (int)task.LevelType);
            cmd.Parameters.AddWithValue("@group", (int)task.GroupType);
            cmd.Parameters.AddWithValue("@order", task.LessonOrder.HasValue ? (object)task.LessonOrder.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@taskorder", task.TaskOrder.HasValue ? (object)task.TaskOrder.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@variant", task.Variant.HasValue ? (object)task.Variant.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@semester", task.Semester.HasValue ? (object)task.Semester.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@screenshots", task.ScreenshotPaths);
            cmd.Parameters.AddWithValue("@created", DateTime.Now.ToString("o"));
            cmd.Parameters.AddWithValue("@moderated", task.IsModerated ? 1 : 0);
            cmd.Parameters.AddWithValue("@submitter", task.SubmittedByUserId);

            cmd.ExecuteNonQuery();
        }

        public static FoxfordTask? FindTaskByLink(string lessonNumber, string taskNumber)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            string query = "SELECT * FROM Tasks WHERE LessonNumber = @lesson AND TaskNumber = @task AND IsModerated = 1 LIMIT 1";
            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@lesson", lessonNumber);
            cmd.Parameters.AddWithValue("@task", taskNumber);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return ReadTaskFromReader(reader);
            }

            return null;
        }

        public static bool CheckForDuplicate(FoxfordTask task)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            // 1. Проверка по ссылке (глобально)
            string linkQuery = "SELECT COUNT(*) FROM Tasks WHERE LessonNumber = @lesson AND TaskNumber = @task";
            using (var linkCmd = new SqliteCommand(linkQuery, conn))
            {
                linkCmd.Parameters.AddWithValue("@lesson", task.LessonNumber);
                linkCmd.Parameters.AddWithValue("@task", task.TaskNumber);
                if (Convert.ToInt32(linkCmd.ExecuteScalar()) > 0) return true;
            }

            // 2. Проверка по параметрам (зависит от типа)
            string paramQuery = "SELECT COUNT(*) FROM Tasks WHERE Grade = @grade AND Subject = @subject AND LevelType = @level AND GroupType = @group";

            // Для Демо - проверяем Семестр и НомерЗадания
            if (task.GroupType == TaskGroupType.Demo)
            {
                paramQuery += " AND Semester = @semester AND TaskOrder = @taskorder";
            }
            // Для ДЗ и Теории - проверяем Урок и НомерЗадания
            else if (task.GroupType == TaskGroupType.Homework || task.GroupType == TaskGroupType.Theory)
            {
                paramQuery += " AND LessonOrder = @order AND TaskOrder = @taskorder";
            }
            // Для КР и ПР - мы НЕ проверяем дубликат по TaskOrder, т.к. там варианты.
            // Мы просто дадим AddTask() добавить новый вариант.
            else if (task.GroupType == TaskGroupType.ControlWork || task.GroupType == TaskGroupType.Test)
            {
                // Не проверяем дубликаты по параметрам, только по ссылке (уже сделали)
                // Любое добавление КР/ПР с существующим TaskOrder - это новый ВАРИАНТ.
                return false;
            }
            // Для других типов (если будут), у которых нет TaskOrder
            else
            {
                paramQuery += " AND LessonOrder = @order";
            }


            using var cmd = new SqliteCommand(paramQuery, conn);
            cmd.Parameters.AddWithValue("@grade", task.Grade);
            cmd.Parameters.AddWithValue("@subject", task.Subject);
            cmd.Parameters.AddWithValue("@level", (int)task.LevelType);
            cmd.Parameters.AddWithValue("@group", (int)task.GroupType);

            if (task.GroupType == TaskGroupType.Demo)
            {
                cmd.Parameters.AddWithValue("@semester", task.Semester.HasValue ? (object)task.Semester.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@taskorder", task.TaskOrder.HasValue ? (object)task.TaskOrder.Value : DBNull.Value);
            }
            else if (task.GroupType == TaskGroupType.Homework || task.GroupType == TaskGroupType.Theory)
            {
                cmd.Parameters.AddWithValue("@order", task.LessonOrder.HasValue ? (object)task.LessonOrder.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@taskorder", task.TaskOrder.HasValue ? (object)task.TaskOrder.Value : DBNull.Value);
            }
            else if (task.GroupType != TaskGroupType.ControlWork && task.GroupType != TaskGroupType.Test)
            {
                cmd.Parameters.AddWithValue("@order", task.LessonOrder.HasValue ? (object)task.LessonOrder.Value : DBNull.Value);
            }

            var result = cmd.ExecuteScalar();
            return result != null && Convert.ToInt32(result) > 0;
        }

        public static List<FoxfordTask> SearchTasks(int? grade = null, string? subject = null,
            TaskLevelType? levelType = null, TaskGroupType? groupType = null,
            int? lessonOrder = null, int? semester = null, int? taskOrder = null, int? variant = null)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            var conditions = new List<string> { "IsModerated = 1" };
            if (grade.HasValue) conditions.Add($"Grade = {grade.Value}");
            if (!string.IsNullOrEmpty(subject)) conditions.Add($"Subject = '{subject.Replace("'", "''")}'");

            // Логика Перевернутых классов
            if (levelType.HasValue)
            {
                if (levelType.Value == TaskLevelType.Inverted &&
                   (groupType == TaskGroupType.ControlWork || groupType == TaskGroupType.Test || groupType == TaskGroupType.Demo))
                {
                    // Подменяем поиск на Обычный
                    conditions.Add($"LevelType = {(int)TaskLevelType.Regular}");
                }
                else
                {
                    conditions.Add($"LevelType = {(int)levelType.Value}");
                }
            }

            if (groupType.HasValue) conditions.Add($"GroupType = {(int)groupType.Value}");
            if (lessonOrder.HasValue) conditions.Add($"LessonOrder = {lessonOrder.Value}");
            if (semester.HasValue) conditions.Add($"Semester = {semester.Value}");
            if (taskOrder.HasValue) conditions.Add($"TaskOrder = {taskOrder.Value}");
            if (variant.HasValue) conditions.Add($"Variant = {variant.Value}");

            string where = "WHERE " + string.Join(" AND ", conditions);
            // Сортировка по Варианту важна для КР/ПР
            string query = $"SELECT * FROM Tasks {where} ORDER BY TaskOrder, Variant, Id";

            using var cmd = new SqliteCommand(query, conn);
            using var reader = cmd.ExecuteReader();

            var tasks = new List<FoxfordTask>();
            while (reader.Read())
            {
                tasks.Add(ReadTaskFromReader(reader));
            }

            return tasks;
        }

        // ОПТИМИЗАЦИЯ: Кэшированные запросы для фильтров
        private static Dictionary<string, List<int>> gradesCache = new Dictionary<string, List<int>>();
        private static DateTime gradesCacheTime = DateTime.MinValue;

        public static List<int> GetGrades()
        {
            // Кэш на 5 минут
            if ((DateTime.Now - gradesCacheTime).TotalMinutes < 5 && gradesCache.ContainsKey("all"))
            {
                return gradesCache["all"];
            }

            var result = GetDistinctValues<int>("Grade", "WHERE IsModerated = 1");
            gradesCache["all"] = result;
            gradesCacheTime = DateTime.Now;
            return result;
        }

        public static List<string> GetSubjects(int? grade = null)
        {
            string where = grade.HasValue ? $"WHERE Grade = {grade.Value} AND IsModerated = 1" : "WHERE IsModerated = 1";
            return GetDistinctValues<string>("Subject", where);
        }

        public static List<int> GetLessonOrders(int grade, string subject, TaskLevelType levelType, TaskGroupType groupType)
        {
            // Логика Перевернутых
            if (levelType == TaskLevelType.Inverted &&
               (groupType == TaskGroupType.ControlWork || groupType == TaskGroupType.Test))
            {
                levelType = TaskLevelType.Regular;
            }

            string where = $"WHERE Grade = {grade} AND Subject = '{subject.Replace("'", "''")}' AND LevelType = {(int)levelType} AND GroupType = {(int)groupType} AND IsModerated = 1";
            return GetDistinctValues<int>("LessonOrder", where);
        }

        // --- НОВЫЕ МЕТОДЫ GET ---
        public static List<int> GetSemesters(int grade, string subject, TaskLevelType levelType)
        {
            // Логика Перевернутых
            if (levelType == TaskLevelType.Inverted)
            {
                levelType = TaskLevelType.Regular;
            }

            string where = $"WHERE Grade = {grade} AND Subject = '{subject.Replace("'", "''")}' AND LevelType = {(int)levelType} AND GroupType IN ({(int)TaskGroupType.Demo}, {(int)TaskGroupType.ControlWork}) AND IsModerated = 1";
            return GetDistinctValues<int>("Semester", where);
        }

        public static List<int> GetTaskOrders(int grade, string subject, TaskLevelType levelType, TaskGroupType groupType, int? lessonOrder, int? semester)
        {
            string where;
            // Логика Перевернутых
            if (levelType == TaskLevelType.Inverted &&
               (groupType == TaskGroupType.ControlWork || groupType == TaskGroupType.Test || groupType == TaskGroupType.Demo))
            {
                levelType = TaskLevelType.Regular;
            }

            if (groupType == TaskGroupType.Demo)
            {
                where = $"WHERE Grade = {grade} AND Subject = '{subject.Replace("'", "''")}' AND LevelType = {(int)levelType} AND GroupType = {(int)groupType} AND Semester = {semester} AND IsModerated = 1";
            }
            else
            {
                where = $"WHERE Grade = {grade} AND Subject = '{subject.Replace("'", "''")}' AND LevelType = {(int)levelType} AND GroupType = {(int)groupType} AND LessonOrder = {lessonOrder} AND IsModerated = 1";
            }
            return GetDistinctValues<int>("TaskOrder", where);
        }

        public static List<int> GetVariants(int grade, string subject, TaskLevelType levelType, TaskGroupType groupType, int lessonOrder, int taskOrder)
        {
            // Логика Перевернутых
            if (levelType == TaskLevelType.Inverted)
            {
                levelType = TaskLevelType.Regular;
            }

            string where = $"WHERE Grade = {grade} AND Subject = '{subject.Replace("'", "''")}' AND LevelType = {(int)levelType} AND GroupType = {(int)groupType} AND LessonOrder = {lessonOrder} AND TaskOrder = {taskOrder} AND IsModerated = 1";
            return GetDistinctValues<int>("Variant", where);
        }

        private static List<T> GetDistinctValues<T>(string column, string whereClause = "")
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            string query = $"SELECT DISTINCT {column} FROM Tasks {whereClause} ORDER BY {column}";
            using var cmd = new SqliteCommand(query, conn);
            using var reader = cmd.ExecuteReader();

            var values = new List<T>();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue; // Пропускаем NULL значения

                if (typeof(T) == typeof(int))
                {
                    values.Add((T)(object)Convert.ToInt32(reader.GetInt64(0)));
                }
                else
                {
                    values.Add((T)reader[0]);
                }
            }
            return values;
        }

        public static void AddUser(long userId, string? firstName, string? username)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            string query = @"INSERT OR IGNORE INTO Users (UserId, FirstName, Username, FirstSeenAt)
                VALUES (@id, @first, @user, @time)";

            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@id", userId);
            cmd.Parameters.AddWithValue("@first", firstName ?? "");
            cmd.Parameters.AddWithValue("@user", username ?? "");
            cmd.Parameters.AddWithValue("@time", DateTime.Now.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        public static void UpdateLastRequest()
        {
            // ОПТИМИЗАЦИЯ: Запускаем асинхронно, не блокируя основной поток
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var conn = new SqliteConnection(ConnectionString);
                    conn.Open();
                    string query = "UPDATE Statistics SET LastTaskRequestedAt = @time WHERE Id = 1";
                    using var cmd = new SqliteCommand(query, conn);
                    cmd.Parameters.AddWithValue("@time", DateTime.Now.ToString("o"));
                    cmd.ExecuteNonQuery();
                }
                catch { }
            });
        }

        public static BotStatistics GetStatistics()
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            var stats = new BotStatistics();

            using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM Users", conn))
                stats.TotalUsers = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);

            using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM Tasks WHERE IsModerated = 1", conn))
                stats.TotalTasks = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);

            using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM Tasks WHERE IsModerated = 0", conn))
                stats.PendingTasks = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);

            using (var cmd = new SqliteCommand("SELECT MAX(CreatedAt) FROM Tasks", conn))
            {
                var result = cmd.ExecuteScalar()?.ToString();
                if (!string.IsNullOrEmpty(result) && DateTime.TryParse(result, out DateTime parsed))
                    stats.LastTaskAdded = parsed;
            }

            using (var cmd = new SqliteCommand("SELECT LastTaskRequestedAt FROM Statistics WHERE Id = 1", conn))
            {
                var result = cmd.ExecuteScalar()?.ToString();
                if (!string.IsNullOrEmpty(result) && DateTime.TryParse(result, out DateTime parsed))
                    stats.LastTaskRequested = parsed;
            }

            return stats;
        }

        public static List<FoxfordTask> GetTasksForModeration()
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            string query = $"SELECT * FROM Tasks WHERE IsModerated = 0 ORDER BY CreatedAt LIMIT 10";
            using var cmd = new SqliteCommand(query, conn);
            using var reader = cmd.ExecuteReader();

            var tasks = new List<FoxfordTask>();
            while (reader.Read())
            {
                tasks.Add(ReadTaskFromReader(reader));
            }
            return tasks;
        }

        public static bool ApproveTask(int taskId)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            string query = "UPDATE Tasks SET IsModerated = 1 WHERE Id = @id";
            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@id", taskId);
            return cmd.ExecuteNonQuery() > 0;
        }

        public static bool DeclineTask(int taskId)
        {
            return DeleteTask(taskId);
        }

        public static bool DeleteTask(int taskId)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            string getQuery = "SELECT ScreenshotPaths FROM Tasks WHERE Id = @id";
            using (var getCmd = new SqliteCommand(getQuery, conn))
            {
                getCmd.Parameters.AddWithValue("@id", taskId);
                var paths = getCmd.ExecuteScalar()?.ToString();

                if (!string.IsNullOrEmpty(paths))
                {
                    // ОПТИМИЗАЦИЯ: Удаляем файлы асинхронно
                    _ = System.Threading.Tasks.Task.Run(() =>
                    {
                        foreach (var path in paths.Split(','))
                        {
                            if (File.Exists(path))
                            {
                                try { File.Delete(path); } catch { }
                            }
                        }
                    });
                }
            }

            string query = "DELETE FROM Tasks WHERE Id = @id";
            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@id", taskId);
            return cmd.ExecuteNonQuery() > 0;
        }

        public static string ExportToJson()
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            using var cmd = new SqliteCommand("SELECT * FROM Tasks WHERE IsModerated = 1 ORDER BY Id", conn);
            using var reader = cmd.ExecuteReader();

            var tasks = new List<FoxfordTask>();
            while (reader.Read())
            {
                tasks.Add(ReadTaskFromReader(reader));
            }

            return JsonSerializer.Serialize(tasks, new JsonSerializerOptions { WriteIndented = true });
        }

        public static int ImportFromJson(string json)
        {
            var tasks = JsonSerializer.Deserialize<List<FoxfordTask>>(json);
            if (tasks == null) return 0;

            int count = 0;
            foreach (var task in tasks)
            {
                try
                {
                    task.IsModerated = true;
                    task.SubmittedByUserId = 0;
                    if (!CheckForDuplicate(task))
                    {
                        AddTask(task);
                        count++;
                    }
                }
                catch { }
            }
            return count;
        }

        public static int GetTotalTasksCount()
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var cmd = new SqliteCommand("SELECT COUNT(*) FROM Tasks WHERE IsModerated = 1", conn);
            var result = cmd.ExecuteScalar();
            return result != null ? Convert.ToInt32(result) : 0;
        }

        private static FoxfordTask ReadTaskFromReader(SqliteDataReader reader)
        {
            return new FoxfordTask
            {
                Id = Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("Id"))),
                LessonNumber = reader.GetString(reader.GetOrdinal("LessonNumber")),
                TaskNumber = reader.GetString(reader.GetOrdinal("TaskNumber")),
                Grade = Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("Grade"))),
                Subject = reader.GetString(reader.GetOrdinal("Subject")),
                LevelType = (TaskLevelType)Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("LevelType"))),
                GroupType = (TaskGroupType)Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("GroupType"))),

                LessonOrder = reader.IsDBNull(reader.GetOrdinal("LessonOrder")) ? null : Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("LessonOrder"))),
                TaskOrder = reader.IsDBNull(reader.GetOrdinal("TaskOrder")) ? null : Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("TaskOrder"))),
                Variant = reader.IsDBNull(reader.GetOrdinal("Variant")) ? null : Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("Variant"))),
                Semester = reader.IsDBNull(reader.GetOrdinal("Semester")) ? null : Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("Semester"))),

                ScreenshotPaths = reader.GetString(reader.GetOrdinal("ScreenshotPaths")),
                CreatedAt = DateTime.TryParse(reader.GetString(reader.GetOrdinal("CreatedAt")), out DateTime dt) ? dt : DateTime.MinValue,
                IsModerated = Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("IsModerated"))) == 1,
                SubmittedByUserId = reader.GetInt64(reader.GetOrdinal("SubmittedByUserId"))
            };
        }
    }
}