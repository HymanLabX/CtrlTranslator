using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CtrlTranslator.Data;

/// <summary>
/// 直接使用参数化 SQL 管理缓存、历史与收藏状态。
/// </summary>
public sealed class HistoryRepository
{
    private readonly Database _database;

    public HistoryRepository(Database database)
    {
        _database = database;
    }

    public async Task<TranslationRecord?> FindCacheAndTouchAsync(
        string cacheKey,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        TranslationRecord? record = null;
        await using (SqliteCommand query = connection.CreateCommand())
        {
            query.CommandText = """
                SELECT id, cache_key, source_text, result_text, target_lang, mode_id,
                       model, is_word, starred, app_name, latency_ms, created_at
                  FROM translations
                 WHERE cache_key = $cache_key
                 ORDER BY created_at DESC
                 LIMIT 1;
                """;
            query.Parameters.AddWithValue("$cache_key", cacheKey);

            await using SqliteDataReader reader = await query.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                record = ReadRecord(reader);
            }
        }

        if (record is null)
        {
            return null;
        }

        record.CreatedAt = DateTime.Now;
        await using SqliteCommand touch = connection.CreateCommand();
        touch.CommandText = "UPDATE translations SET created_at = $created_at WHERE id = $id;";
        touch.Parameters.AddWithValue("$created_at", record.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        touch.Parameters.AddWithValue("$id", record.Id);
        await touch.ExecuteNonQueryAsync(cancellationToken);
        return record;
    }

    public async Task<long> InsertAsync(
        TranslationRecord record,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO translations (
                cache_key, source_text, result_text, target_lang, mode_id, model,
                is_word, starred, app_name, latency_ms, created_at)
            VALUES (
                $cache_key, $source_text, $result_text, $target_lang, $mode_id, $model,
                $is_word, $starred, $app_name, $latency_ms, $created_at);
            SELECT last_insert_rowid();
            """;
        AddRecordParameters(command, record);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async Task SetStarredAsync(long id, bool starred, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE translations SET starred = $starred WHERE id = $id;";
        command.Parameters.AddWithValue("$starred", starred ? 1 : 0);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddRecordParameters(SqliteCommand command, TranslationRecord record)
    {
        command.Parameters.AddWithValue("$cache_key", record.CacheKey);
        command.Parameters.AddWithValue("$source_text", record.SourceText);
        command.Parameters.AddWithValue("$result_text", record.ResultText);
        command.Parameters.AddWithValue("$target_lang", record.TargetLanguage);
        command.Parameters.AddWithValue("$mode_id", record.ModeId);
        command.Parameters.AddWithValue("$model", record.Model);
        command.Parameters.AddWithValue("$is_word", record.IsWord ? 1 : 0);
        command.Parameters.AddWithValue("$starred", record.Starred ? 1 : 0);
        command.Parameters.AddWithValue("$app_name", (object?)record.AppName ?? DBNull.Value);
        command.Parameters.AddWithValue("$latency_ms", (object?)record.LatencyMilliseconds ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$created_at",
            record.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private static TranslationRecord ReadRecord(SqliteDataReader reader)
    {
        return new TranslationRecord
        {
            Id = reader.GetInt64(0),
            CacheKey = reader.GetString(1),
            SourceText = reader.GetString(2),
            ResultText = reader.GetString(3),
            TargetLanguage = reader.GetString(4),
            ModeId = reader.GetString(5),
            Model = reader.GetString(6),
            IsWord = reader.GetInt64(7) != 0,
            Starred = reader.GetInt64(8) != 0,
            AppName = reader.IsDBNull(9) ? null : reader.GetString(9),
            LatencyMilliseconds = reader.IsDBNull(10) ? null : reader.GetInt64(10),
            CreatedAt = DateTime.Parse(
                reader.GetString(11),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind)
        };
    }
}
