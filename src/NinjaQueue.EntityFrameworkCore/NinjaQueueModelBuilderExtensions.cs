using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NinjaQueue.EntityFrameworkCore.Entities;

namespace NinjaQueue.EntityFrameworkCore;

/// <summary>Maps the NinjaQueue tables into the host's <see cref="DbContext"/>.</summary>
public static class NinjaQueueModelBuilderExtensions
{
    /// <summary>
    /// Adds the job tables to the model, in <paramref name="schema"/>. An application maps them into its own context so that
    /// a staged job joins its transaction. The schema itself comes from NinjaQueue's migrations; this mapping matches them
    /// column for column, including keys, indexes, filters and CHECK constraints. Every name is set explicitly and does not
    /// depend on the application's naming convention. SQL fragments use only syntax shared by SQL Server and PostgreSQL.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="schema">The schema of the job tables (must match <c>NinjaQueue:Schema</c>).</param>
    /// <param name="excludeFromMigrations">True (the default for application contexts): the application's own EF Core
    /// migrations ignore the job tables, because NinjaQueue installs and upgrades them itself.</param>
    public static ModelBuilder ApplyNinjaQueue(this ModelBuilder modelBuilder, string schema = JobSchemaName.Default, bool excludeFromMigrations = true)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        JobSchemaName.Validate(schema);
        MapTables(modelBuilder, schema);
        if (excludeFromMigrations)
        {
            foreach (Type entity in (Type[])[typeof(JobRecord), typeof(JobQueueEntry), typeof(JobAttemptRecord), typeof(JobEventRecord),
                         typeof(RecurringJobRecord), typeof(JobWorkerRecord), typeof(JobLeaseRecord), typeof(JobSideEffectRecord),
                         typeof(JobActiveKeyRecord), typeof(JobFencedStepRecord), typeof(JobAdminOperationRecord)])
            {
                modelBuilder.Entity(entity).Metadata.SetIsTableExcludedFromMigrations(true);
            }
        }

        return modelBuilder;
    }

    private static void MapTables(ModelBuilder modelBuilder, string schema)
    {

        modelBuilder.Entity<JobRecord>(b =>
        {
            b.ToTable("background_jobs", schema, t =>
            {
                t.HasCheckConstraint("ck_background_jobs_status", NinjaQueueSql.StatusCheck);
                t.HasCheckConstraint("ck_background_jobs_max_attempts", NinjaQueueSql.MaxAttemptsCheck);
                t.HasCheckConstraint("ck_background_jobs_attempt", NinjaQueueSql.AttemptCheck);
                t.HasCheckConstraint("ck_background_jobs_counters", NinjaQueueSql.CountersCheck);
                t.HasCheckConstraint("ck_background_jobs_continuation_policy", NinjaQueueSql.ContinuationPolicyCheck);
                t.HasCheckConstraint("ck_background_jobs_terminal_reason", NinjaQueueSql.TerminalReasonCheck);
                t.HasCheckConstraint("ck_background_jobs_tenant_scope", NinjaQueueSql.TenantScopeCheck);
            });
            b.HasKey(x => x.Id).HasName("pk_background_jobs");
            b.Property(x => x.JobType).HasMaxLength(JobLimits.MaxJobTypeLength);
            b.Property(x => x.Queue).HasMaxLength(JobLimits.MaxQueueLength);
            b.Property(x => x.IdempotencyKey).HasMaxLength(JobLimits.MaxKeyLength);
            b.Property(x => x.UniqueKey).HasMaxLength(JobLimits.MaxKeyLength);
            b.Property(x => x.ConcurrencyKey).HasMaxLength(JobLimits.MaxKeyLength);
            b.Property(x => x.CancelReason).HasMaxLength(1000);
            b.Property(x => x.CorrelationId).HasMaxLength(100);
            b.Property(x => x.CausationId).HasMaxLength(100);
            b.Property(x => x.TraceParent).HasMaxLength(100);
            b.Property(x => x.Source).HasMaxLength(100);
            b.Property(x => x.LastErrorCode).HasMaxLength(100);
            b.Property(x => x.LastError).HasMaxLength(2000);
            b.HasIndex(x => x.GuidId).IsUnique().HasDatabaseName("ux_background_jobs_guid_id");
            b.HasIndex(x => new { x.TenantScope, x.JobType, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("ux_background_jobs_idempotency").HasFilter(NinjaQueueSql.IdempotencyFilter);
            b.HasIndex(x => x.ParentJobId, "ix_background_jobs_parent_awaiting").HasFilter(NinjaQueueSql.AwaitingFilter);
            b.HasIndex(x => x.ParentJobId, "ix_background_jobs_parent").HasFilter(NinjaQueueSql.HasParentFilter); // V3
            b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAtUtc }).IsDescending(false, false, true)
                .HasDatabaseName("ix_background_jobs_tenant_status_created");
            b.HasIndex(x => new { x.Status, x.CompletedAtUtc }).HasDatabaseName("ix_background_jobs_status_completed");
            b.HasIndex(x => new { x.JobType, x.Id }).HasDatabaseName("ix_background_jobs_dead_letter_pending").HasFilter(NinjaQueueSql.DeadLetterPendingFilter);
            b.HasOne<JobRecord>().WithMany().HasForeignKey(x => x.ParentJobId)
                .HasConstraintName("fk_background_jobs_parent_job_id").OnDelete(DeleteBehavior.NoAction);
            SnakeCaseColumns(b);
        });

        modelBuilder.Entity<JobQueueEntry>(b =>
        {
            b.ToTable("job_queue", schema, t =>
            {
                t.HasCheckConstraint("ck_job_queue_attempts_left", NinjaQueueSql.AttemptsLeftCheck);
                t.HasCheckConstraint("ck_job_queue_claimable_has_attempts", NinjaQueueSql.ClaimableHasAttemptsCheck);
                t.HasCheckConstraint("ck_job_queue_lease_complete", NinjaQueueSql.LeaseCompleteCheck);
            });
            b.HasKey(x => x.JobId).HasName("pk_job_queue");
            b.Property(x => x.JobId).ValueGeneratedNever();
            b.Property(x => x.Queue).HasMaxLength(JobLimits.MaxQueueLength);
            b.Property(x => x.JobType).HasMaxLength(JobLimits.MaxJobTypeLength);
            b.Property(x => x.ConcurrencyKey).HasMaxLength(JobLimits.MaxKeyLength);
            b.Property(x => x.LeaseOwner).HasMaxLength(200);
            b.HasIndex(x => new { x.Queue, x.JobType, x.AvailableAtUtc, x.JobId }).HasDatabaseName("ix_job_queue_claim_type").HasFilter(NinjaQueueSql.UnleasedFilter); // V3
            b.HasIndex(x => new { x.TenantScope, x.Queue }).HasDatabaseName("ix_job_queue_tenant_scope"); // V3
            b.HasIndex(x => x.LeaseExpiresAtUtc).HasDatabaseName("ix_job_queue_lease_expiry").HasFilter(NinjaQueueSql.LeasedFilter);
            b.HasIndex(x => new { x.TenantScope, x.ConcurrencyKey }).IsUnique()
                .HasDatabaseName("ux_job_queue_running_concurrency_key").HasFilter(NinjaQueueSql.RunningConcurrencyKeyFilter);
            b.HasOne<JobRecord>().WithOne().HasForeignKey<JobQueueEntry>(x => x.JobId)
                .HasConstraintName("fk_job_queue_job_id").OnDelete(DeleteBehavior.NoAction);
            SnakeCaseColumns(b);
        });

        modelBuilder.Entity<JobAttemptRecord>(b =>
        {
            b.ToTable("job_attempts", schema, t =>
            {
                t.HasCheckConstraint("ck_job_attempts_attempt_seq", NinjaQueueSql.AttemptSeqCheck);
                t.HasCheckConstraint("ck_job_attempts_outcome", NinjaQueueSql.OutcomeCheck);
            });
            b.HasKey(x => x.Id).HasName("pk_job_attempts");
            b.Property(x => x.WorkerId).HasMaxLength(200);
            b.Property(x => x.ErrorCode).HasMaxLength(100);
            b.Property(x => x.Error).HasMaxLength(2000);
            b.Property(x => x.ExceptionType).HasMaxLength(500);
            b.HasIndex(x => new { x.JobId, x.AttemptSeq }).IsUnique().HasDatabaseName("ux_job_attempts_job_id_attempt_seq");
            b.HasOne<JobRecord>().WithMany().HasForeignKey(x => x.JobId)
                .HasConstraintName("fk_job_attempts_job_id").OnDelete(DeleteBehavior.NoAction);
            SnakeCaseColumns(b);
        });

        modelBuilder.Entity<JobEventRecord>(b =>
        {
            b.ToTable("job_events", schema);
            b.HasKey(x => x.Id).HasName("pk_job_events");
            b.Property(x => x.EventCode).HasMaxLength(JobEventCodes.MaxLength);
            b.Property(x => x.WorkerId).HasMaxLength(200);
            b.Property(x => x.Reason).HasMaxLength(1000);
            b.Property(x => x.Data).HasMaxLength(4000);
            b.HasIndex(x => new { x.JobId, x.Id }).HasDatabaseName("ix_job_events_job_id_id");
            b.HasOne<JobRecord>().WithMany().HasForeignKey(x => x.JobId)
                .HasConstraintName("fk_job_events_job_id").OnDelete(DeleteBehavior.NoAction);
            SnakeCaseColumns(b);
        });

        modelBuilder.Entity<RecurringJobRecord>(b =>
        {
            b.ToTable("job_recurring", schema, t => t.HasCheckConstraint("ck_job_recurring_misfire_policy", NinjaQueueSql.MisfirePolicyCheck));
            b.HasKey(x => x.Id).HasName("pk_job_recurring");
            b.Property(x => x.Code).HasMaxLength(200);
            b.Property(x => x.JobType).HasMaxLength(JobLimits.MaxJobTypeLength);
            b.Property(x => x.Queue).HasMaxLength(JobLimits.MaxQueueLength);
            b.Property(x => x.Cron).HasMaxLength(100);
            b.Property(x => x.TimeZone).HasMaxLength(100);
            b.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_job_recurring_code");
            b.HasIndex(x => new { x.Enabled, x.NextRunAtUtc }).HasDatabaseName("ix_job_recurring_due");
            SnakeCaseColumns(b);
        });

        modelBuilder.Entity<JobWorkerRecord>(b =>
        {
            b.ToTable("job_workers", schema);
            b.HasKey(x => x.WorkerId).HasName("pk_job_workers");
            b.Property(x => x.WorkerId).HasMaxLength(200).ValueGeneratedNever();
            b.Property(x => x.Host).HasMaxLength(200);
            b.Property(x => x.AppVersion).HasMaxLength(100);
            b.Property(x => x.Queues).HasMaxLength(2000);
            b.HasIndex(x => x.LastHeartbeatAtUtc).HasDatabaseName("ix_job_workers_last_heartbeat");
            SnakeCaseColumns(b);
        });

        modelBuilder.Entity<JobLeaseRecord>(b =>
        {
            b.ToTable("job_leases", schema);
            b.HasKey(x => x.Name).HasName("pk_job_leases");
            b.Property(x => x.Name).HasMaxLength(100).ValueGeneratedNever();
            b.Property(x => x.Owner).HasMaxLength(200);
            SnakeCaseColumns(b);
        });

        modelBuilder.Entity<JobSideEffectRecord>(b =>
        {
            b.ToTable("job_side_effects", schema, t =>
            {
                t.HasCheckConstraint("ck_job_side_effects_status", NinjaQueueSql.SideEffectStatusCheck);
                t.HasCheckConstraint("ck_job_side_effects_issue_count", NinjaQueueSql.IssueCountCheck);
            });
            b.HasKey(x => new { x.JobId, x.StepKey }).HasName("pk_job_side_effects");
            b.Property(x => x.StepKey).HasMaxLength(200);
            b.Property(x => x.ExternalRef).HasMaxLength(500);
            b.HasOne<JobRecord>().WithMany().HasForeignKey(x => x.JobId)
                .HasConstraintName("fk_job_side_effects_job_id").OnDelete(DeleteBehavior.NoAction);
            SnakeCaseColumns(b);
        });

        modelBuilder.Entity<JobActiveKeyRecord>(b =>
        {
            b.ToTable("job_active_keys", schema);
            b.HasKey(x => new { x.TenantScope, x.UniqueKey }).HasName("pk_job_active_keys");
            b.Property(x => x.UniqueKey).HasMaxLength(JobLimits.MaxKeyLength);
            b.HasIndex(x => x.JobId).IsUnique().HasDatabaseName("ux_job_active_keys_job_id");
            b.HasOne<JobRecord>().WithOne().HasForeignKey<JobActiveKeyRecord>(x => x.JobId)
                .HasConstraintName("fk_job_active_keys_job_id").OnDelete(DeleteBehavior.NoAction);
            SnakeCaseColumns(b);
        });

        modelBuilder.Entity<JobFencedStepRecord>(b =>
        {
            b.ToTable("job_fenced_steps", schema);
            b.HasKey(x => x.JobId).HasName("pk_job_fenced_steps");
            b.Property(x => x.JobId).ValueGeneratedNever();
            b.Property(x => x.OwnerWorker).HasMaxLength(200);
            b.HasIndex(x => x.DeadlineUtc).HasDatabaseName("ix_job_fenced_steps_deadline");
            SnakeCaseColumns(b);
        });

        modelBuilder.Entity<JobAdminOperationRecord>(b =>
        {
            b.ToTable("job_admin_operations", schema, t =>
            {
                t.HasCheckConstraint("ck_job_admin_operations_operation", NinjaQueueSql.AdminOperationKindCheck);
                t.HasCheckConstraint("ck_job_admin_operations_status", NinjaQueueSql.AdminOperationStatusCheck);
            });
            b.HasKey(x => x.Id).HasName("pk_job_admin_operations");
            b.Property(x => x.Reason).HasMaxLength(1000);
            b.Property(x => x.IdempotencyKey).HasMaxLength(JobLimits.MaxKeyLength);
            b.HasIndex(x => x.GuidId).IsUnique().HasDatabaseName("ux_job_admin_operations_guid_id");
            b.HasIndex(x => new { x.TenantScope, x.ActorId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("ux_job_admin_operations_idempotency");
            SnakeCaseColumns(b);
        });
    }

    // Explicit (not convention) column names, so a host without a snake_case convention still matches the migration.
    private static void SnakeCaseColumns<TEntity>(EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableProperty property in builder.Metadata.GetProperties())
        {
            property.SetColumnName(ToSnakeCase(property.Name));
        }
    }

    internal static string ToSnakeCase(string name)
    {
        StringBuilder result = new(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
            {
                result.Append('_');
            }

            result.Append(char.ToLowerInvariant(c));
        }

        return result.ToString();
    }
}