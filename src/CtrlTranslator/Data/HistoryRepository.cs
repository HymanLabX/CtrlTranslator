using System.Globalization;
using CtrlTranslator.Diagnostics;
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

    public Task<TranslationRecord?> FindCacheAndTouchAsync(
        string cacheKey,
        CancellationToken cancellationToken) =>
        _database.RunAsync(() => FindCacheAndTouchAsyncCore(cacheKey, cancellationToken), cancellationToken);

    private TranslationRecord? FindCacheAndTouchAsyncCore(
        string cacheKey,
        CancellationToken cancellationToken)
    {
        using SqliteConnection connection = _database.CreateConnection();
        connection.Open();
        cancellationToken.ThrowIfCancellationRequested();

        TranslationRecord? record = null;
        using (SqliteCommand query = connection.CreateCommand())
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

            using SqliteDataReader reader = query.ExecuteReader();
            if (reader.Read())
            {
                record = ReadRecord(reader);
            }
        }

        if (record is null)
        {
            return null;
        }

        // 只更新"最后一次用到"的时间，不动 created_at。
        // created_at 是这条翻译真实发生的时刻，历史列表的排序、显示和 CSV 导出都以它为准；
        // 一旦被缓存命中改写，用户看到的时间就是错的，而且原值再也找不回来。
        using SqliteCommand touch = connection.CreateCommand();
        touch.CommandText = "UPDATE translations SET last_used_at = $last_used_at WHERE id = $id;";
        touch.Parameters.AddWithValue(
            "$last_used_at",
            DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
        touch.Parameters.AddWithValue("$id", record.Id);
        touch.ExecuteNonQuery();
        return record;
    }

    public Task<long> InsertAsync(
        TranslationRecord record,
        CancellationToken cancellationToken) =>
        _database.RunAsync(() => InsertAsyncCore(record, cancellationToken), cancellationToken);

    private long InsertAsyncCore(
        TranslationRecord record,
        CancellationToken cancellationToken)
    {
        using SqliteConnection connection = _database.CreateConnection();
        connection.Open();
        cancellationToken.ThrowIfCancellationRequested();

        using SqliteCommand command = connection.CreateCommand();
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
        object? result = command.ExecuteScalar();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// <summary>按编号取一条记录；不存在时返回 null。主窗口用它把刚翻译出来的那条插到列表顶部。</summary>
    public Task<TranslationRecord?> GetAsync(long id, CancellationToken cancellationToken) =>
        _database.RunAsync(() => GetAsyncCore(id, cancellationToken), cancellationToken);

    private TranslationRecord? GetAsyncCore(long id, CancellationToken cancellationToken)
    {
        using SqliteConnection connection = _database.CreateConnection();
        connection.Open();
        cancellationToken.ThrowIfCancellationRequested();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, cache_key, source_text, result_text, target_lang, mode_id,
                   model, is_word, starred, app_name, latency_ms, created_at,
                   profile_name, prompt_tokens, completion_tokens
              FROM translations
             WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    public Task SetStarredAsync(long id, bool starred, CancellationToken cancellationToken) =>
        _database.RunAsync(() => SetStarredAsyncCore(id, starred, cancellationToken), cancellationToken);

    private void SetStarredAsyncCore(long id, bool starred, CancellationToken cancellationToken)
    {
        using SqliteConnection connection = _database.CreateConnection();
        connection.Open();
        cancellationToken.ThrowIfCancellationRequested();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE translations SET starred = $starred WHERE id = $id;";
        command.Parameters.AddWithValue("$starred", starred ? 1 : 0);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public Task<IReadOnlyList<TranslationRecord>> QueryAsync(
        string keyword,
        bool starredOnly,
        int offset,
        int limit,
        CancellationToken cancellationToken) =>
        _database.RunAsync(() => QueryAsyncCore(keyword, starredOnly, offset, limit, cancellationToken), cancellationToken);

    private IReadOnlyList<TranslationRecord> QueryAsyncCore(
        string keyword,
        bool starredOnly,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        using SqliteConnection connection = _database.CreateConnection();
        connection.Open();
        cancellationToken.ThrowIfCancellationRequested();

        using SqliteCommand command = connection.CreateCommand();
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
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    public Task<IReadOnlyList<TranslationRecord>> GetAllAsync(CancellationToken cancellationToken) =>
        _database.RunAsync(() => GetAllAsyncCore(cancellationToken), cancellationToken);

    private IReadOnlyList<TranslationRecord> GetAllAsyncCore(CancellationToken cancellationToken)
    {
        using SqliteConnection connection = _database.CreateConnection();
        connection.Open();
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, cache_key, source_text, result_text, target_lang, mode_id,
                   model, is_word, starred, app_name, latency_ms, created_at,
                   profile_name, prompt_tokens, completion_tokens
              FROM translations
             ORDER BY created_at DESC;
            """;

        List<TranslationRecord> records = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    public Task DeleteAsync(long id, CancellationToken cancellationToken) =>
        _database.RunAsync(() => DeleteAsyncCore(id, cancellationToken), cancellationToken);

    private void DeleteAsyncCore(long id, CancellationToken cancellationToken)
    {
        using SqliteConnection connection = _database.CreateConnection();
        connection.Open();
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM translations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public Task RunReadWriteDiagnosticAsync(CancellationToken cancellationToken) =>
        _database.RunAsync(() => RunReadWriteDiagnosticAsyncCore(cancellationToken), cancellationToken);

    private void RunReadWriteDiagnosticAsyncCore(CancellationToken cancellationToken)
    {
        string marker = $"diagnostic-{Guid.NewGuid():N}";
        using SqliteConnection connection = _database.CreateConnection();
        connection.Open();
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteTransaction transaction = connection.BeginTransaction();

        using (SqliteCommand insert = connection.CreateCommand())
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
            insert.ExecuteNonQuery();
        }

        using (SqliteCommand read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT result_text FROM translations WHERE cache_key = $marker;";
            read.Parameters.AddWithValue("$marker", marker);
            object? value = read.ExecuteScalar();
            if (!string.Equals(value as string, "诊断", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("数据库写入后无法正确读回。");
            }
        }

        using (SqliteCommand delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM translations WHERE cache_key = $marker;";
            delete.Parameters.AddWithValue("$marker", marker);
            delete.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// 统计记录条数。SPEC §7.4 规定清空操作前必须告诉用户会删多少、保留多少。
    /// </summary>
    public Task<(int NonStarred, int Starred)> CountAsync(CancellationToken cancellationToken) =>
        _database.RunAsync(() => CountAsyncCore(cancellationToken), cancellationToken);

    private (int NonStarred, int Starred) CountAsyncCore(CancellationToken cancellationToken)
    {
        using SqliteConnection connection = _database.CreateConnection();
        connection.Open();
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT SUM(CASE WHEN starred = 0 THEN 1 ELSE 0 END), "
            + "SUM(CASE WHEN starred = 1 THEN 1 ELSE 0 END) FROM translations;";
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return (0, 0);
        }

        int nonStarred = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
        int starred = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        return (nonStarred, starred);
    }

    /// <summary>
    /// 只删除未收藏的记录。SPEC §7.4：星标收藏是用户攒的生词本，任何清理操作都不得删除。
    /// 刻意不提供批量删除收藏的入口，收藏只能在主窗口的记录卡片上逐条删除。
    /// </summary>
    public Task ClearNonStarredAsync(CancellationToken cancellationToken) =>
        _database.RunAsync(() => ClearNonStarredAsyncCore(cancellationToken), cancellationToken);

    private void ClearNonStarredAsyncCore(CancellationToken cancellationToken)
    {
        ExecuteDelete("DELETE FROM translations WHERE starred = 0;", cancellationToken);

        // SQLite 删记录只是把那几页标成"可复用"，原文译文的字节还躺在文件里，
        // 用工具打开 history.db 照样翻得出来。用户点「清空」就是要它没了，所以顺手压实一次。
        // 压实失败不影响"已删除"这个结果，只记日志。
        try
        {
            ExecuteDelete("VACUUM;", cancellationToken);
        }
        catch (SqliteException exception)
        {
            Logger.Warning("数据库", $"清空后压实数据库文件失败，记录已删除，不影响使用：{exception.Message}");
        }
    }

    /// <summary>
    /// 还能被缓存命中的记录有几条。清空缓存前报给用户。
    /// </summary>
    /// <remarks>
    /// 能命中的 key 是纯十六进制哈希；带 <c>#</c> 的都已经不会命中了
    /// （<c>#incomplete</c> 是被截断的译文，<c>#cleared</c> 是清过缓存的），不算在内。
    /// </remarks>
    public Task<int> CountCachedAsync(CancellationToken cancellationToken) =>
        _database.RunAsync(() => CountCachedAsyncCore(cancellationToken), cancellationToken);

    private int CountCachedAsyncCore(CancellationToken cancellationToken)
    {
        using SqliteConnection connection = _database.CreateConnection();
        connection.Open();
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM translations WHERE instr(cache_key, '#') = 0;";
        object? value = command.ExecuteScalar();
        return value is long count ? (int)count : 0;
    }

    /// <summary>
    /// 清空翻译缓存：让现有记录全部不再被缓存命中，<b>一条都不删</b>（负责人 2026-10 决定）。
    /// </summary>
    /// <remarks>
    /// 做法是给 cache_key 加后缀 <c>#cleared</c>，和 <c>#incomplete</c> 同一个思路：
    /// 查询按 key 全等匹配，加了后缀就再也对不上。不清成空串是为了留着原值好排查问题；
    /// 只改不带 <c>#</c> 的，反复清也不会把后缀越叠越长。
    /// <para>
    /// 以前「清空缓存」和「清空历史」删的是同一批记录，按钮旁却写着「清掉不会丢任何记录」。
    /// </para>
    /// </remarks>
    /// <returns>这次失效的记录条数。</returns>
    public Task<int> InvalidateCacheAsync(CancellationToken cancellationToken) =>
        _database.RunAsync(() => InvalidateCacheAsyncCore(cancellationToken), cancellationToken);

    private int InvalidateCacheAsyncCore(CancellationToken cancellationToken)
    {
        using SqliteConnection connection = _database.CreateConnection();
        connection.Open();
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE translations SET cache_key = cache_key || '#cleared' WHERE instr(cache_key, '#') = 0;";
        return command.ExecuteNonQuery();
    }

    private void ExecuteDelete(string commandText, CancellationToken cancellationToken)
    {
        using SqliteConnection connection = _database.CreateConnection();
        connection.Open();
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
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
            // 时间读不懂（比如被别的工具改过）就当作很早以前，别让一条坏记录把整页历史都读挂。
            CreatedAt = DateTime.TryParse(
                reader.GetString(11),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTime createdAt)
                ? createdAt
                : DateTime.MinValue,
            ProfileName = reader.IsDBNull(12) ? null : reader.GetString(12),
            PromptTokens = reader.IsDBNull(13) ? null : reader.GetInt64(13),
            CompletionTokens = reader.IsDBNull(14) ? null : reader.GetInt64(14)
        };
    }
}
