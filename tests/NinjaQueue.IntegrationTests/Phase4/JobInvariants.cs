namespace NinjaQueue.IntegrationTests.Phase4;

/// <summary>
/// The invariant checker (§14.8, §16.5 G6, §17.2), run from a second connection after a scenario. Each check is a count of
/// violating rows; any non-zero count is a violation. Checks can be skipped by name where a scenario leaves a job
/// deliberately mid-flight (a lease left for recovery, a forced state conflict).
/// </summary>
public static class JobInvariants
{
    public const string QueueRowOnlyForNonTerminal = nameof(QueueRowOnlyForNonTerminal);
    public const string NonTerminalHasQueueRow = nameof(NonTerminalHasQueueRow);
    public const string AttemptWithinMax = nameof(AttemptWithinMax);
    public const string AttemptEqualsChargedRows = nameof(AttemptEqualsChargedRows);
    public const string AttemptsLeftConsistent = nameof(AttemptsLeftConsistent);
    public const string UnleasedHasAttemptsLeft = nameof(UnleasedHasAttemptsLeft);
    public const string AtMostOneSucceededAttempt = nameof(AtMostOneSucceededAttempt);
    public const string SucceededHasOneSucceededAttempt = nameof(SucceededHasOneSucceededAttempt);
    public const string OneActiveJobPerUniqueKey = nameof(OneActiveJobPerUniqueKey);
    public const string TerminalHoldsNoReservation = nameof(TerminalHoldsNoReservation);
    public const string ActiveUniqueJobHoldsReservation = nameof(ActiveUniqueJobHoldsReservation);
    public const string ProcessingIsLeased = nameof(ProcessingIsLeased);
    public const string OpenAttemptOnlyWhileProcessing = nameof(OpenAttemptOnlyWhileProcessing);

    public static async Task<List<string>> CheckAsync(TestDatabase db, params string[] skip)
    {
        ArgumentNullException.ThrowIfNull(db);
        string t = db.Bool(true);
        (string Name, string Sql)[] checks =
        [
            (QueueRowOnlyForNonTerminal, "SELECT count(*) FROM ninjaqueue.job_queue q JOIN ninjaqueue.background_jobs b ON b.id = q.job_id WHERE b.status >= 10"),
            (NonTerminalHasQueueRow, "SELECT count(*) FROM ninjaqueue.background_jobs b WHERE b.status IN (1, 2, 4) AND NOT EXISTS (SELECT 1 FROM ninjaqueue.job_queue q WHERE q.job_id = b.id)"),
            (AttemptWithinMax, "SELECT count(*) FROM ninjaqueue.background_jobs WHERE attempt < 0 OR attempt > max_attempts"),
            (AttemptEqualsChargedRows, $"SELECT count(*) FROM ninjaqueue.background_jobs b WHERE b.attempt <> (SELECT count(*) FROM ninjaqueue.job_attempts a WHERE a.job_id = b.id AND a.charged = {t})"),
            (AttemptsLeftConsistent, "SELECT count(*) FROM ninjaqueue.job_queue q JOIN ninjaqueue.background_jobs b ON b.id = q.job_id WHERE q.attempts_left <> b.max_attempts - b.attempt"),
            (UnleasedHasAttemptsLeft, "SELECT count(*) FROM ninjaqueue.job_queue WHERE lease_token IS NULL AND attempts_left < 1"),
            (AtMostOneSucceededAttempt, "SELECT count(*) FROM (SELECT job_id FROM ninjaqueue.job_attempts WHERE outcome = 1 GROUP BY job_id HAVING count(*) > 1) x"),
            (SucceededHasOneSucceededAttempt, "SELECT count(*) FROM ninjaqueue.background_jobs b WHERE b.status = 10 AND (SELECT count(*) FROM ninjaqueue.job_attempts a WHERE a.job_id = b.id AND a.outcome = 1) <> 1"),
            (OneActiveJobPerUniqueKey, """
                SELECT count(*) FROM (SELECT tenant_scope, unique_key FROM ninjaqueue.background_jobs WHERE status < 10 AND unique_key IS NOT NULL
                 GROUP BY tenant_scope, unique_key HAVING count(*) > 1) x
                """),
            (TerminalHoldsNoReservation, "SELECT count(*) FROM ninjaqueue.job_active_keys k JOIN ninjaqueue.background_jobs b ON b.id = k.job_id WHERE b.status >= 10"),
            (ActiveUniqueJobHoldsReservation, """
                SELECT count(*) FROM ninjaqueue.background_jobs b WHERE b.status < 10 AND b.unique_key IS NOT NULL
                   AND NOT EXISTS (SELECT 1 FROM ninjaqueue.job_active_keys k WHERE k.job_id = b.id AND k.unique_key = b.unique_key)
                """),
            (ProcessingIsLeased, """
                SELECT count(*) FROM ninjaqueue.job_queue q JOIN ninjaqueue.background_jobs b ON b.id = q.job_id
                 WHERE (b.status = 4 AND q.lease_token IS NULL) OR (b.status IN (1, 2) AND q.lease_token IS NOT NULL)
                """),
            (OpenAttemptOnlyWhileProcessing, "SELECT count(*) FROM ninjaqueue.job_attempts a JOIN ninjaqueue.background_jobs b ON b.id = a.job_id WHERE a.outcome IS NULL AND b.status <> 4"),
        ];

        List<string> violations = [];
        foreach ((string name, string sql) in checks.Where(c => !skip.Contains(c.Name)))
        {
            int count = await db.ScalarAsync<int>(sql);
            if (count != 0)
            {
                violations.Add($"{name}: {count} row(s)");
            }
        }

        return violations;
    }

    /// <summary>Asserts that every invariant holds.</summary>
    public static async Task AssertHoldAsync(TestDatabase db, params string[] skip)
        => (await CheckAsync(db, skip)).Should().BeEmpty("the §14.8 / §16.5 invariants hold after every scenario");
}