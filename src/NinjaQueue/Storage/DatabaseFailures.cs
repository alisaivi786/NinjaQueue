using System.Data.Common;

namespace NinjaQueue.Storage;

/// <summary>Recognizes a database failure in the forms the providers raise it.</summary>
internal static class DatabaseFailures
{
    /// <summary>
    /// True for a <see cref="DbException"/>, and for the <see cref="InvalidOperationException"/> that Npgsql's execution
    /// strategy wraps a transient <see cref="DbException"/> in (SqlClient throws it as is).
    /// </summary>
    public static bool IsDatabaseFailure(Exception ex) => ex is DbException || ex is InvalidOperationException { InnerException: DbException };
}