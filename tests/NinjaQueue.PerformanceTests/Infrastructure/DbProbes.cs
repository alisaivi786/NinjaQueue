namespace NinjaQueue.PerformanceTests.Infrastructure;

/// <summary>Server-side measurements: sessions, deadlocks, lock escalation, lock waits and query plans, per provider.</summary>
public static class DbProbes
{
    /// <summary>Sessions this database currently has open for the application name (idle pooled connections included).</summary>
    public static Task<int> ConnectionsAsync(PerfDatabase db, string applicationName)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.ScalarAsync<int>(db.IsPostgres
            ? $"SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND application_name = '{applicationName}'"
            : $"SELECT count(*) FROM sys.dm_exec_sessions WHERE database_id = DB_ID() AND program_name = '{applicationName}'");
    }

    /// <summary>Cumulative deadlocks: this database (PostgreSQL <c>pg_stat_database</c>) or the instance (SQL Server counter).</summary>
    public static Task<long> DeadlocksAsync(PerfDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.ScalarAsync<long>(db.IsPostgres
            ? "SELECT deadlocks FROM pg_stat_database WHERE datname = current_database()"
            : "SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name LIKE 'Number of Deadlocks/sec%' AND instance_name = '_Total'");
    }

    /// <summary>SQL Server: lock escalations (row/page to table) on the jobs tables since the database started. PostgreSQL has none.</summary>
    public static Task<long> LockEscalationsAsync(PerfDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.IsPostgres
            ? Task.FromResult(0L)
            : db.ScalarAsync<long>("""
                SELECT COALESCE(SUM(s.index_lock_promotion_count), 0)
                  FROM sys.dm_db_index_operational_stats(DB_ID(), NULL, NULL, NULL) s
                  JOIN sys.objects o ON o.object_id = s.object_id
                 WHERE SCHEMA_NAME(o.schema_id) = 'jobs'
                """);
    }

    /// <summary>SQL Server: escalations per jobs table and index since the database started ("table.index=n", comma separated).</summary>
    public static async Task<string> LockEscalationDetailAsync(PerfDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.IsPostgres)
        {
            return "";
        }

        List<object?[]> rows = await db.QueryAsync("""
            SELECT o.name, COALESCE(i.name, 'heap'), SUM(s.index_lock_promotion_attempt_count), SUM(s.index_lock_promotion_count)
              FROM sys.dm_db_index_operational_stats(DB_ID(), NULL, NULL, NULL) s
              JOIN sys.objects o ON o.object_id = s.object_id
              LEFT JOIN sys.indexes i ON i.object_id = s.object_id AND i.index_id = s.index_id
             WHERE SCHEMA_NAME(o.schema_id) = 'jobs' AND s.index_lock_promotion_attempt_count > 0
             GROUP BY o.name, i.name
            """);
        return string.Join(", ", rows.Select(r => $"{r[0]}.{r[1]} attempts={r[2]} escalated={r[3]}"));
    }

    /// <summary>
    /// One sample: (lock requests waiting, table-level locks on the data of the jobs tables). SQL Server object locks with a
    /// subtype are metadata locks, not data locks: auto-update statistics holds OBJECT X/UPDSTATS on a busy table while it
    /// rebuilds a statistic, and that blocks no row access, so it is not counted.
    /// </summary>
    public static async Task<(int Waiting, int TableLocks)> SampleLocksAsync(PerfDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        List<object?[]> rows = await db.QueryAsync(db.IsPostgres
            ? """
              SELECT (SELECT count(*) FROM pg_locks l JOIN pg_database d ON d.oid = l.database WHERE d.datname = current_database() AND NOT l.granted),
                     (SELECT count(*) FROM pg_locks l JOIN pg_class c ON c.oid = l.relation JOIN pg_namespace n ON n.oid = c.relnamespace
                       WHERE l.locktype = 'relation' AND n.nspname = 'jobs' AND l.granted AND l.mode IN ('ExclusiveLock', 'AccessExclusiveLock', 'ShareLock', 'ShareRowExclusiveLock'))
              """
            : """
              SELECT (SELECT count(*) FROM sys.dm_tran_locks WHERE resource_database_id = DB_ID() AND request_status = 'WAIT'),
                     (SELECT count(*) FROM sys.dm_tran_locks l JOIN sys.objects o ON o.object_id = l.resource_associated_entity_id
                       WHERE l.resource_database_id = DB_ID() AND l.resource_type = 'OBJECT' AND SCHEMA_NAME(o.schema_id) = 'jobs'
                         AND l.resource_subtype = '' AND l.request_mode IN ('X', 'S', 'SIX', 'U') AND l.request_status = 'GRANT')
              """);
        return (Convert.ToInt32(rows[0][0], CultureInfo.InvariantCulture), Convert.ToInt32(rows[0][1], CultureInfo.InvariantCulture));
    }

    /// <summary>SQL Server: the table-level locks on the jobs tables right now ("table mode by statement", "; " separated).</summary>
    public static async Task<string> TableLockDetailAsync(PerfDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.IsPostgres)
        {
            List<object?[]> pg = await db.QueryAsync("""
                SELECT c.relname, l.mode, left(coalesce(a.query, ''), 200) FROM pg_locks l JOIN pg_class c ON c.oid = l.relation
                  JOIN pg_namespace n ON n.oid = c.relnamespace LEFT JOIN pg_stat_activity a ON a.pid = l.pid
                 WHERE l.locktype = 'relation' AND n.nspname = 'jobs' AND l.granted
                   AND l.mode IN ('ExclusiveLock', 'AccessExclusiveLock', 'ShareLock', 'ShareRowExclusiveLock')
                """);
            return string.Join("; ", pg.Select(r => $"{r[0]} {r[1]} by {r[2]}"));
        }

        List<object?[]> rows = await db.QueryAsync("""
            SELECT o.name, l.request_mode + CASE WHEN l.resource_subtype <> '' THEN '/' + l.resource_subtype ELSE '' END,
                   LEFT(COALESCE(SUBSTRING(t.text, r.statement_start_offset / 2 + 1,
                        (CASE r.statement_end_offset WHEN -1 THEN DATALENGTH(t.text) ELSE r.statement_end_offset END - r.statement_start_offset) / 2 + 1), ''), 300)
              FROM sys.dm_tran_locks l JOIN sys.objects o ON o.object_id = l.resource_associated_entity_id
              LEFT JOIN sys.dm_exec_requests r ON r.session_id = l.request_session_id
              OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
             WHERE l.resource_database_id = DB_ID() AND l.resource_type = 'OBJECT' AND SCHEMA_NAME(o.schema_id) = 'jobs'
               AND l.request_mode IN ('X', 'S', 'SIX', 'U') AND l.request_status = 'GRANT'
            """);
        return string.Join("; ", rows.Select(r => $"{r[0]} {r[1]} by {System.Text.RegularExpressions.Regex.Replace((string?)r[2] ?? "", @"\s+", " ")}"));
    }

    /// <summary>PostgreSQL durability settings of the test container, reported next to the numbers.</summary>
    public static async Task<string> ServerSettingsAsync(PerfDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.IsPostgres)
        {
            List<object?[]> rows = await db.QueryAsync("SELECT name, setting FROM pg_settings WHERE name IN ('fsync', 'synchronous_commit', 'shared_buffers', 'max_connections') ORDER BY name");
            return string.Join(", ", rows.Select(r => $"{r[0]}={r[1]}"));
        }

        List<object?[]> info = await db.QueryAsync("""
            SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)), CAST(SERVERPROPERTY('Edition') AS nvarchar(100)),
                   (SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID()),
                   (SELECT compatibility_level FROM sys.databases WHERE database_id = DB_ID())
            """);
        return $"version={info[0][0]}, edition={info[0][1]}, rcsi={info[0][2]}, compat={info[0][3]}";
    }

    /// <summary>
    /// The claim statement's plan. PostgreSQL: <c>EXPLAIN (ANALYZE, BUFFERS)</c> of the claim's locking SELECT (the CTE of
    /// <c>JobSql.Claim</c>: one LATERAL seek per job type, same predicates and order) inside a rolled-back transaction. SQL Server: the cached actual plan of
    /// the real claim statement (matched on its text) with its execution statistics from <c>sys.dm_exec_query_stats</c>.
    /// </summary>
    public static async Task<string> ClaimPlanAsync(PerfDatabase db, string queue, string jobType)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.IsPostgres)
        {
            await using DbConnection connection = await db.OpenAsync();
            await using DbTransaction transaction = await connection.BeginTransactionAsync();
            await using DbCommand explain = connection.CreateCommand();
            explain.Transaction = transaction;
            explain.CommandText = $"""
                EXPLAIN (ANALYZE, BUFFERS)
                SELECT x.job_id
                  FROM unnest(ARRAY['{jobType}']) AS t(job_type)
                  CROSS JOIN LATERAL (
                    SELECT q.job_id, q.available_at_utc FROM ninjaqueue.job_queue q
                     WHERE q.queue = '{queue}' AND q.job_type = t.job_type AND q.lease_token IS NULL AND q.available_at_utc <= now()
                       AND q.concurrency_key IS NULL AND q.attempts_left >= 1
                     ORDER BY q.available_at_utc, q.job_id
                     LIMIT 10
                     FOR UPDATE SKIP LOCKED) AS x
                 ORDER BY x.available_at_utc, x.job_id
                 LIMIT 10
                """;
            List<string> lines = [];
            await using (DbDataReader reader = await explain.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    lines.Add(reader.GetString(0));
                }
            }

            await transaction.RollbackAsync();
            return string.Join(Environment.NewLine, lines);
        }

        List<object?[]> rows = await db.QueryAsync("""
            SELECT TOP (1) qs.execution_count, qs.total_elapsed_time / qs.execution_count, qs.max_elapsed_time,
                   qs.total_logical_reads / qs.execution_count, CAST(qp.query_plan AS nvarchar(max))
              FROM sys.dm_exec_query_stats qs
             CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
             CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
             CROSS APPLY sys.dm_exec_plan_attributes(qs.plan_handle) pa
             WHERE pa.attribute = 'dbid' AND CONVERT(int, pa.value) = DB_ID()
               AND st.text LIKE '%OPENJSON(@types)%' AND st.text LIKE '%job_queue%READPAST%'
             ORDER BY qs.execution_count DESC
            """);
        if (rows.Count == 0)
        {
            return "(claim plan not in cache)";
        }

        object?[] r = rows[0];
        return string.Create(CultureInfo.InvariantCulture,
            $"executions={r[0]} avg_elapsed_us={r[1]} max_elapsed_us={r[2]} avg_logical_reads={r[3]}{Environment.NewLine}{r[4]}");
    }

    /// <summary>The index names a plan text references among the job_queue indexes.</summary>
    public static IReadOnlyList<string> QueueIndexesIn(string plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return [.. ((string[])["ix_job_queue_claim_type", "ix_job_queue_lease_expiry", "pk_job_queue"]).Where(i => plan.Contains(i, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>A short summary of a plan (the PostgreSQL text as-is; the SQL Server statistics line and physical operators).</summary>
    public static string Summarize(PerfDatabase db, string plan)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(plan);
        if (db.IsPostgres)
        {
            return plan;
        }

        string firstLine = plan.Split(Environment.NewLine)[0];
        IEnumerable<string> ops = System.Text.RegularExpressions.Regex.Matches(plan, "PhysicalOp=\"([^\"]+)\"").Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal);
        IEnumerable<string> indexes = System.Text.RegularExpressions.Regex.Matches(plan, "Index=\"\\[([^\\]]+)\\]\"").Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal);
        return $"{firstLine}; operators: {string.Join(", ", ops)}; indexes: {string.Join(", ", indexes)}";
    }
}