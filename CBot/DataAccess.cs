using Microsoft.Data.Sqlite;
using DotNetEnv;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace CBot;

public class DataAccess
{
    private static string _databasePath;

    static DataAccess()
    {
        try
        {
            Console.WriteLine("[INFO] Инициализация пути к базе данных...");
            
            if (File.Exists(".env"))
            {
                Env.Load();
                Console.WriteLine("[INFO] Файл .env успешно загружен");
            }
            else
            {
                Console.WriteLine("[WARNING] Файл .env не найден, используются значения по умолчанию");
            }
            
            string? dbName = Environment.GetEnvironmentVariable("DATABASE") ?? "bot.db";
            Console.WriteLine($"[INFO] Имя базы данных: {dbName}");
            
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appFolder = Path.Combine(appDataPath, "CBot");
            
            Console.WriteLine($"[INFO] Папка приложения: {appFolder}");
            
            if (!Directory.Exists(appFolder))
            {
                Directory.CreateDirectory(appFolder);
                Console.WriteLine($"[INFO] Папка приложения создана: {appFolder}");
            }
            
            _databasePath = Path.Combine(appFolder, dbName);
            Console.WriteLine($"[INFO] Полный путь к БД: {_databasePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Ошибка при инициализации пути к БД: {ex.Message}");
            throw;
        }
    }

    private static SqliteConnection GetConnection()
    {
        return new SqliteConnection($"Filename={_databasePath}");
    }

    public static async Task InitializeDatabase()
    {
        Console.WriteLine("[INFO] Инициализация базы данных...");
        
        try
        {
            if (!File.Exists(_databasePath))
            {
                File.Create(_databasePath).Close();
                Console.WriteLine($"[INFO] Файл базы данных создан: {_databasePath}");
            }
            else
            {
                Console.WriteLine($"[INFO] Файл базы данных уже существует: {_databasePath}");
            }

            using (var db = GetConnection())
            {
                Console.WriteLine("[INFO] Подключение к базе данных...");
                await db.OpenAsync();
                Console.WriteLine("[INFO] Подключение к базе данных установлено");
                
                string tableCommand = @"
                    CREATE TABLE IF NOT EXISTS chatSettings (
                        chat_id INTEGER PRIMARY KEY NOT NULL,
                        delete_message INTEGER NOT NULL DEFAULT 0,
                        mute_minutes INTEGER NOT NULL DEFAULT 0,
                        vote_mute_limit INTEGER NOT NULL DEFAULT 0,
                        vote_ban_limit INTEGER NOT NULL DEFAULT 0
                    )";

                var createTable = new SqliteCommand(tableCommand, db);
                await createTable.ExecuteNonQueryAsync();
                Console.WriteLine("[INFO] Таблица chatSettings создана или уже существует");
            }
            
            Console.WriteLine("[INFO] Инициализация базы данных завершена успешно");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Ошибка при инициализации базы данных: {ex.Message}");
            throw;
        }
    }
    public static async Task AddOrUpdateChatSettings(long chatId, int deleteMessage = 0, 
        int muteMinutes = 0, int voteMuteLimit = 0, int voteBanLimit = 0)
    {
        Console.WriteLine($"[INFO] Добавление/обновление настроек для чата {chatId}");
        
        try
        {
            using (var db = GetConnection())
            {
                await db.OpenAsync();

                var command = new SqliteCommand();
                command.Connection = db;
                
                command.CommandText = @"
                    INSERT OR REPLACE INTO chatSettings 
                    (chat_id, delete_message, mute_minutes, vote_mute_limit, vote_ban_limit)
                    VALUES (@chatId, @deleteMessage, @muteMinutes, @voteMuteLimit, @voteBanLimit)";
                
                command.Parameters.AddWithValue("@chatId", chatId);
                command.Parameters.AddWithValue("@deleteMessage", deleteMessage);
                command.Parameters.AddWithValue("@muteMinutes", muteMinutes);
                command.Parameters.AddWithValue("@voteMuteLimit", voteMuteLimit);
                command.Parameters.AddWithValue("@voteBanLimit", voteBanLimit);

                int rowsAffected = await command.ExecuteNonQueryAsync();
                Console.WriteLine($"[INFO] Настройки чата {chatId} сохранены. Затронуто строк: {rowsAffected}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Ошибка при сохранении настроек для чата {chatId}: {ex.Message}");
            throw;
        }
    }
    
    public static async Task<Dictionary<string, object>> GetChatSettings(long chatId)
    {
        Console.WriteLine($"[INFO] Получение настроек для чата {chatId}");
        
        try
        {
            using (var db = GetConnection())
            {
                await db.OpenAsync();
                
                var command = new SqliteCommand(
                    "SELECT * FROM chatSettings WHERE chat_id = @chatId", db);
                command.Parameters.AddWithValue("@chatId", chatId);

                using (var reader = await command.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        var result = new Dictionary<string, object>
                        {
                            ["chat_id"] = reader.GetInt64(0),
                            ["delete_message"] = reader.GetInt32(1),
                            ["mute_minutes"] = reader.GetInt32(2),
                            ["vote_mute_limit"] = reader.GetInt32(3),
                            ["vote_ban_limit"] = reader.GetInt32(4)
                        };
                        
                        Console.WriteLine($"[INFO] Настройки для чата {chatId} найдены в БД");
                        return result;
                    }
                }
                
                Console.WriteLine($"[INFO] Настройки для чата {chatId} не найдены, возвращаются значения по умолчанию");
                return new Dictionary<string, object>
                {
                    ["chat_id"] = chatId,
                    ["delete_message"] = 0,
                    ["mute_minutes"] = 0,
                    ["vote_mute_limit"] = 0,
                    ["vote_ban_limit"] = 0
                };
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Ошибка при получении настроек для чата {chatId}: {ex.Message}");
            throw;
        }
    }
    
    public static async Task<List<Dictionary<string, object>>> GetAllChatSettings()
    {
        Console.WriteLine("[INFO] Получение всех настроек чатов");
        
        var results = new List<Dictionary<string, object>>();
        
        try
        {
            using (var db = GetConnection())
            {
                await db.OpenAsync();
                
                var command = new SqliteCommand("SELECT * FROM chatSettings", db);

                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var result = new Dictionary<string, object>
                        {
                            ["chat_id"] = reader.GetInt64(0),
                            ["delete_message"] = reader.GetInt32(1),
                            ["mute_minutes"] = reader.GetInt32(2),
                            ["vote_mute_limit"] = reader.GetInt32(3),
                            ["vote_ban_limit"] = reader.GetInt32(4)
                        };
                        
                        results.Add(result);
                    }
                }
                
                Console.WriteLine($"[INFO] Получено {results.Count} записей настроек чатов");
                return results;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Ошибка при получении всех настроек чатов: {ex.Message}");
            throw;
        }
    }
}