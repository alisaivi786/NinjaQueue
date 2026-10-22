namespace NinjaQueue.IntegrationTests.Infrastructure;

/// <summary>
/// G13 (§17.5): the pinned schema (V1, plus the V2 dead-letter marker and the V3 performance indexes). A change to the migration or the EF model that is not made here too fails CI, so the
/// plan, the migration and the code cannot drift apart silently. Types are the logical names from <see cref="SchemaSnapshot"/>.
/// </summary>
public static class SchemaManifest
{
    public static readonly string[] Columns =
    [
        // background_jobs
        "background_jobs.id bigint not null",
        "background_jobs.guid_id uuid not null",
        "background_jobs.job_type varchar(200) not null",
        "background_jobs.queue varchar(50) not null",
        "background_jobs.tenant_id uuid null",
        "background_jobs.tenant_scope uuid not null",
        "background_jobs.status smallint not null",
        "background_jobs.payload text null",
        "background_jobs.checkpoint text null",
        "background_jobs.idempotency_key varchar(200) null",
        "background_jobs.unique_key varchar(200) null",
        "background_jobs.concurrency_key varchar(200) null",
        "background_jobs.max_attempts int not null",
        "background_jobs.attempt int not null",
        "background_jobs.snooze_count int not null",
        "background_jobs.release_count int not null",
        "background_jobs.cancel_requested_at_utc timestamptz null",
        "background_jobs.cancel_requested_by uuid null",
        "background_jobs.cancel_reason varchar(1000) null",
        "background_jobs.retain_until_utc timestamptz null",
        "background_jobs.run_at_utc timestamptz not null",
        "background_jobs.parent_job_id bigint null",
        "background_jobs.continuation_policy smallint not null",
        "background_jobs.terminal_reason_code smallint null",
        "background_jobs.replaces_job_id bigint null",
        "background_jobs.orphaned_at_utc timestamptz null",
        "background_jobs.correlation_id varchar(100) null",
        "background_jobs.causation_id varchar(100) null",
        "background_jobs.trace_parent varchar(100) null",
        "background_jobs.actor_id uuid null",
        "background_jobs.source varchar(100) null",
        "background_jobs.progress_processed int null",
        "background_jobs.progress_total int null",
        "background_jobs.last_error_code varchar(100) null",
        "background_jobs.last_error varchar(2000) null",
        "background_jobs.created_at_utc timestamptz not null",
        "background_jobs.started_at_utc timestamptz null",
        "background_jobs.completed_at_utc timestamptz null",
        "background_jobs.updated_at_utc timestamptz not null",
        "background_jobs.dead_letter_handled_at_utc timestamptz null", // V2
        "background_jobs.dead_letter_attempts int null", // V2

        // job_queue
        "job_queue.job_id bigint not null",
        "job_queue.queue varchar(50) not null",
        "job_queue.job_type varchar(200) not null",
        "job_queue.available_at_utc timestamptz not null",
        "job_queue.tenant_scope uuid not null",
        "job_queue.concurrency_key varchar(200) null",
        "job_queue.attempts_left smallint not null",
        "job_queue.lease_owner varchar(200) null",
        "job_queue.lease_token uuid null",
        "job_queue.lease_expires_at_utc timestamptz null",

        // job_attempts
        "job_attempts.id bigint not null",
        "job_attempts.job_id bigint not null",
        "job_attempts.attempt_seq int not null",
        "job_attempts.charged bool not null",
        "job_attempts.worker_id varchar(200) not null",
        "job_attempts.lease_token uuid not null",
        "job_attempts.started_at_utc timestamptz not null",
        "job_attempts.finished_at_utc timestamptz null",
        "job_attempts.duration_ms bigint null",
        "job_attempts.outcome smallint null",
        "job_attempts.error_code varchar(100) null",
        "job_attempts.error varchar(2000) null",
        "job_attempts.exception_type varchar(500) null",

        // job_events
        "job_events.id bigint not null",
        "job_events.job_id bigint not null",
        "job_events.event_code varchar(50) not null",
        "job_events.occurred_at_utc timestamptz not null",
        "job_events.worker_id varchar(200) null",
        "job_events.actor_id uuid null",
        "job_events.reason varchar(1000) null",
        "job_events.data varchar(4000) null",

        // job_recurring
        "job_recurring.id bigint not null",
        "job_recurring.code varchar(200) not null",
        "job_recurring.job_type varchar(200) not null",
        "job_recurring.queue varchar(50) not null",
        "job_recurring.payload text null",
        "job_recurring.cron varchar(100) not null",
        "job_recurring.time_zone varchar(100) not null",
        "job_recurring.misfire_policy smallint not null",
        "job_recurring.allow_overlap bool not null",
        "job_recurring.definition_version int not null",
        "job_recurring.enabled bool not null",
        "job_recurring.paused_by uuid null",
        "job_recurring.next_run_at_utc timestamptz not null",
        "job_recurring.last_run_at_utc timestamptz null",
        "job_recurring.last_job_id bigint null",
        "job_recurring.created_at_utc timestamptz not null",
        "job_recurring.updated_at_utc timestamptz not null",

        // job_workers
        "job_workers.worker_id varchar(200) not null",
        "job_workers.host varchar(200) not null",
        "job_workers.process_id int not null",
        "job_workers.app_version varchar(100) null",
        "job_workers.queues varchar(2000) not null",
        "job_workers.job_types text not null",
        "job_workers.started_at_utc timestamptz not null",
        "job_workers.last_heartbeat_at_utc timestamptz not null",

        // job_leases
        "job_leases.name varchar(100) not null",
        "job_leases.owner varchar(200) null",
        "job_leases.token uuid null",
        "job_leases.expires_at_utc timestamptz not null",

        // job_side_effects
        "job_side_effects.job_id bigint not null",
        "job_side_effects.step_key varchar(200) not null",
        "job_side_effects.status smallint not null",
        "job_side_effects.owner_lease_token uuid not null",
        "job_side_effects.issue_count int not null",
        "job_side_effects.external_ref varchar(500) null",
        "job_side_effects.result_json text null",
        "job_side_effects.started_at_utc timestamptz not null",
        "job_side_effects.completed_at_utc timestamptz null",

        // job_active_keys
        "job_active_keys.tenant_scope uuid not null",
        "job_active_keys.unique_key varchar(200) not null",
        "job_active_keys.job_id bigint not null",
        "job_active_keys.reserved_at_utc timestamptz not null",

        // job_fenced_steps
        "job_fenced_steps.job_id bigint not null",
        "job_fenced_steps.lease_token uuid not null",
        "job_fenced_steps.server_session_id int not null",
        "job_fenced_steps.session_start_utc timestamptz not null",
        "job_fenced_steps.transaction_id bigint null",
        "job_fenced_steps.deadline_utc timestamptz not null",
        "job_fenced_steps.owner_worker varchar(200) not null",

        // job_admin_operations
        "job_admin_operations.id bigint not null",
        "job_admin_operations.guid_id uuid not null",
        "job_admin_operations.tenant_scope uuid not null",
        "job_admin_operations.operation smallint not null",
        "job_admin_operations.actor_id uuid not null",
        "job_admin_operations.reason varchar(1000) not null",
        "job_admin_operations.idempotency_key varchar(200) not null",
        "job_admin_operations.filter_json text null",
        "job_admin_operations.status smallint not null",
        "job_admin_operations.requested_count int not null",
        "job_admin_operations.applied_count int not null",
        "job_admin_operations.not_eligible_count int not null",
        "job_admin_operations.conflict_count int not null",
        "job_admin_operations.not_found_count int not null",
        "job_admin_operations.failed_count int not null",
        "job_admin_operations.report_json text null",
        "job_admin_operations.created_at_utc timestamptz not null",
        "job_admin_operations.completed_at_utc timestamptz null",

        // dashboard (migration 20261023000000_Dashboard)
        "dashboard_users.id uuid not null",
        "dashboard_users.user_name varchar(100) not null",
        "dashboard_users.password_hash varchar(500) not null",
        "dashboard_users.is_admin bool not null",
        "dashboard_users.must_change_password bool not null",
        "dashboard_users.failed_logins int not null",
        "dashboard_users.locked_until_utc timestamptz null",
        "dashboard_users.security_stamp uuid not null",
        "dashboard_users.created_at_utc timestamptz not null",
        "dashboard_users.updated_at_utc timestamptz not null",
        "dashboard_user_tenants.user_id uuid not null",
        "dashboard_user_tenants.tenant_id uuid not null",
        "tenants.tenant_id uuid not null",
        "tenants.name varchar(200) not null",
        "tenants.created_at_utc timestamptz not null",
    ];

    public static readonly string[] Indexes =
    [
        "background_jobs.pk_background_jobs PK (id)",
        "background_jobs.ux_background_jobs_guid_id UNIQUE (guid_id)",
        "background_jobs.ux_background_jobs_idempotency UNIQUE (tenant_scope, job_type, idempotency_key) FILTERED",
        "background_jobs.ix_background_jobs_parent_awaiting INDEX (parent_job_id) FILTERED",
        "background_jobs.ix_background_jobs_tenant_status_created INDEX (tenant_id, status, created_at_utc DESC)",
        "background_jobs.ix_background_jobs_status_completed INDEX (status, completed_at_utc)",
        "background_jobs.ix_background_jobs_dead_letter_pending INDEX (job_type, id) FILTERED", // V2
        "background_jobs.ix_background_jobs_parent INDEX (parent_job_id) FILTERED", // V3
        "job_queue.pk_job_queue PK (job_id)",
        "job_queue.ix_job_queue_claim_type INDEX (queue, job_type, available_at_utc, job_id) FILTERED", // V3 (replaces ix_job_queue_claim)
        "job_queue.ix_job_queue_tenant_scope INDEX (tenant_scope, queue)", // V3
        "job_queue.ix_job_queue_lease_expiry INDEX (lease_expires_at_utc) FILTERED",
        "job_queue.ux_job_queue_running_concurrency_key UNIQUE (tenant_scope, concurrency_key) FILTERED",
        "job_attempts.pk_job_attempts PK (id)",
        "job_attempts.ux_job_attempts_job_id_attempt_seq UNIQUE (job_id, attempt_seq)",
        "job_events.pk_job_events PK (id)",
        "job_events.ix_job_events_job_id_id INDEX (job_id, id)",
        "job_recurring.pk_job_recurring PK (id)",
        "job_recurring.ux_job_recurring_code UNIQUE (code)",
        "job_recurring.ix_job_recurring_due INDEX (enabled, next_run_at_utc)",
        "job_workers.pk_job_workers PK (worker_id)",
        "job_workers.ix_job_workers_last_heartbeat INDEX (last_heartbeat_at_utc)",
        "job_leases.pk_job_leases PK (name)",
        "job_side_effects.pk_job_side_effects PK (job_id, step_key)",
        "job_active_keys.pk_job_active_keys PK (tenant_scope, unique_key)",
        "job_active_keys.ux_job_active_keys_job_id UNIQUE (job_id)",
        "job_fenced_steps.pk_job_fenced_steps PK (job_id)",
        "job_fenced_steps.ix_job_fenced_steps_deadline INDEX (deadline_utc)",
        "job_admin_operations.pk_job_admin_operations PK (id)",
        "job_admin_operations.ux_job_admin_operations_guid_id UNIQUE (guid_id)",
        "job_admin_operations.ux_job_admin_operations_idempotency UNIQUE (tenant_scope, actor_id, idempotency_key)",
        "dashboard_users.pk_dashboard_users PK (id)",
        "dashboard_users.ux_dashboard_users_user_name UNIQUE (user_name)",
        "dashboard_user_tenants.pk_dashboard_user_tenants PK (user_id, tenant_id)",
        "dashboard_user_tenants.ix_dashboard_user_tenants_tenant_id INDEX (tenant_id)",
        "tenants.pk_tenants PK (tenant_id)",
    ];

    public static readonly string[] Checks =
    [
        "background_jobs.ck_background_jobs_status",
        "background_jobs.ck_background_jobs_max_attempts",
        "background_jobs.ck_background_jobs_attempt",
        "background_jobs.ck_background_jobs_counters",
        "background_jobs.ck_background_jobs_continuation_policy",
        "background_jobs.ck_background_jobs_terminal_reason",
        "background_jobs.ck_background_jobs_tenant_scope",
        "job_queue.ck_job_queue_attempts_left",
        "job_queue.ck_job_queue_claimable_has_attempts",
        "job_queue.ck_job_queue_lease_complete",
        "job_attempts.ck_job_attempts_attempt_seq",
        "job_attempts.ck_job_attempts_outcome",
        "job_recurring.ck_job_recurring_misfire_policy",
        "job_side_effects.ck_job_side_effects_status",
        "job_side_effects.ck_job_side_effects_issue_count",
        "job_admin_operations.ck_job_admin_operations_operation",
        "job_admin_operations.ck_job_admin_operations_status",
    ];

    public static readonly string[] ForeignKeys =
    [
        "background_jobs.fk_background_jobs_parent_job_id -> background_jobs",
        "job_queue.fk_job_queue_job_id -> background_jobs",
        "job_attempts.fk_job_attempts_job_id -> background_jobs",
        "job_events.fk_job_events_job_id -> background_jobs",
        "job_side_effects.fk_job_side_effects_job_id -> background_jobs",
        "job_active_keys.fk_job_active_keys_job_id -> background_jobs",
        "dashboard_user_tenants.fk_dashboard_user_tenants_user_id -> dashboard_users",
        "dashboard_user_tenants.fk_dashboard_user_tenants_tenant_id -> tenants",
    ];

    public static readonly string[] SeededLeases =
    [
        "maintenance.orphans", "maintenance.purge", "maintenance.recovery", "maintenance.watchdog", "metrics.sampler", "scheduler.recurring",
    ];

    /// <summary>Asserts that <paramref name="snapshot"/> is exactly the pinned schema.</summary>
    public static void AssertMatches(SchemaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Columns.Should().BeEquivalentTo(Columns, "columns, types and nullability are pinned (G13)");
        snapshot.Indexes.Should().BeEquivalentTo(Indexes, "keys, indexes and filters are pinned (G13)");
        snapshot.CheckNames.Should().BeEquivalentTo(Checks, "CHECK constraints are pinned (G13)");
        snapshot.ForeignKeys.Should().BeEquivalentTo(ForeignKeys, "foreign keys are pinned (G13)");
    }
}