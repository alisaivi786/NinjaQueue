namespace NinjaQueue.EntityFrameworkCore.Sql;

// The external operation ledger, job_side_effects (§15.5, Release 2). Executed on both providers by the integration suite.
internal abstract partial class JobSql
{
    /// <summary>Inserts the step as Started owned by @token; PostgreSQL returns a row only when it inserted.</summary>
    public abstract string InsertSideEffect { get; }

    /// <summary>SQL Server: the HOLDLOCK probe that serializes two inserts of one step. PostgreSQL: null (ON CONFLICT).</summary>
    public abstract string? SideEffectProbe { get; }

    public string SelectSideEffect => $"""
        SELECT status, owner_lease_token, issue_count, external_ref, result_json FROM {S}.job_side_effects
         WHERE job_id = @id AND step_key = @step_key
        """;

    /// <summary>Compare-and-set on the owner; returns the new issue_count when it wins.</summary>
    public abstract string SwapSideEffect { get; }

    /// <summary>Records a result while @token still owns the step (no lease check: a truthful late result is kept).</summary>
    public string RecordSideEffect => $"""
        UPDATE {S}.job_side_effects
           SET status = @status, external_ref = COALESCE(@external_ref, external_ref), result_json = COALESCE(@result_json, result_json),
               completed_at_utc = CASE WHEN @status = 2 THEN {Now} ELSE completed_at_utc END
         WHERE job_id = @id AND step_key = @step_key AND owner_lease_token = @token
        """;

    protected string SwapSet => $"""
           SET status = @status, owner_lease_token = @new_owner, issue_count = issue_count + @count,
               external_ref = COALESCE(@external_ref, external_ref), result_json = COALESCE(@result_json, result_json),
               started_at_utc = CASE WHEN @count = 1 THEN {Now} ELSE started_at_utc END,
               completed_at_utc = CASE WHEN @status = 2 THEN {Now} ELSE completed_at_utc END
        """;
}

internal sealed partial class PostgresJobSql
{
    public override string InsertSideEffect => $"""
        INSERT INTO {S}.job_side_effects (job_id, step_key, status, owner_lease_token, issue_count, started_at_utc)
        VALUES (@id, @step_key, 1, @token, 1, now())
        ON CONFLICT (job_id, step_key) DO NOTHING
        RETURNING issue_count
        """;

    public override string? SideEffectProbe => null;

    public override string SwapSideEffect => $"""
        UPDATE {S}.job_side_effects
        {SwapSet}
         WHERE job_id = @id AND step_key = @step_key AND owner_lease_token = @expected_owner
        RETURNING issue_count
        """;
}

internal sealed partial class SqlServerJobSql
{
    public override string InsertSideEffect => $"""
        INSERT INTO {S}.job_side_effects (job_id, step_key, status, owner_lease_token, issue_count, started_at_utc)
        OUTPUT inserted.issue_count
        VALUES (@id, @step_key, 1, @token, 1, SYSDATETIMEOFFSET())
        """;

    public override string? SideEffectProbe => $"""
        SELECT status, owner_lease_token, issue_count, external_ref, result_json FROM {S}.job_side_effects WITH (UPDLOCK, HOLDLOCK)
         WHERE job_id = @id AND step_key = @step_key
        """;

    public override string SwapSideEffect => $"""
        UPDATE {S}.job_side_effects WITH (ROWLOCK)
        {SwapSet}
        OUTPUT inserted.issue_count
         WHERE job_id = @id AND step_key = @step_key AND owner_lease_token = @expected_owner
        """;
}