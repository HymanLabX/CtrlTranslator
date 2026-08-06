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
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        command.ExecuteNonQuery();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
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
              created_at   TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_cache_key ON translations(cache_key);
            CREATE INDEX IF NOT EXISTS idx_starred   ON translations(starred, created_at DESC);
            CREATE INDEX IF NOT EXISTS idx_created   ON translations(created_at DESC);
            """;

    internal SqliteConnection CreateConnection() => new(ConnectionString);
}
