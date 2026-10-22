namespace NinjaQueue.IntegrationTests.Infrastructure;

/// <summary>
/// A provider-neutral description of a schema as the database reports it (information_schema / pg_catalog / sys.*).
/// Types are normalized to logical names (bigint, int, smallint, uuid, bool, timestamptz, varchar(n), text) so one pinned
/// manifest covers both providers. CHECK and filter definitions are kept as the server normalized them, which is
/// comparable between two databases of the same provider (EF-built versus migration-built).
/// </summary>
public sealed record SchemaSnapshot(
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> Indexes,
    IReadOnlyList<string> CheckNames,
    IReadOnlyList<string> CheckDefinitions,
    IReadOnlyList<string> IndexFilters,
    IReadOnlyList<string> ForeignKeys)
{
    public static async Task<SchemaSnapshot> ReadAsync(TestDatabase database, string schema)
    {
        ArgumentNullException.ThrowIfNull(database);
        SchemaSnapshot raw = database.IsPostgres ? await ReadPostgresAsync(database, schema) : await ReadSqlServerAsync(database, schema);

        // EF Core's migration history table (__ninjaqueue_migrations) lives in the schema but is not part of the job schema.
        static IReadOnlyList<string> JobTables(IReadOnlyList<string> entries) => [.. entries.Where(e => !e.StartsWith("__", StringComparison.Ordinal))];
        return new SchemaSnapshot(
            JobTables(raw.Columns), JobTables(raw.Indexes), JobTables(raw.CheckNames), JobTables(raw.CheckDefinitions), JobTables(raw.IndexFilters),
            JobTables(raw.ForeignKeys));
    }

    /// <summary>The tables in <paramref name="schema"/> (the migration history table included).</summary>
    public static async Task<List<string>> TablesAsync(TestDatabase database, string schema)
    {
        ArgumentNullException.ThrowIfNull(database);
        return [.. (await database.QueryAsync(database.IsPostgres
            ? $"SELECT table_name FROM information_schema.tables WHERE table_schema = '{schema}' ORDER BY table_name"
            : $"SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{schema}' ORDER BY TABLE_NAME")).Select(r => (string)r[0]!)];
    }

    public static async Task<bool> SchemaExistsAsync(TestDatabase database, string schema)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.ScalarAsync<int>(database.IsPostgres
            ? $"SELECT count(*) FROM information_schema.schemata WHERE schema_name = '{schema}'"
            : $"SELECT count(*) FROM sys.schemas WHERE name = '{schema}'") > 0;
    }

    private static async Task<SchemaSnapshot> ReadPostgresAsync(TestDatabase db, string schema)
    {
        List<string> columns = (await db.QueryAsync($"""
            SELECT table_name, column_name, data_type, character_maximum_length, is_nullable
              FROM information_schema.columns WHERE table_schema = '{schema}' ORDER BY table_name, column_name
            """)).Select(r => Column(r, PostgresType)).ToList();

        List<object?[]> indexRows = await db.QueryAsync($"""
            SELECT t.relname, i.relname, ix.indisunique, ix.indisprimary, pg_get_expr(ix.indpred, ix.indrelid),
                   a.attname, k.ord, (ix.indoption[k.ord - 1] & 1) = 1
              FROM pg_index ix
              JOIN pg_class i ON i.oid = ix.indexrelid
              JOIN pg_class t ON t.oid = ix.indrelid
              JOIN pg_namespace n ON n.oid = t.relnamespace
             CROSS JOIN LATERAL unnest(ix.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord)
              JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
             WHERE n.nspname = '{schema}'
             ORDER BY t.relname, i.relname, k.ord
            """);

        List<object?[]> checks = await db.QueryAsync($"""
            SELECT rel.relname, c.conname, pg_get_constraintdef(c.oid)
              FROM pg_constraint c JOIN pg_class rel ON rel.oid = c.conrelid JOIN pg_namespace n ON n.oid = rel.relnamespace
             WHERE n.nspname = '{schema}' AND c.contype = 'c'
            """);

        List<object?[]> fks = await db.QueryAsync($"""
            SELECT rel.relname, c.conname, ref.relname
              FROM pg_constraint c JOIN pg_class rel ON rel.oid = c.conrelid JOIN pg_class ref ON ref.oid = c.confrelid
              JOIN pg_namespace n ON n.oid = rel.relnamespace
             WHERE n.nspname = '{schema}' AND c.contype = 'f'
            """);

        return Build(columns, indexRows, checks, fks);
    }

    private static async Task<SchemaSnapshot> ReadSqlServerAsync(TestDatabase db, string schema)
    {
        List<string> columns = (await db.QueryAsync($"""
            SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE
              FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{schema}' ORDER BY TABLE_NAME, COLUMN_NAME
            """)).Select(r => Column(r, SqlServerType)).ToList();

        List<object?[]> indexRows = await db.QueryAsync($"""
            SELECT t.name, i.name, i.is_unique, i.is_primary_key, i.filter_definition, c.name, ic.key_ordinal, ic.is_descending_key
              FROM sys.indexes i
              JOIN sys.tables t ON t.object_id = i.object_id
              JOIN sys.schemas s ON s.schema_id = t.schema_id
              JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
             WHERE s.name = '{schema}'
             ORDER BY t.name, i.name, ic.key_ordinal
            """);

        List<object?[]> checks = await db.QueryAsync($"""
            SELECT t.name, cc.name, cc.definition
              FROM sys.check_constraints cc JOIN sys.tables t ON t.object_id = cc.parent_object_id JOIN sys.schemas s ON s.schema_id = t.schema_id
             WHERE s.name = '{schema}'
            """);

        List<object?[]> fks = await db.QueryAsync($"""
            SELECT t.name, fk.name, r.name
              FROM sys.foreign_keys fk JOIN sys.tables t ON t.object_id = fk.parent_object_id JOIN sys.tables r ON r.object_id = fk.referenced_object_id
              JOIN sys.schemas s ON s.schema_id = t.schema_id
             WHERE s.name = '{schema}'
            """);

        return Build(columns, indexRows, checks, fks);
    }

    private static SchemaSnapshot Build(List<string> columns, List<object?[]> indexRows, List<object?[]> checks, List<object?[]> fks)
    {
        List<string> indexes = [];
        List<string> filters = [];
        foreach (IGrouping<(string Table, string Name), object?[]> index in indexRows.GroupBy(r => ((string)r[0]!, (string)r[1]!)))
        {
            object?[] first = index.First();
            bool unique = Convert.ToBoolean(first[2], System.Globalization.CultureInfo.InvariantCulture);
            bool primary = Convert.ToBoolean(first[3], System.Globalization.CultureInfo.InvariantCulture);
            string? filter = first[4] as string;
            string keyColumns = string.Join(", ", index.OrderBy(r => Convert.ToInt32(r[6], System.Globalization.CultureInfo.InvariantCulture))
                .Select(r => (string)r[5]! + (Convert.ToBoolean(r[7], System.Globalization.CultureInfo.InvariantCulture) ? " DESC" : "")));
            string kind = primary ? "PK" : unique ? "UNIQUE" : "INDEX";
            indexes.Add($"{index.Key.Table}.{index.Key.Name} {kind} ({keyColumns}){(filter is null ? "" : " FILTERED")}");
            if (filter is not null)
            {
                filters.Add($"{index.Key.Name}: {filter}");
            }
        }

        return new SchemaSnapshot(
            columns.Order(StringComparer.Ordinal).ToList(),
            indexes.Order(StringComparer.Ordinal).ToList(),
            checks.Select(r => $"{r[0]}.{r[1]}").Order(StringComparer.Ordinal).ToList(),
            checks.Select(r => $"{r[1]}: {r[2]}").Order(StringComparer.Ordinal).ToList(),
            filters.Order(StringComparer.Ordinal).ToList(),
            fks.Select(r => $"{r[0]}.{r[1]} -> {r[2]}").Order(StringComparer.Ordinal).ToList());
    }

    private static string Column(object?[] r, Func<string, int?, string> type)
    {
        int? length = r[3] is null ? null : Convert.ToInt32(r[3], System.Globalization.CultureInfo.InvariantCulture);
        string nullability = (string)r[4]! == "YES" ? "null" : "not null";
        return $"{r[0]}.{r[1]} {type((string)r[2]!, length)} {nullability}";
    }

    private static string PostgresType(string dataType, int? length) => dataType switch
    {
        "bigint" => "bigint",
        "integer" => "int",
        "smallint" => "smallint",
        "uuid" => "uuid",
        "boolean" => "bool",
        "timestamp with time zone" => "timestamptz",
        "character varying" => $"varchar({length})",
        "text" => "text",
        _ => $"?{dataType}",
    };

    private static string SqlServerType(string dataType, int? length) => dataType switch
    {
        "bigint" => "bigint",
        "int" => "int",
        "smallint" => "smallint",
        "uniqueidentifier" => "uuid",
        "bit" => "bool",
        "datetimeoffset" => "timestamptz",
        "nvarchar" when length == -1 => "text",
        "nvarchar" => $"varchar({length})",
        _ => $"?{dataType}",
    };
}