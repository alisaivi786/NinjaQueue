namespace NinjaQueue.IntegrationTests.Schema;

/// <summary>
/// Phase 2 exit, "constraint tests pass": the database itself enforces the invariants the engine relies on (§17.2 attempt
/// invariant, §14.5 tenant scope, §15.3 reservations, lease consistency and the persisted value domains), on PostgreSQL
/// and SQL Server RCSI ON and OFF. Each case shows the valid shape is accepted and the invalid one rejected.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Gate", "G13")]
public sealed class SchemaConstraintTests(JobsDatabaseFixture fixture)
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Background_job_domains_are_enforced(DatabaseKind kind)
    {
        TestDatabase db = await MigratedAsync(kind);

        await InsertJobAsync(db);                                                           // valid
        await Rejected(() => InsertJobAsync(db, status: 5));                                // reserved, not a state
        await Rejected(() => InsertJobAsync(db, status: 13));
        await Rejected(() => InsertJobAsync(db, maxAttempts: 0));
        await Rejected(() => InsertJobAsync(db, maxAttempts: 1001));                        // §17.4 persisted ceiling
        await InsertJobAsync(db, maxAttempts: 1000, attempt: 1000);
        await Rejected(() => InsertJobAsync(db, maxAttempts: 5, attempt: 6));               // attempt <= max_attempts
        await Rejected(() => InsertJobAsync(db, attempt: -1));
        await Rejected(() => InsertJobAsync(db, continuationPolicy: 3));
        await Rejected(() => InsertJobAsync(db, terminalReason: 9));
        await InsertJobAsync(db, status: 12, terminalReason: 2);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Tenant_scope_must_match_the_tenant(DatabaseKind kind)
    {
        TestDatabase db = await MigratedAsync(kind);

        await InsertJobAsync(db, tenantId: TenantA, tenantScope: TenantA);
        await InsertJobAsync(db, tenantId: null, tenantScope: Guid.Empty);                  // system job
        await Rejected(() => InsertJobAsync(db, tenantId: TenantA, tenantScope: TenantB));
        await Rejected(() => InsertJobAsync(db, tenantId: TenantA, tenantScope: Guid.Empty));
        await Rejected(() => InsertJobAsync(db, tenantId: null, tenantScope: TenantA));
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Exhausted_queue_row_cannot_be_claimable(DatabaseKind kind)
    {
        TestDatabase db = await MigratedAsync(kind);

        await InsertQueueAsync(db, await InsertJobAsync(db), attemptsLeft: 1, leased: false);
        await InsertQueueAsync(db, await InsertJobAsync(db), attemptsLeft: 0, leased: true); // final attempt running (§17.2)
        await Rejected(async () => await InsertQueueAsync(db, await InsertJobAsync(db), attemptsLeft: 0, leased: false));
        await Rejected(async () => await InsertQueueAsync(db, await InsertJobAsync(db), attemptsLeft: -1, leased: true));

        // Clearing the lease of an exhausted row (rescheduling instead of dead-lettering) fails on the CHECK.
        long running = await InsertJobAsync(db);
        await InsertQueueAsync(db, running, attemptsLeft: 0, leased: true);
        await Rejected(() => db.ExecuteAsync(
            $"UPDATE ninjaqueue.job_queue SET lease_token = NULL, lease_owner = NULL, lease_expires_at_utc = NULL WHERE job_id = {running}"));
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Lease_columns_are_set_and_cleared_together(DatabaseKind kind)
    {
        TestDatabase db = await MigratedAsync(kind);
        long job = await InsertJobAsync(db);
        await InsertQueueAsync(db, job, attemptsLeft: 3, leased: true);

        await Rejected(() => db.ExecuteAsync($"UPDATE ninjaqueue.job_queue SET lease_owner = NULL WHERE job_id = {job}"));
        await Rejected(() => db.ExecuteAsync($"UPDATE ninjaqueue.job_queue SET lease_expires_at_utc = NULL WHERE job_id = {job}"));
        (await db.ExecuteAsync(
            $"UPDATE ninjaqueue.job_queue SET lease_token = NULL, lease_owner = NULL, lease_expires_at_utc = NULL WHERE job_id = {job}")).Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Idempotency_key_is_unique_per_tenant_scope_and_job_type(DatabaseKind kind)
    {
        TestDatabase db = await MigratedAsync(kind);

        await InsertJobAsync(db, tenantId: TenantA, tenantScope: TenantA, idempotencyKey: "k1");
        await Rejected(() => InsertJobAsync(db, tenantId: TenantA, tenantScope: TenantA, idempotencyKey: "k1"));
        await InsertJobAsync(db, tenantId: TenantB, tenantScope: TenantB, idempotencyKey: "k1");                 // other tenant
        await InsertJobAsync(db, tenantId: TenantA, tenantScope: TenantA, idempotencyKey: "k1", jobType: "Other.v1"); // other type
        await InsertJobAsync(db, tenantId: TenantA, tenantScope: TenantA);                                        // no key
        await InsertJobAsync(db, tenantId: TenantA, tenantScope: TenantA);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task At_most_one_running_job_per_concurrency_key(DatabaseKind kind)
    {
        TestDatabase db = await MigratedAsync(kind);

        await InsertQueueAsync(db, await InsertJobAsync(db, tenantId: TenantA, tenantScope: TenantA), 3, leased: true, TenantA, "channel:1");
        await InsertQueueAsync(db, await InsertJobAsync(db, tenantId: TenantA, tenantScope: TenantA), 3, leased: false, TenantA, "channel:1"); // waiting
        await InsertQueueAsync(db, await InsertJobAsync(db, tenantId: TenantB, tenantScope: TenantB), 3, leased: true, TenantB, "channel:1"); // other tenant
        await Rejected(async () => await InsertQueueAsync(db, await InsertJobAsync(db, tenantId: TenantA, tenantScope: TenantA), 3, leased: true, TenantA, "channel:1"));
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Unique_key_reservation_is_one_job_per_tenant_scope_and_key(DatabaseKind kind)
    {
        TestDatabase db = await MigratedAsync(kind);
        long first = await InsertJobAsync(db, tenantId: TenantA, tenantScope: TenantA);
        long second = await InsertJobAsync(db, tenantId: TenantA, tenantScope: TenantA);
        long other = await InsertJobAsync(db, tenantId: TenantB, tenantScope: TenantB);

        await Reserve(db, TenantA, "company:provision", first);
        await Rejected(() => Reserve(db, TenantA, "company:provision", second));
        await Reserve(db, TenantB, "company:provision", other);
        await Rejected(() => Reserve(db, TenantA, "company:other", first));              // one reservation per job
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Child_rows_need_their_job_and_values_stay_in_range(DatabaseKind kind)
    {
        TestDatabase db = await MigratedAsync(kind);
        long job = await InsertJobAsync(db);

        await Rejected(() => InsertQueueAsync(db, 999_999, attemptsLeft: 1, leased: false));
        await Rejected(() => db.ExecuteAsync(
            $"INSERT INTO ninjaqueue.job_events (job_id, event_code, occurred_at_utc) VALUES (999999, 'JobEnqueued', {db.Now})"));

        string attempt = $"INSERT INTO ninjaqueue.job_attempts (job_id, attempt_seq, charged, worker_id, lease_token, started_at_utc, outcome) VALUES ({job}, {{0}}, {db.Bool(true)}, 'w1', '{Guid.NewGuid()}', {db.Now}, {{1}})";
        await db.ExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, attempt, 1, "NULL"));
        await db.ExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, attempt, 2, 8));
        await Rejected(() => db.ExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, attempt, 3, 9)));
        await Rejected(() => db.ExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, attempt, 0, 1)));
        await Rejected(() => db.ExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, attempt, 1, 1))); // duplicate seq

        string sideEffect = $"INSERT INTO ninjaqueue.job_side_effects (job_id, step_key, status, owner_lease_token, issue_count, started_at_utc) VALUES ({job}, '{{0}}', {{1}}, '{Guid.NewGuid()}', {{2}}, {db.Now})";
        await db.ExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, sideEffect, "send", 1, 1));
        await Rejected(() => db.ExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, sideEffect, "send", 1, 1)));
        await Rejected(() => db.ExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, sideEffect, "s2", 5, 1)));
        await Rejected(() => db.ExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, sideEffect, "s3", 1, 0)));
    }

    private async Task<TestDatabase> MigratedAsync(DatabaseKind kind)
    {
        TestDatabase db = await fixture.CreateDatabaseAsync(kind);
        db.MigrateUp();
        return db;
    }

    private static async Task<long> InsertJobAsync(
        TestDatabase db, int status = 1, int maxAttempts = 5, int attempt = 0, int continuationPolicy = 1, int? terminalReason = null,
        Guid? tenantId = null, Guid? tenantScope = null, string? idempotencyKey = null, string jobType = "Test.Job.v1")
    {
        string tenant = tenantId is { } t ? $"'{t}'" : "NULL";
        string scope = $"'{tenantScope ?? Guid.Empty}'";
        string key = idempotencyKey is null ? "NULL" : $"'{idempotencyKey}'";
        string reason = terminalReason?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
        string columns = "guid_id, job_type, queue, tenant_id, tenant_scope, status, idempotency_key, max_attempts, attempt, snooze_count, release_count, run_at_utc, continuation_policy, terminal_reason_code, created_at_utc, updated_at_utc";
        string values = $"'{Guid.NewGuid()}', '{jobType}', 'default', {tenant}, {scope}, {status}, {key}, {maxAttempts}, {attempt}, 0, 0, {db.Now}, {continuationPolicy}, {reason}, {db.Now}, {db.Now}";
        string sql = db.IsPostgres
            ? $"INSERT INTO ninjaqueue.background_jobs ({columns}) VALUES ({values}) RETURNING id"
            : $"INSERT INTO ninjaqueue.background_jobs ({columns}) OUTPUT inserted.id VALUES ({values})";
        return await db.ScalarAsync<long>(sql);
    }

    private static Task<int> InsertQueueAsync(
        TestDatabase db, long jobId, int attemptsLeft, bool leased, Guid? tenantScope = null, string? concurrencyKey = null)
    {
        string lease = leased ? $"'w1', '{Guid.NewGuid()}', {db.Now}" : "NULL, NULL, NULL";
        string key = concurrencyKey is null ? "NULL" : $"'{concurrencyKey}'";
        return db.ExecuteAsync($"""
            INSERT INTO ninjaqueue.job_queue (job_id, queue, job_type, available_at_utc, tenant_scope, concurrency_key, attempts_left, lease_owner, lease_token, lease_expires_at_utc)
            VALUES ({jobId}, 'default', 'Test.Job.v1', {db.Now}, '{tenantScope ?? Guid.Empty}', {key}, {attemptsLeft}, {lease})
            """);
    }

    private static Task<int> Reserve(TestDatabase db, Guid tenantScope, string key, long jobId)
        => db.ExecuteAsync($"INSERT INTO ninjaqueue.job_active_keys (tenant_scope, unique_key, job_id, reserved_at_utc) VALUES ('{tenantScope}', '{key}', {jobId}, {db.Now})");

    private static async Task Rejected(Func<Task> action) => await action.Should().ThrowAsync<DbException>();
}