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
                       model, is_word, starred, app_name, latency_ms, created_at,
                       profile_name, prompt_tokens, completion_tokens
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
                is_word, starred, app_name, latency_ms, created_at,
                profile_name, prompt_tokens, completion_tokens)
            VALUES (
                $cache_key, $source_text, $result_text, $target_lang, $mode_id, $model,
                $is_word, $starred, $app_name, $latency_ms, $created_at,
                $profile_name, $prompt_tokens, $completion_tokens);
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

    public async Task<IReadOnlyList<TranslationRecord>> QueryAsync(
        string keyword,
        bool starredOnly,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, cache_key, source_text, result_text, target_lang, mode_id,
                   model, is_word, starred, app_name, latency_ms, created_at,
                   profile_name, prompt_tokens, completion_tokens
              FROM translations
             WHERE ($starred_only = 0 OR starred = 1)
               AND ($keyword = '' OR source_text LIKE $pattern ESCAPE '\' OR result_text LIKE $pattern ESCAPE '\')
             ORDER BY created_at DESC
             LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$starred_only", starredOnly ? 1 : 0);
        command.Parameters.AddWithValue("$keyword", keyword);
        command.Parameters.AddWithValue("$pattern", $"%{EscapeLikePattern(keyword)}%");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));

        List<TranslationRecord> records = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    public async Task<IReadOnlyList<TranslationRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, cache_key, source_text, result_text, target_lang, mode_id,
                   model, is_word, starred, app_name, latency_ms, created_at,
                   profile_name, prompt_tokens, completion_tokens
              FROM translations
             ORDER BY created_at DESC;
            """;

        List<TranslationRecord> records = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM translations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RunReadWriteDiagnosticAsync(CancellationToken cancellationToken)
    {
        string marker = $"diagnostic-{Guid.NewGuid():N}";
        await using SqliteConnection connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO translations
                    (cache_key, source_text, result_text, target_lang, mode_id, model,
                     is_word, starred, app_name, latency_ms, created_at)
                VALUES ($marker, $marker, '诊断', 'zh-CN', 'diagnostic', 'diagnostic',
                        0, 0, 'CtrlTranslator', 0, $created_at);
                """;
            insert.Parameters.AddWithValue("$marker", marker);
            insert.Parameters.AddWithValue("$created_at", DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (SqliteCommand read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT result_text FROM translations WHERE cache_key = $marker;";
            read.Parameters.AddWithValue("$marker", marker);
            object? value = await read.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(value as string, "诊断", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("数据库写入后无法正确读回。");
            }
        }

        await using (SqliteCommand delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM translations WHERE cache_key = $marker;";
            delete.Parameters.AddWithValue("$marker", marker);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// 统计记录条数。SPEC §7.4 规定清空操作前必须告诉用户会删多少、保留多少。
    /// </summary>
    public async Task<(int NonStarred, int Starred)> CountAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT SUM(CASE WHEN starred = 0 THEN 1 ELSE 0 END), "
            + "SUM(CASE WHEN starred = 1 THEN 1 ELSE 0 END) FROM translations;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0, 0);
        }

        int nonStarred = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
        int starred = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        return (nonStarred, starred);
    }

    /// <summary>
    /// 只删除未收藏的记录。SPEC §7.4：星标收藏是用户攒的生词本，任何清理操作都不得删除。
    /// v1.0 刻意不提供批量删除收藏的入口，收藏只能在主窗口逐条右键删除。
    /// </summary>
    public async Task ClearNonStarredAsync(CancellationToken cancellationToken)
    {
        await ExecuteDeleteAsync("DELETE FROM translations WHERE starred = 0;", cancellationToken);
    }

    private async Task ExecuteDeleteAsync(string commandText, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

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
        command.Parameters.AddWithValue("$profile_name", (object?)record.ProfileName ?? DBNull.Value);
        command.Parameters.AddWithValue("$prompt_tokens", (object?)record.PromptTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$completion_tokens", (object?)record.CompletionTokens ?? DBNull.Value);
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
                DateTimeStyles.RoundtripKind),
            ProfileName = reader.IsDBNull(12) ? null : reader.GetString(12),
            PromptTokens = reader.IsDBNull(13) ? null : reader.GetInt64(13),
            CompletionTokens = reader.IsDBNull(14) ? null : reader.GetInt64(14)
        };
    }
}
