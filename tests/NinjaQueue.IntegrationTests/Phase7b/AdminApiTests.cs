using NinjaQueue.Admin;
using NinjaQueue.IntegrationTests.Phase4;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase7b;

/// <summary>
/// The admin service (kit part of G15, §17.8, §17.9): tenant scoping on the server (out of scope = not found), redacted
/// payloads, keyset paging, and the bulk contract — exactly one selector, the 500-id cap, per-job outcomes, idempotent
/// replay, one active operation per tenant, and filter operations run as the engine's own job.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "7b")]
[Trait("Gate", "G15")]
public sealed class AdminApiTests(JobsDatabaseFixture fixture)
{
    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_tenant_caller_never_sees_or_changes_another_tenants_jobs(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        JobId own = await host.EnqueueAsync(new P7bTenantPayload(P7b.TenantA, "own"), new JobOptions { TenantId = P7b.TenantA });
        JobId other = await host.EnqueueAsync(new P7bTenantPayload(P7b.TenantB, "other"), new JobOptions { TenantId = P7b.TenantB });
        host.Get<TestTenants>().AllowedTenantScopes = [P7b.TenantA];

        (await host.AdminAsync(a => a.GetAsync(other))).Should().BeNull("a cross-tenant id is not found (404, never 403)");
        (await host.AdminAsync(a => a.GetAsync(own)))!.Job.TenantId.Should().Be(P7b.TenantA);
        (await host.AdminAsync(a => a.ListAsync(new JobQuery()))).Items.Select(j => j.JobId).Should().Equal(own);
        (await host.AdminAsync(a => a.ListAsync(new JobQuery { TenantId = P7b.TenantB }))).Items.Should().BeEmpty();
        (await host.CancelAsync(other)).Should().Be(CancelResult.NotFound);
        (await host.AdminAsync(a => a.RequeueAsync(other, P7b.Actor, "x"))).Outcome.Should().Be(AdminRunOutcome.NotFound);
        (await host.StatusAsync(other)).Should().Be(JobStatus.Enqueued);
        (await host.EventsAsync(other)).Should().Equal(JobEventCodes.JobEnqueued);

        host.Get<TestTenants>().AllowedTenantScopes = [];
        (await host.AdminAsync(a => a.ListAsync(new JobQuery()))).Items.Should().BeEmpty("no scopes = nothing");
        host.Get<TestTenants>().AllowedTenantScopes = null;
        (await host.AdminAsync(a => a.ListAsync(new JobQuery()))).Items.Should().HaveCount(2, "the all-tenants caller sees everything");
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Detail_shows_history_and_a_redacted_payload_only_when_allowed(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        JobId visible = await host.EnqueueAsync(new P7bVisiblePayload(P7b.TenantA, "Welcome", "ceo@example.com", new P7bVisibleInner("n", "s3cret")),
            new JobOptions { TenantId = P7b.TenantA });
        JobId hidden = await host.EnqueueAsync(new P7bTenantPayload(P7b.TenantA, "hidden"), new JobOptions { TenantId = P7b.TenantA });
        await host.RunOneAsync(P7b.Visible);

        JobDetail withoutPermission = (await host.AdminAsync(a => a.GetAsync(visible)))!;
        withoutPermission.Payload.Should().BeNull();
        withoutPermission.Attempts.Should().ContainSingle().Which.Outcome.Should().Be(JobAttemptOutcome.Succeeded);
        withoutPermission.Events.Select(e => e.EventCode).Should().Equal(JobEventCodes.JobEnqueued, JobEventCodes.JobClaimed, JobEventCodes.JobSucceeded);

        string payload = (await host.AdminAsync(a => a.GetAsync(visible, includePayload: true)))!.Payload!;
        payload.Should().Contain("Welcome").And.Contain("\"recipient\":\"***\"").And.Contain("\"secret\":\"***\"");
        payload.Should().NotContain("ceo@example.com").And.NotContain("s3cret");
        (await host.AdminAsync(a => a.GetAsync(hidden, includePayload: true)))!.Payload.Should().BeNull("only DashboardVisible types ever show a payload");
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Lists_are_keyset_paged_newest_first_and_filtered(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        List<JobId> ids = [];
        for (int i = 0; i < 5; i++)
        {
            ids.Add(await host.EnqueueAsync(new P7bPayload($"p{i}")));
        }

        JobId dead = await host.EnqueueAsync(new P4Payload("permanent"));
        await host.RunOneAsync(P7b.P4Standard);

        List<JobId> seen = [];
        string? cursor = null;
        int pages = 0;
        do
        {
            JobPage page = await host.AdminAsync(a => a.ListAsync(new JobQuery { JobType = P7b.Step, PageSize = 2, Cursor = cursor }));
            seen.AddRange(page.Items.Select(j => j.JobId));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        seen.Should().Equal(Enumerable.Reverse(ids));
        (await host.AdminAsync(a => a.ListAsync(new JobQuery { Statuses = [JobStatus.DeadLettered] }))).Items.Select(j => j.JobId).Should().Equal(dead);
        await FluentActions.Awaiting(() => host.AdminAsync(a => a.ListAsync(new JobQuery { PageSize = 0 })))
            .Should().ThrowAsync<JobAdminValidationException>().Where(e => e.Code == JobAdminErrorCodes.InvalidPageSize);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Bulk_ids_report_each_outcome_and_a_replay_applies_nothing_twice(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        // Dead letters first: DeadLetterAsync claims the oldest queued job of the type.
        JobId eligible = await DeadLetterAsync(host, P7b.TenantA, null);
        JobId conflicting = await DeadLetterAsync(host, P7b.TenantA, "bulk:held");
        JobId crossTenant = await DeadLetterAsync(host, P7b.TenantB, null);
        JobId holder = await host.EnqueueAsync(new P7bTenantPayload(P7b.TenantA, "holder"), new JobOptions { TenantId = P7b.TenantA, UniqueKey = "bulk:held" });
        JobId ineligible = await host.EnqueueAsync(new P7bTenantPayload(P7b.TenantA, "queued"), new JobOptions { TenantId = P7b.TenantA });
        JobId missing = new(Guid.NewGuid());
        host.Get<TestTenants>().AllowedTenantScopes = [P7b.TenantA];

        BulkRequest request = new()
        {
            Operation = AdminOperationKind.BulkRetry,
            JobIds = [eligible, conflicting, ineligible, crossTenant, missing],
            Reason = "relay fixed",
            IdempotencyKey = "op-1",
        };
        BulkSubmission first = await host.AdminAsync(a => a.BulkAsync(request, P7b.Actor));
        first.Kind.Should().Be(BulkSubmissionKind.Completed);
        BulkOperationReport report = first.Report!;
        report.Status.Should().Be(AdminOperationStatus.Completed);
        report.Items.Should().BeEquivalentTo(new[]
        {
            new BulkItem(eligible, BulkItemOutcome.Applied, JobStatus.Enqueued),
            new BulkItem(conflicting, BulkItemOutcome.Conflict, JobStatus.DeadLettered, holder),
            new BulkItem(ineligible, BulkItemOutcome.NotEligible, JobStatus.Enqueued),
            new BulkItem(crossTenant, BulkItemOutcome.NotFound),
            new BulkItem(missing, BulkItemOutcome.NotFound),
        });
        (report.Requested, report.Applied, report.Conflict, report.NotEligible, report.NotFound).Should().Be((5, 1, 1, 1, 2));
        (await host.EventCountAsync(eligible, JobEventCodes.BulkOperationApplied)).Should().Be(1);
        (await host.StatusAsync(crossTenant)).Should().Be(JobStatus.DeadLettered);

        (await host.RunOneAsync(P7b.Keyed)).Should().NotBeNull();
        BulkSubmission replay = await host.AdminAsync(a => a.BulkAsync(request, P7b.Actor));
        replay.Kind.Should().Be(BulkSubmissionKind.Replayed);
        replay.OperationId.Should().Be(first.OperationId);
        replay.Report!.Items.Should().BeEquivalentTo(report.Items);
        (await host.EventCountAsync(eligible, JobEventCodes.JobRequeued)).Should().Be(1, "a replay applies nothing twice");
        (await host.AdminAsync(a => a.GetBulkOperationAsync(first.OperationId!.Value)))!.Applied.Should().Be(1);
        host.Get<TestTenants>().AllowedTenantScopes = [P7b.TenantB];
        (await host.AdminAsync(a => a.GetBulkOperationAsync(first.OperationId!.Value))).Should().BeNull("another tenant cannot read the report");
        host.Get<RecordingAuditSink>().Entries.Should().ContainSingle(e => e.OperationId == first.OperationId);
        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Bulk_requests_are_validated_and_capped_at_500_ids(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        List<JobId> ids = [];
        for (int i = 0; i < 500; i++)
        {
            ids.Add(new JobId(Guid.NewGuid()));
        }

        BulkRequest Request(IReadOnlyList<JobId>? jobIds, BulkJobFilter? filter, string key) => new()
        {
            Operation = AdminOperationKind.BulkCancel,
            JobIds = jobIds,
            Filter = filter,
            Reason = "cleanup",
            IdempotencyKey = key,
        };

        BulkSubmission ok = await host.AdminAsync(a => a.BulkAsync(Request(ids, null, "cap-500"), P7b.Actor));
        ok.Kind.Should().Be(BulkSubmissionKind.Completed);
        ok.Report!.NotFound.Should().Be(500);

        async Task<string> RejectedAsync(BulkRequest request)
            => (await FluentActions.Awaiting(() => host.AdminAsync(a => a.BulkAsync(request, P7b.Actor))).Should().ThrowAsync<JobAdminValidationException>()).Which.Code;

        (await RejectedAsync(Request([.. ids, new JobId(Guid.NewGuid())], null, "cap-501"))).Should().Be(JobAdminErrorCodes.BulkJobIdsLimitExceeded);
        (await RejectedAsync(Request(ids, new BulkJobFilter(), "both"))).Should().Be(JobAdminErrorCodes.BulkSelectorRequired);
        (await RejectedAsync(Request(null, null, "neither"))).Should().Be(JobAdminErrorCodes.BulkSelectorRequired);
        (await RejectedAsync(Request([], null, "empty"))).Should().Be(JobAdminErrorCodes.BulkSelectorRequired);
        (await RejectedAsync(Request(ids, null, " "))).Should().Be(JobAdminErrorCodes.IdempotencyKeyRequired);
        (await RejectedAsync(Request(ids, null, "k") with { Reason = "" })).Should().Be(JobAdminErrorCodes.ReasonRequired);
        (await host.Database.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.job_admin_operations")).Should().Be(1, "a rejected request writes no operation row");
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_filter_runs_as_a_job_and_a_second_operation_for_the_tenant_is_rejected_meanwhile(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        List<JobId> dead = [];
        for (int i = 0; i < 120; i++)
        {
            dead.Add(await host.EnqueueAsync(new P4Payload("permanent")));
        }

        while (await host.RunOneAsync(P7b.P4Standard) is not null)
        {
        }

        JobId notMatching = await host.EnqueueAsync(new P7bPayload("queued"));
        BulkRequest filter = new()
        {
            Operation = AdminOperationKind.BulkRequeue,
            Filter = new BulkJobFilter { Status = JobStatus.DeadLettered, JobType = P7b.P4Standard },
            Reason = "nightly requeue",
            IdempotencyKey = "filter-1",
        };
        BulkSubmission queued = await host.AdminAsync(a => a.BulkAsync(filter, P7b.Actor));
        queued.Kind.Should().Be(BulkSubmissionKind.Queued);
        queued.Report!.Status.Should().Be(AdminOperationStatus.Pending);

        BulkSubmission second = await host.AdminAsync(a => a.BulkAsync(filter with { IdempotencyKey = "filter-2" }, P7b.Actor));
        second.Kind.Should().Be(BulkSubmissionKind.RejectedActiveOperation, "one active bulk operation per tenant");
        (await host.AdminAsync(a => a.BulkAsync(filter, P7b.Actor))).Kind.Should().Be(BulkSubmissionKind.Replayed);

        (await host.RunOneAsync("NinjaQueue.Admin.BulkOperation.v1")).Should().Be(Execution.ExecutionResult.Succeeded);
        BulkOperationReport report = (await host.AdminAsync(a => a.GetBulkOperationAsync(queued.OperationId!.Value)))!;
        report.Status.Should().Be(AdminOperationStatus.Completed);
        (report.Requested, report.Applied).Should().Be((120, 120));
        foreach (JobId id in dead)
        {
            (await host.StatusAsync(id)).Should().Be(JobStatus.Enqueued);
        }

        (await host.EventCountAsync(dead[0], JobEventCodes.BulkOperationApplied)).Should().Be(1);
        (await host.StatusAsync(notMatching)).Should().Be(JobStatus.Enqueued);
        (await host.Database.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.job_events WHERE event_code = 'BulkOperationApplied'")).Should().Be(120);
        (await host.AdminAsync(a => a.BulkAsync(filter with { IdempotencyKey = "filter-3" }, P7b.Actor))).Kind
            .Should().Be(BulkSubmissionKind.Queued, "the tenant is free again once the operation ended");
        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Stats_workers_and_audit_entries_are_tenant_aware(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        JobId dead = await DeadLetterAsync(host, P7b.TenantA, null);
        await host.EnqueueAsync(new P7bTenantPayload(P7b.TenantA, "ready"), new JobOptions { TenantId = P7b.TenantA });
        await host.EnqueueAsync(new P7bTenantPayload(P7b.TenantA, "later"), new JobOptions { TenantId = P7b.TenantA, RunAt = DateTimeOffset.UtcNow.AddHours(1) });
        await host.EnqueueAsync(new P7bTenantPayload(P7b.TenantB, "other"), new JobOptions { TenantId = P7b.TenantB });
        host.Get<TestTenants>().AllowedTenantScopes = [P7b.TenantA];

        JobStats stats = await host.AdminAsync(a => a.GetStatsAsync());
        stats.Queues.Should().ContainSingle().Which.Should().Match<QueueStats>(q => q.Queue == "default" && q.Enqueued == 1 && q.Scheduled == 1 && q.Processing == 0);
        (stats.DeadLettered, stats.Succeeded, stats.Awaiting).Should().Be((1, 0, 0));
        host.Get<TestTenants>().AllowedTenantScopes = null;
        (await host.AdminAsync(a => a.GetStatsAsync())).Queues.Single().Enqueued.Should().Be(2, "the all-tenants caller counts tenant B too");

        (await host.AdminAsync(a => a.RequeueAsync(dead, P7b.Actor, "audit"))).TenantScope.Should().Be(P7b.TenantA);
        host.Get<RecordingAuditSink>().Entries.Should().ContainSingle(e => e.Action == "Requeue" && e.JobId == dead && e.TenantScope == P7b.TenantA);

        await host.StartWorkersAsync();
        IReadOnlyList<JobWorkerInfo> workers = await host.AdminAsync(a => a.GetWorkersAsync());
        workers.Should().ContainSingle().Which.WorkerId.Should().Be(host.Get<Hosting.NinjaQueueWorkerHost>().WorkerId);
        workers[0].JobTypes.Should().Contain(P7b.Keyed).And.Contain("NinjaQueue.Admin.BulkOperation.v1");
        await host.StopWorkersAsync();
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_recurring_pause_keeps_paused_by_and_resume_recomputes_the_next_run(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        JobId last = await host.EnqueueAsync(new P7bPayload("last-occurrence"));
        TestDatabase db = host.Database;
        const string payload = "{\"name\":\"d\"}";
        await db.ExecuteAsync($"""
            INSERT INTO ninjaqueue.job_recurring (code, job_type, queue, payload, cron, time_zone, misfire_policy, allow_overlap, definition_version, enabled,
                                            paused_by, next_run_at_utc, last_run_at_utc, last_job_id, created_at_utc, updated_at_utc)
            VALUES ('digest.daily', '{P7b.Step}', 'default', '{payload}', '0 2 * * *', 'UTC', 1, {db.Bool(false)}, 1, {db.Bool(true)},
                    NULL, {db.Now}, NULL, {await host.DbIdAsync(last)}, {db.Now}, {db.Now})
            """);

        host.Get<TestTenants>().AllowedTenantScopes = [P7b.TenantA];
        (await host.AdminAsync(a => a.PauseRecurringAsync("digest.daily", P7b.Actor, "x"))).Should().Be(RecurringAdminOutcome.NotFound, "schedules are platform-level");
        (await host.AdminAsync(a => a.ListRecurringAsync())).Should().BeEmpty();
        host.Get<TestTenants>().AllowedTenantScopes = null;

        (await host.AdminAsync(a => a.PauseRecurringAsync("digest.daily", null, "maintenance window"))).Should().Be(RecurringAdminOutcome.Applied);
        RecurringScheduleState paused = (await host.AdminAsync(a => a.ListRecurringAsync())).Single();
        paused.Enabled.Should().BeFalse();
        paused.PausedBy.Should().NotBeNull("a pause without an actor still sets paused_by, so the startup sync keeps it paused");
        (await host.AdminAsync(a => a.PauseRecurringAsync("digest.daily", P7b.Actor, "again"))).Should().Be(RecurringAdminOutcome.NotEligible);
        (await host.AdminAsync(a => a.PauseRecurringAsync("missing", P7b.Actor, "x"))).Should().Be(RecurringAdminOutcome.NotFound);

        (await host.AdminAsync(a => a.ResumeRecurringAsync("digest.daily", P7b.Actor, "window over"))).Should().Be(RecurringAdminOutcome.Applied);
        RecurringScheduleState resumed = (await host.AdminAsync(a => a.ListRecurringAsync())).Single();
        resumed.Enabled.Should().BeTrue();
        resumed.PausedBy.Should().BeNull();
        resumed.NextRunAtUtc.Should().BeAfter(DateTimeOffset.UtcNow.AddSeconds(-5)).And.BeBefore(DateTimeOffset.UtcNow.AddDays(1).AddMinutes(1));
        resumed.NextRunAtUtc.Hour.Should().Be(2, "recomputed from the cron, not the stale next run");
        (await host.AdminAsync(a => a.ResumeRecurringAsync("digest.daily", P7b.Actor, "again"))).Should().Be(RecurringAdminOutcome.NotEligible);

        List<object?[]> events = await db.QueryAsync($"""
            SELECT e.event_code, e.actor_id, e.reason FROM ninjaqueue.job_events e WHERE e.job_id = {await host.DbIdAsync(last)} AND e.event_code LIKE 'Recurring%' ORDER BY e.id
            """);
        events.Select(e => (string)e[0]!).Should().Equal(JobEventCodes.RecurringPaused, JobEventCodes.RecurringResumed);
        events[1][1].Should().Be(P7b.Actor);
        events[1][2].Should().Be("window over");
        host.Get<RecordingAuditSink>().Entries.Select(e => e.Action).Should().Contain(["PauseRecurring", "ResumeRecurring"]);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_recurring_trigger_enqueues_one_occurrence_under_the_overlap_rule_and_is_audited(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        TestDatabase db = host.Database;
        await db.ExecuteAsync($"""
            INSERT INTO ninjaqueue.job_recurring (code, job_type, queue, payload, cron, time_zone, misfire_policy, allow_overlap, definition_version, enabled,
                                            paused_by, next_run_at_utc, last_run_at_utc, last_job_id, created_at_utc, updated_at_utc)
            VALUES ('digest.daily', '{P7b.Step}', 'default', '{"{\"name\":\"d\"}"}', '0 2 * * *', 'UTC', 1, {db.Bool(false)}, 1, {db.Bool(true)},
                    NULL, '2030-01-01T02:00:00+00:00', NULL, NULL, {db.Now}, {db.Now}),
                   ('digest.overlap', '{P7b.Step}', 'default', '{"{\"name\":\"o\"}"}', '0 3 * * *', 'UTC', 1, {db.Bool(true)}, 1, {db.Bool(true)},
                    NULL, '2030-01-01T03:00:00+00:00', NULL, NULL, {db.Now}, {db.Now}),
                   ('digest.unknown', 'Test.NotRegistered.v1', 'default', NULL, '0 4 * * *', 'UTC', 1, {db.Bool(false)}, 1, {db.Bool(true)},
                    NULL, '2030-01-01T04:00:00+00:00', NULL, NULL, {db.Now}, {db.Now})
            """);

        host.Get<TestTenants>().AllowedTenantScopes = [P7b.TenantA];
        (await host.AdminAsync(a => a.TriggerRecurringAsync("digest.daily", P7b.Actor, "x"))).Outcome.Should().Be(RecurringAdminOutcome.NotFound, "schedules are platform-level");
        host.Get<TestTenants>().AllowedTenantScopes = null;
        await FluentActions.Awaiting(() => host.AdminAsync(a => a.TriggerRecurringAsync("digest.daily", P7b.Actor, " "))).Should().ThrowAsync<Exception>("a reason is required");
        await FluentActions.Awaiting(() => host.AdminAsync(a => a.TriggerRecurringAsync("digest.daily", Guid.Empty, "x"))).Should().ThrowAsync<ArgumentException>("an actor is required");
        (await host.AdminAsync(a => a.TriggerRecurringAsync("missing", P7b.Actor, "x"))).Outcome.Should().Be(RecurringAdminOutcome.NotFound);
        (await host.AdminAsync(a => a.TriggerRecurringAsync("digest.unknown", P7b.Actor, "x"))).Outcome.Should().Be(RecurringAdminOutcome.NotEligible, "this instance does not run the type");

        // 1. Applied: one occurrence with the schedule's keys; the next run is untouched.
        RecurringTriggerResult first = await host.AdminAsync(a => a.TriggerRecurringAsync("digest.daily", P7b.Actor, "rerun after the outage"));
        first.Outcome.Should().Be(RecurringAdminOutcome.Applied);
        JobId job = first.JobId!.Value;
        (await host.StatusAsync(job)).Should().Be(JobStatus.Enqueued);
        long dbId = await host.DbIdAsync(job);
        object?[] row = (await db.QueryAsync($"SELECT idempotency_key, unique_key, source, actor_id, payload FROM ninjaqueue.background_jobs WHERE id = {dbId}")).Single();
        ((string)row[0]!).Should().StartWith("recurring:digest.daily:");
        row[1].Should().Be("recurring:digest.daily");
        row[2].Should().Be("recurring");
        row[3].Should().Be(P7b.Actor);
        row[4].Should().Be("{\"name\":\"d\"}");
        RecurringScheduleState state = (await host.AdminAsync(a => a.ListRecurringAsync())).Single(s => s.Code == "digest.daily");
        state.NextRunAtUtc.Should().Be(new DateTimeOffset(2030, 1, 1, 2, 0, 0, TimeSpan.Zero), "a trigger does not move the schedule");
        state.LastJobId.Should().Be(dbId);
        object?[] triggered = (await db.QueryAsync($"SELECT actor_id, reason FROM ninjaqueue.job_events WHERE job_id = {dbId} AND event_code = '{JobEventCodes.RecurringTriggered}'")).Single();
        triggered[0].Should().Be(P7b.Actor);
        triggered[1].Should().Be("rerun after the outage");
        host.Get<RecordingAuditSink>().Entries.Should().ContainSingle(e => e.Action == "TriggerRecurring" && e.JobId == job && e.ActorId == P7b.Actor);

        // 2. Conflict: the occurrence is still active and overlap is not allowed; nothing new is enqueued.
        RecurringTriggerResult again = await host.AdminAsync(a => a.TriggerRecurringAsync("digest.daily", P7b.Actor, "again"));
        again.Outcome.Should().Be(RecurringAdminOutcome.Conflict);
        again.ActiveJobId.Should().Be(job);
        (await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.background_jobs WHERE job_type = '{P7b.Step}'")).Should().Be(1);
        host.Get<RecordingAuditSink>().Entries.Count(e => e.Action == "TriggerRecurring").Should().Be(1, "a conflict is not audited as applied");

        // 3. Once the occurrence ends, a trigger enqueues the next one.
        Storage.ClaimedJob claimed = (await host.ClaimOneAsync(P7b.Step))!;
        claimed.JobId.Should().Be(job);
        await host.InScopeAsync(sp => sp.GetRequiredService<Storage.IJobStore>().RecordOutcomeAsync(claimed,
            new Storage.AttemptOutcome(Storage.AttemptOutcomeKind.Succeeded), CancellationToken.None));
        RecurringTriggerResult next = await host.AdminAsync(a => a.TriggerRecurringAsync("digest.daily", P7b.Actor, "after it finished"));
        next.Outcome.Should().Be(RecurringAdminOutcome.Applied);
        next.JobId.Should().NotBe(job);

        // 4. Overlap allowed: two triggers, two occurrences.
        (await host.AdminAsync(a => a.TriggerRecurringAsync("digest.overlap", P7b.Actor, "one"))).Outcome.Should().Be(RecurringAdminOutcome.Applied);
        (await host.AdminAsync(a => a.TriggerRecurringAsync("digest.overlap", P7b.Actor, "two"))).Outcome.Should().Be(RecurringAdminOutcome.Applied);
        (await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.background_jobs WHERE job_type = '{P7b.Step}' AND status < 10")).Should().Be(3);

        // 5. A paused schedule is not triggered.
        (await host.AdminAsync(a => a.PauseRecurringAsync("digest.overlap", P7b.Actor, "window"))).Should().Be(RecurringAdminOutcome.Applied);
        (await host.AdminAsync(a => a.TriggerRecurringAsync("digest.overlap", P7b.Actor, "x"))).Outcome.Should().Be(RecurringAdminOutcome.NotEligible);
    }

    private static async Task<JobId> DeadLetterAsync(Phase4Host host, Guid tenant, string? uniqueKey)
    {
        JobId id = await host.EnqueueAsync(new P7bTenantPayload(tenant, "dl"), new JobOptions { TenantId = tenant, UniqueKey = uniqueKey, MaxAttempts = 1 });
        Storage.ClaimedJob job = (await host.ClaimOneAsync(P7b.Keyed))!;
        job.JobId.Should().Be(id);
        await host.InScopeAsync(sp => sp.GetRequiredService<Storage.IJobStore>().RecordOutcomeAsync(job,
            new Storage.AttemptOutcome(Storage.AttemptOutcomeKind.DeadLetter, ErrorCode: "Test", Error: "dead", TerminalReason: JobTerminalReason.PermanentError),
            CancellationToken.None));
        (await host.StatusAsync(id)).Should().Be(JobStatus.DeadLettered);
        return id;
    }
}