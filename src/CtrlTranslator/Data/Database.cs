using Microsoft.Data.Sqlite;
using CtrlTranslator.Diagnostics;
using System.IO;

namespace CtrlTranslator.Data;

/// <summary>
/// 管理 history.db 的连接与建表，不在内存中长期持有连接。
/// </summary>
public sealed class Database
{
    public Database()
    {
        string path = Path.Combine(Logger.DataDirectory, "history.db");
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        ConnectionString = builder.ToString();
    }

    public string ConnectionString { get; }

    public void Initialize()
    {
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = SchemaSql;
            command.ExecuteNonQuery();
        }

        AddMissingColumns(connection);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        AddMissingColumns(connection);
    }

    /// <summary>
    /// 给老库补上 M5 新增的列。SQLite 的 <c>ALTER TABLE ADD COLUMN</c> 没有 IF NOT EXISTS，
    /// 只能先用 <c>PRAGMA table_info</c> 查一遍现有列名再决定加哪些。
    /// </summary>
    private static void AddMissingColumns(SqliteConnection connection)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (SqliteCommand info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(translations);";
            using SqliteDataReader reader = info.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(1));
            }
        }

        (string Name, string Definition)[] added =
        [
            ("profile_name", "TEXT"),
            ("prompt_tokens", "INTEGER"),
            ("completion_tokens", "INTEGER"),
            // 缓存命中的时间必须和翻译发生的时间分开记。以前命中缓存会直接改写
            // created_at，导致历史列表的时间、排序和导出的 CSV 全变成"最后一次命中"的时间，
            // 三个月前翻的东西今天再划一次就显示成今天，真实时间被永久覆盖。
            ("last_used_at", "TEXT")
        ];

        foreach ((string name, string definition) in added)
        {
            if (existing.Contains(name))
            {
                continue;
            }

            using SqliteCommand alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE translations ADD COLUMN {name} {definition};";
            alter.ExecuteNonQuery();
            Logger.Info("数据库", $"历史表已补充列 {name}。");
        }
    }

    private const string SchemaSql = """
            PRAGMA busy_timeout = 3000;

            CREATE TABLE IF NOT EXISTS translations (
              id           INTEGER PRIMARY KEY AUTOINCREMENT,
              cache_key    TEXT    NOT NULL,
              source_text  TEXT    NOT NULL,
              result_text  TEXT    NOT NULL,
              target_lang  TEXT    NOT NULL,
              mode_id      TEXT    NOT NULL,
              model        TEXT    NOT NULL,
              is_word      INTEGER NOT NULL DEFAULT 0,
              starred      INTEGER NOT NULL DEFAULT 0,
              app_name     TEXT,
              latency_ms   INTEGER,
              created_at   TEXT    NOT NULL,
              profile_name      TEXT,
              prompt_tokens     INTEGER,
              completion_tokens INTEGER
            );

            CREATE INDEX IF NOT EXISTS idx_cache_key ON translations(cache_key);
            CREATE INDEX IF NOT EXISTS idx_starred   ON translations(starred, created_at DESC);
            CREATE INDEX IF NOT EXISTS idx_created   ON translations(created_at DESC);
            """;

    internal SqliteConnection CreateConnection() => new(ConnectionString);
}
