using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace NinjaQueue.EntityFrameworkCore.Migrations;

/// <summary>
/// The complete NinjaQueue schema: the job record, the hot dispatch queue, attempts, the lifecycle audit, recurring
/// schedules, workers, maintenance leases (seeded expired), the side-effect ledger, unique-key reservations, the fenced-step
/// watchdog registry and bulk admin operations. Every name is explicit and matches <c>ApplyNinjaQueue</c> column for column.
/// </summary>
[DbContext(typeof(NinjaQueueDbContext))]
[Migration("20261022000000_InitialSchema")]
public sealed class InitialSchema : NinjaQueueMigration
{
    /// <summary>The maintenance leases seeded with an expired lease, so the first instance to ask takes each one.</summary>
    public static IReadOnlyList<string> SeededLeaseNames { get; } =
    [
        "maintenance.recovery", "maintenance.purge", "maintenance.orphans", "maintenance.watchdog", "scheduler.recurring", "metrics.sampler",
    ];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        string schema = JobSchemaName.Validate(Schema);
        bool postgres = IsPostgres(migrationBuilder);
        migrationBuilder.EnsureSchema(schema);

        // background_jobs: the canonical record, one row per job, kept until retention.
        migrationBuilder.CreateTable(
            name: "background_jobs",
            schema: schema,
            columns: table => new
            {
                id = Identity(table, postgres),
                guid_id = table.Column<Guid>(nullable: false),
                job_type = table.Column<string>(maxLength: JobLimits.MaxJobTypeLength, nullable: false),
                queue = table.Column<string>(maxLength: JobLimits.MaxQueueLength, nullable: false),
                tenant_id = table.Column<Guid>(nullable: true),
                tenant_scope = table.Column<Guid>(nullable: false),
                status = table.Column<short>(nullable: false),
                payload = table.Column<string>(nullable: true),
                checkpoint = table.Column<string>(nullable: true),
                idempotency_key = table.Column<string>(maxLength: JobLimits.MaxKeyLength, nullable: true),
                unique_key = table.Column<string>(maxLength: JobLimits.MaxKeyLength, nullable: true),
                concurrency_key = table.Column<string>(maxLength: JobLimits.MaxKeyLength, nullable: true),
                max_attempts = table.Column<int>(nullable: false),
                attempt = table.Column<int>(nullable: false),
                snooze_count = table.Column<int>(nullable: false),
                release_count = table.Column<int>(nullable: false),
                cancel_requested_at_utc = table.Column<DateTimeOffset>(nullable: true),
                cancel_requested_by = table.Column<Guid>(nullable: true),
                cancel_reason = table.Column<string>(maxLength: 1000, nullable: true),
                retain_until_utc = table.Column<DateTimeOffset>(nullable: true),
                run_at_utc = table.Column<DateTimeOffset>(nullable: false),
                parent_job_id = table.Column<long>(nullable: true),
                continuation_policy = table.Column<short>(nullable: false),
                terminal_reason_code = table.Column<short>(nullable: true),
                replaces_job_id = table.Column<long>(nullable: true),
                orphaned_at_utc = table.Column<DateTimeOffset>(nullable: true),
                correlation_id = table.Column<string>(maxLength: 100, nullable: true),
                causation_id = table.Column<string>(maxLength: 100, nullable: true),
                trace_parent = table.Column<string>(maxLength: 100, nullable: true),
                actor_id = table.Column<Guid>(nullable: true),
                source = table.Column<string>(maxLength: 100, nullable: true),
                progress_processed = table.Column<int>(nullable: true),
                progress_total = table.Column<int>(nullable: true),
                last_error_code = table.Column<string>(maxLength: 100, nullable: true),
                last_error = table.Column<string>(maxLength: 2000, nullable: true),
                created_at_utc = table.Column<DateTimeOffset>(nullable: false),
                started_at_utc = table.Column<DateTimeOffset>(nullable: true),
                completed_at_utc = table.Column<DateTimeOffset>(nullable: true),
                updated_at_utc = table.Column<DateTimeOffset>(nullable: false),
                dead_letter_handled_at_utc = table.Column<DateTimeOffset>(nullable: true),
                dead_letter_attempts = table.Column<int>(nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_background_jobs", x => x.id);
                table.ForeignKey("fk_background_jobs_parent_job_id", x => x.parent_job_id, "background_jobs", "id", schema, onDelete: ReferentialAction.NoAction);
                table.CheckConstraint("ck_background_jobs_status", NinjaQueueSql.StatusCheck);
                table.CheckConstraint("ck_background_jobs_max_attempts", NinjaQueueSql.MaxAttemptsCheck);
                table.CheckConstraint("ck_background_jobs_attempt", NinjaQueueSql.AttemptCheck);
                table.CheckConstraint("ck_background_jobs_counters", NinjaQueueSql.CountersCheck);
                table.CheckConstraint("ck_background_jobs_continuation_policy", NinjaQueueSql.ContinuationPolicyCheck);
                table.CheckConstraint("ck_background_jobs_terminal_reason", NinjaQueueSql.TerminalReasonCheck);
                table.CheckConstraint("ck_background_jobs_tenant_scope", NinjaQueueSql.TenantScopeCheck);
            });
        Index(migrationBuilder, schema, "background_jobs", "ux_background_jobs_guid_id", ["guid_id"], unique: true);
        Index(migrationBuilder, schema, "background_jobs", "ux_background_jobs_idempotency", ["tenant_scope", "job_type", "idempotency_key"], unique: true,
            filter: NinjaQueueSql.IdempotencyFilter);
        Index(migrationBuilder, schema, "background_jobs", "ix_background_jobs_parent_awaiting", ["parent_job_id"], filter: NinjaQueueSql.AwaitingFilter);
        Index(migrationBuilder, schema, "background_jobs", "ix_background_jobs_parent", ["parent_job_id"], filter: NinjaQueueSql.HasParentFilter);
        migrationBuilder.CreateIndex("ix_background_jobs_tenant_status_created", "background_jobs", ["tenant_id", "status", "created_at_utc"], schema,
            descending: [false, false, true]);
        Index(migrationBuilder, schema, "background_jobs", "ix_background_jobs_status_completed", ["status", "completed_at_utc"]);
        Index(migrationBuilder, schema, "background_jobs", "ix_background_jobs_dead_letter_pending", ["job_type", "id"], filter: NinjaQueueSql.DeadLetterPendingFilter);

        // job_queue: dispatch state for active work only; the row is deleted when the job becomes terminal.
        migrationBuilder.CreateTable(
            name: "job_queue",
            schema: schema,
            columns: table => new
            {
                job_id = table.Column<long>(nullable: false),
                queue = table.Column<string>(maxLength: JobLimits.MaxQueueLength, nullable: false),
                job_type = table.Column<string>(maxLength: JobLimits.MaxJobTypeLength, nullable: false),
                available_at_utc = table.Column<DateTimeOffset>(nullable: false),
                tenant_scope = table.Column<Guid>(nullable: false),
                concurrency_key = table.Column<string>(maxLength: JobLimits.MaxKeyLength, nullable: true),
                attempts_left = table.Column<short>(nullable: false),
                lease_owner = table.Column<string>(maxLength: 200, nullable: true),
                lease_token = table.Column<Guid>(nullable: true),
                lease_expires_at_utc = table.Column<DateTimeOffset>(nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_job_queue", x => x.job_id);
                table.ForeignKey("fk_job_queue_job_id", x => x.job_id, "background_jobs", "id", schema, onDelete: ReferentialAction.NoAction);
                table.CheckConstraint("ck_job_queue_attempts_left", NinjaQueueSql.AttemptsLeftCheck);
                table.CheckConstraint("ck_job_queue_claimable_has_attempts", NinjaQueueSql.ClaimableHasAttemptsCheck);
                table.CheckConstraint("ck_job_queue_lease_complete", NinjaQueueSql.LeaseCompleteCheck);
            });
        Index(migrationBuilder, schema, "job_queue", "ix_job_queue_claim_type", ["queue", "job_type", "available_at_utc", "job_id"], filter: NinjaQueueSql.UnleasedFilter);
        Index(migrationBuilder, schema, "job_queue", "ix_job_queue_tenant_scope", ["tenant_scope", "queue"]);
        Index(migrationBuilder, schema, "job_queue", "ix_job_queue_lease_expiry", ["lease_expires_at_utc"], filter: NinjaQueueSql.LeasedFilter);
        Index(migrationBuilder, schema, "job_queue", "ux_job_queue_running_concurrency_key", ["tenant_scope", "concurrency_key"], unique: true,
            filter: NinjaQueueSql.RunningConcurrencyKeyFilter);

        // The queue row changes on every claim, renewal and outcome.
        if (postgres)
        {
            // Leave room for HOT updates and vacuum early.
            migrationBuilder.Sql($"ALTER TABLE {schema}.job_queue SET (fillfactor = 80, autovacuum_vacuum_scale_factor = 0.01);");
        }
        else if (IsSqlServer(migrationBuilder))
        {
            // A claim locks every row it examines: never escalate to a table lock.
            migrationBuilder.Sql($"ALTER TABLE {schema}.job_queue SET (LOCK_ESCALATION = DISABLE);");
        }

        // job_attempts: one row per claim.
        migrationBuilder.CreateTable(
            name: "job_attempts",
            schema: schema,
            columns: table => new
            {
                id = Identity(table, postgres),
                job_id = table.Column<long>(nullable: false),
                attempt_seq = table.Column<int>(nullable: false),
                charged = table.Column<bool>(nullable: false),
                worker_id = table.Column<string>(maxLength: 200, nullable: false),
                lease_token = table.Column<Guid>(nullable: false),
                started_at_utc = table.Column<DateTimeOffset>(nullable: false),
                finished_at_utc = table.Column<DateTimeOffset>(nullable: true),
                duration_ms = table.Column<long>(nullable: true),
                outcome = table.Column<short>(nullable: true),
                error_code = table.Column<string>(maxLength: 100, nullable: true),
                error = table.Column<string>(maxLength: 2000, nullable: true),
                exception_type = table.Column<string>(maxLength: 500, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_job_attempts", x => x.id);
                table.ForeignKey("fk_job_attempts_job_id", x => x.job_id, "background_jobs", "id", schema, onDelete: ReferentialAction.NoAction);
                table.CheckConstraint("ck_job_attempts_attempt_seq", NinjaQueueSql.AttemptSeqCheck);
                table.CheckConstraint("ck_job_attempts_outcome", NinjaQueueSql.OutcomeCheck);
            });
        Index(migrationBuilder, schema, "job_attempts", "ux_job_attempts_job_id_attempt_seq", ["job_id", "attempt_seq"], unique: true);

        // job_events: the append-only lifecycle audit.
        migrationBuilder.CreateTable(
            name: "job_events",
            schema: schema,
            columns: table => new
            {
                id = Identity(table, postgres),
                job_id = table.Column<long>(nullable: false),
                event_code = table.Column<string>(maxLength: JobEventCodes.MaxLength, nullable: false),
                occurred_at_utc = table.Column<DateTimeOffset>(nullable: false),
                worker_id = table.Column<string>(maxLength: 200, nullable: true),
                actor_id = table.Column<Guid>(nullable: true),
                reason = table.Column<string>(maxLength: 1000, nullable: true),
                data = table.Column<string>(maxLength: 4000, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_job_events", x => x.id);
                table.ForeignKey("fk_job_events_job_id", x => x.job_id, "background_jobs", "id", schema, onDelete: ReferentialAction.NoAction);
            });
        Index(migrationBuilder, schema, "job_events", "ix_job_events_job_id_id", ["job_id", "id"]);

        // job_recurring: cron schedules defined in code and synced at startup.
        migrationBuilder.CreateTable(
            name: "job_recurring",
            schema: schema,
            columns: table => new
            {
                id = Identity(table, postgres),
                code = table.Column<string>(maxLength: 200, nullable: false),
                job_type = table.Column<string>(maxLength: JobLimits.MaxJobTypeLength, nullable: false),
                queue = table.Column<string>(maxLength: JobLimits.MaxQueueLength, nullable: false),
                payload = table.Column<string>(nullable: true),
                cron = table.Column<string>(maxLength: 100, nullable: false),
                time_zone = table.Column<string>(maxLength: 100, nullable: false),
                misfire_policy = table.Column<short>(nullable: false),
                allow_overlap = table.Column<bool>(nullable: false),
                definition_version = table.Column<int>(nullable: false),
                enabled = table.Column<bool>(nullable: false),
                paused_by = table.Column<Guid>(nullable: true),
                next_run_at_utc = table.Column<DateTimeOffset>(nullable: false),
                last_run_at_utc = table.Column<DateTimeOffset>(nullable: true),
                last_job_id = table.Column<long>(nullable: true),
                created_at_utc = table.Column<DateTimeOffset>(nullable: false),
                updated_at_utc = table.Column<DateTimeOffset>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_job_recurring", x => x.id);
                table.CheckConstraint("ck_job_recurring_misfire_policy", NinjaQueueSql.MisfirePolicyCheck);
            });
        Index(migrationBuilder, schema, "job_recurring", "ux_job_recurring_code", ["code"], unique: true);
        Index(migrationBuilder, schema, "job_recurring", "ix_job_recurring_due", ["enabled", "next_run_at_utc"]);

        // job_workers: the live worker registry.
        migrationBuilder.CreateTable(
            name: "job_workers",
            schema: schema,
            columns: table => new
            {
                worker_id = table.Column<string>(maxLength: 200, nullable: false),
                host = table.Column<string>(maxLength: 200, nullable: false),
                process_id = table.Column<int>(nullable: false),
                app_version = table.Column<string>(maxLength: 100, nullable: true),
                queues = table.Column<string>(maxLength: 2000, nullable: false),
                job_types = table.Column<string>(nullable: false),
                started_at_utc = table.Column<DateTimeOffset>(nullable: false),
                last_heartbeat_at_utc = table.Column<DateTimeOffset>(nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_job_workers", x => x.worker_id));
        Index(migrationBuilder, schema, "job_workers", "ix_job_workers_last_heartbeat", ["last_heartbeat_at_utc"]);

        // job_leases: named maintenance leadership, seeded expired so the first instance to ask takes each lease.
        migrationBuilder.CreateTable(
            name: "job_leases",
            schema: schema,
            columns: table => new
            {
                name = table.Column<string>(maxLength: 100, nullable: false),
                owner = table.Column<string>(maxLength: 200, nullable: true),
                token = table.Column<Guid>(nullable: true),
                expires_at_utc = table.Column<DateTimeOffset>(nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_job_leases", x => x.name));
        foreach (string lease in SeededLeaseNames)
        {
            migrationBuilder.Sql($"INSERT INTO {schema}.job_leases (name, expires_at_utc) VALUES ('{lease}', '1970-01-01T00:00:00+00:00');");
        }

        // job_side_effects: the external operation ledger.
        migrationBuilder.CreateTable(
            name: "job_side_effects",
            schema: schema,
            columns: table => new
            {
                job_id = table.Column<long>(nullable: false),
                step_key = table.Column<string>(maxLength: 200, nullable: false),
                status = table.Column<short>(nullable: false),
                owner_lease_token = table.Column<Guid>(nullable: false),
                issue_count = table.Column<int>(nullable: false),
                external_ref = table.Column<string>(maxLength: 500, nullable: true),
                result_json = table.Column<string>(nullable: true),
                started_at_utc = table.Column<DateTimeOffset>(nullable: false),
                completed_at_utc = table.Column<DateTimeOffset>(nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_job_side_effects", x => new { x.job_id, x.step_key });
                table.ForeignKey("fk_job_side_effects_job_id", x => x.job_id, "background_jobs", "id", schema, onDelete: ReferentialAction.NoAction);
                table.CheckConstraint("ck_job_side_effects_status", NinjaQueueSql.SideEffectStatusCheck);
                table.CheckConstraint("ck_job_side_effects_issue_count", NinjaQueueSql.IssueCountCheck);
            });

        // job_active_keys: unique_key reservations covering every non-terminal state.
        migrationBuilder.CreateTable(
            name: "job_active_keys",
            schema: schema,
            columns: table => new
            {
                tenant_scope = table.Column<Guid>(nullable: false),
                unique_key = table.Column<string>(maxLength: JobLimits.MaxKeyLength, nullable: false),
                job_id = table.Column<long>(nullable: false),
                reserved_at_utc = table.Column<DateTimeOffset>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_job_active_keys", x => new { x.tenant_scope, x.unique_key });
                table.ForeignKey("fk_job_active_keys_job_id", x => x.job_id, "background_jobs", "id", schema, onDelete: ReferentialAction.NoAction);
            });
        Index(migrationBuilder, schema, "job_active_keys", "ux_job_active_keys_job_id", ["job_id"], unique: true);

        // job_fenced_steps: the watchdog registry. Written in autocommit on a control connection, so no foreign key.
        migrationBuilder.CreateTable(
            name: "job_fenced_steps",
            schema: schema,
            columns: table => new
            {
                job_id = table.Column<long>(nullable: false),
                lease_token = table.Column<Guid>(nullable: false),
                server_session_id = table.Column<int>(nullable: false),
                session_start_utc = table.Column<DateTimeOffset>(nullable: false),
                transaction_id = table.Column<long>(nullable: true),
                deadline_utc = table.Column<DateTimeOffset>(nullable: false),
                owner_worker = table.Column<string>(maxLength: 200, nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_job_fenced_steps", x => x.job_id));
        Index(migrationBuilder, schema, "job_fenced_steps", "ix_job_fenced_steps_deadline", ["deadline_utc"]);

        // job_admin_operations: bulk admin operations and their outcome reports.
        migrationBuilder.CreateTable(
            name: "job_admin_operations",
            schema: schema,
            columns: table => new
            {
                id = Identity(table, postgres),
                guid_id = table.Column<Guid>(nullable: false),
                tenant_scope = table.Column<Guid>(nullable: false),
                operation = table.Column<short>(nullable: false),
                actor_id = table.Column<Guid>(nullable: false),
                reason = table.Column<string>(maxLength: 1000, nullable: false),
                idempotency_key = table.Column<string>(maxLength: JobLimits.MaxKeyLength, nullable: false),
                filter_json = table.Column<string>(nullable: true),
                status = table.Column<short>(nullable: false),
                requested_count = table.Column<int>(nullable: false),
                applied_count = table.Column<int>(nullable: false),
                not_eligible_count = table.Column<int>(nullable: false),
                conflict_count = table.Column<int>(nullable: false),
                not_found_count = table.Column<int>(nullable: false),
                failed_count = table.Column<int>(nullable: false),
                report_json = table.Column<string>(nullable: true),
                created_at_utc = table.Column<DateTimeOffset>(nullable: false),
                completed_at_utc = table.Column<DateTimeOffset>(nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_job_admin_operations", x => x.id);
                table.CheckConstraint("ck_job_admin_operations_operation", NinjaQueueSql.AdminOperationKindCheck);
                table.CheckConstraint("ck_job_admin_operations_status", NinjaQueueSql.AdminOperationStatusCheck);
            });
        Index(migrationBuilder, schema, "job_admin_operations", "ux_job_admin_operations_guid_id", ["guid_id"], unique: true);
        Index(migrationBuilder, schema, "job_admin_operations", "ux_job_admin_operations_idempotency", ["tenant_scope", "actor_id", "idempotency_key"], unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        string schema = JobSchemaName.Validate(Schema);

        // Children first: the foreign keys have no cascade. The schema itself stays (it also holds the migration history);
        // NinjaQueueSchema.UninstallAsync removes it.
        foreach (string table in (string[])["job_admin_operations", "job_fenced_steps", "job_active_keys", "job_side_effects",
                     "job_leases", "job_workers", "job_recurring", "job_events", "job_attempts", "job_queue", "background_jobs"])
        {
            migrationBuilder.DropTable(table, schema);
        }
    }

    // Identity bigint: GENERATED BY DEFAULT AS IDENTITY on PostgreSQL, IDENTITY(1, 1) on SQL Server.
    private static OperationBuilder<AddColumnOperation> Identity(ColumnsBuilder table, bool postgres)
        => postgres
            ? table.Column<long>(type: "bigint GENERATED BY DEFAULT AS IDENTITY", nullable: false)
            : table.Column<long>(nullable: false).Annotation("SqlServer:Identity", "1, 1");

    private static void Index(
        MigrationBuilder migrationBuilder, string schema, string table, string name, string[] columns, bool unique = false, string? filter = null)
        => migrationBuilder.CreateIndex(name, table, columns, schema, unique, filter);
}