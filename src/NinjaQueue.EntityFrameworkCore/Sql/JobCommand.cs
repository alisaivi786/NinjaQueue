using System.Data;
using System.Data.Common;

namespace NinjaQueue.EntityFrameworkCore.Sql;

/// <summary>Small ADO.NET helpers: every parameter carries an explicit type, so a null never reaches a provider untyped.</summary>
internal static class JobCommand
{
    public static DbCommand Create(DbConnection connection, DbTransaction? transaction, string sql, int? commandTimeoutSeconds = null)
    {
        DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (commandTimeoutSeconds is { } timeout)
        {
            command.CommandTimeout = timeout;
        }

        return command;
    }

    public static DbCommand With(this DbCommand command, string name, object? value, DbType type)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value switch
        {
            null => DBNull.Value,
            DateTimeOffset dto => dto.ToUniversalTime(),
            _ => value,
        };
        command.Parameters.Add(parameter);
        return command;
    }

    /// <summary>Adds a parameter whose type the provider infers from the value (arrays on PostgreSQL).</summary>
    public static DbCommand WithInferred(this DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
        return command;
    }

    public static DbCommand Id(this DbCommand command, long id) => command.With("id", id, DbType.Int64);

    public static DbCommand Token(this DbCommand command, Guid token) => command.With("token", token, DbType.Guid);

    public static async Task<int> RunAsync(this DbCommand command, CancellationToken cancellationToken)
    {
        await using (command)
        {
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public static async Task<object?> ScalarAsync(this DbCommand command, CancellationToken cancellationToken)
    {
        await using (command)
        {
            return await command.ExecuteScalarAsync(cancellationToken);
        }
    }

    public static DateTimeOffset ReadUtc(this DbDataReader reader, int ordinal) => reader.GetValue(ordinal) switch
    {
        DateTimeOffset dto => dto.ToUniversalTime(),
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
        var other => throw new InvalidCastException($"Unexpected timestamp type {other.GetType().Name}."),
    };

    public static DateTimeOffset? ReadUtcOrNull(this DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.ReadUtc(ordinal);

    public static string? ReadStringOrNull(this DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static Guid? ReadGuidOrNull(this DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    public static int ReadInt(this DbDataReader reader, int ordinal) => Convert.ToInt32(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    public static long ReadLong(this DbDataReader reader, int ordinal) => Convert.ToInt64(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
}