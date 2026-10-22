using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NinjaQueue.Observability;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase6;

/// <summary>
/// Phase 6a (§7.6): the <c>NinjaQueue</c> meter emits claimed / completed{outcome} / retries / dead-lettered / duration /
/// in-flight and the sampled queue gauges; one <c>ninjaqueue.execute</c> activity per job continues the enqueuing request's trace;
/// the <c>ninjaqueue</c> health check reports Healthy, Degraded and Unhealthy.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "6a")]
public sealed class ObservabilityTests(JobsDatabaseFixture fixture)
{
    private const string Probe = "Test.Probe.v1";

    private sealed record Measured(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Metrics_count_claims_outcomes_retries_dead_letters_and_sample_the_queue(DatabaseKind kind)
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, kind);
        await using WorkerTestHost host = WorkerTestHost.Create(db, b => b.AddJobHandler<ProbeHandler>(), o => o.Worker.MaintenanceInterval = TimeSpan.FromMilliseconds(300));
        IMeterFactory? factory = host.Services.GetService<IMeterFactory>();
        ConcurrentQueue<Measured> measured = new();
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == JobMetrics.MeterName && ReferenceEquals(instrument.Meter.Scope, factory))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) => measured.Enqueue(new Measured(i.Name, v, Tags(tags))));
        listener.SetMeasurementEventCallback<double>((i, v, tags, _) => measured.Enqueue(new Measured(i.Name, v, Tags(tags))));
        listener.Start();

        host.Get<ProbePlan>().Behaviour = (payload, context, _) => payload.Name switch
        {
            "flaky" when context.Attempt == 1 => throw new InvalidOperationException("Transient failure."),
            "permanent" => throw new PermanentJobException("Bad data."),
            _ => Task.FromResult(JobResult.Success()),
        };
        await host.StartAsync();
        JobId ok = await host.EnqueueAsync(new ProbePayload("ok"));
        JobId flaky = await host.EnqueueAsync(new ProbePayload("flaky"));
        JobId permanent = await host.EnqueueAsync(new ProbePayload("permanent"));
        (await JobProbes.EventuallyAsync(async () => await db.StatusOfAsync(ok) == JobStatus.Succeeded && await db.StatusOfAsync(flaky) == JobStatus.Succeeded
            && await db.StatusOfAsync(permanent) == JobStatus.DeadLettered, TimeSpan.FromSeconds(30))).Should().BeTrue(
            "ok: {0} [{1}]; flaky: {2} [{3}]; permanent: {4} [{5}]; blocking: {6}; logs: {7}",
            await db.StatusOfAsync(ok), await db.AttemptTrailAsync(ok), await db.StatusOfAsync(flaky), await db.AttemptTrailAsync(flaky),
            await db.StatusOfAsync(permanent), await db.AttemptTrailAsync(permanent), await db.BlockingAsync(), host.Logs);

        // A job waiting in the queue for the sampler (observable gauges).
        await host.EnqueueAsync(new ProbePayload("later"), new JobOptions { RunAt = DateTimeOffset.UtcNow.AddHours(1) });
        (await JobProbes.EventuallyAsync(() =>
        {
            listener.RecordObservableInstruments();
            return Task.FromResult(measured.Any(m => m.Instrument == "jobs.queue_depth" && m.Value == 1 && Equals(m.Tags.GetValueOrDefault("queue"), "default")));
        }, TimeSpan.FromSeconds(10))).Should().BeTrue("the sampler feeds jobs.queue_depth{queue}");
        measured.Should().Contain(m => m.Instrument == "jobs.oldest_pending_age");

        double Sum(string name, string? outcome = null) => measured
            .Where(m => m.Instrument == name && (outcome is null || Equals(m.Tags.GetValueOrDefault("outcome"), outcome)))
            .Sum(m => m.Value);

        Sum("jobs.claimed").Should().Be(4, "ok, flaky twice and permanent were claimed");
        Sum("jobs.completed", nameof(Execution.ExecutionResult.Succeeded)).Should().Be(2);
        Sum("jobs.completed", nameof(Execution.ExecutionResult.Rescheduled)).Should().Be(1);
        Sum("jobs.completed", nameof(Execution.ExecutionResult.DeadLettered)).Should().Be(1);
        Sum("jobs.retries").Should().Be(1);
        Sum("jobs.dead_lettered").Should().Be(1);
        measured.Count(m => m.Instrument == "jobs.duration").Should().Be(4);
        Sum("jobs.in_flight").Should().Be(0, "every +1 has its -1");
        measured.Where(m => m.Instrument is "jobs.claimed" or "jobs.completed" or "jobs.retries" or "jobs.dead_lettered")
            .Should().OnlyContain(m => Equals(m.Tags.GetValueOrDefault("job_type"), Probe));
    }

    [Fact]
    public async Task Enqueue_increments_jobs_enqueued()
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, DatabaseKind.Postgres);
        await using WorkerTestHost host = WorkerTestHost.Create(db, b => b.AddJobHandler<ProbeHandler>(), o => o.Worker.Enabled = false);
        IMeterFactory? factory = host.Services.GetService<IMeterFactory>();
        long enqueued = 0;
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == JobMetrics.MeterName && ReferenceEquals(instrument.Meter.Scope, factory) && instrument.Name == "jobs.enqueued")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, v, _, _) => Interlocked.Add(ref enqueued, v));
        listener.Start();

        await host.EnqueueAsync(new ProbePayload("counted"));

        Interlocked.Read(ref enqueued).Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Each_job_gets_an_execute_activity_that_continues_the_enqueue_trace(DatabaseKind kind)
    {
        await using JobsHost host = await JobsHost.StartAsync(fixture, kind, b =>
        {
            b.AddJobHandler<ProbeHandler>();
            b.Services.AddSingleton<ProbePlan>();
        });
        using ActivitySource requestSource = new("NinjaQueue.Tests.Request");
        ConcurrentQueue<Activity> stopped = new();
        using ActivityListener listener = new()
        {
            ShouldListenTo = s => s.Name == JobActivitySource.Name || s.Name == requestSource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        Activity request = requestSource.StartActivity("POST /companies/provision", ActivityKind.Server)!;
        JobId first = await host.EnqueueAsync(new ProbePayload("traced-1"));
        JobId second = await host.EnqueueAsync(new ProbePayload("traced-2"));
        request.Stop();
        Activity.Current.Should().BeNull();
        JobId untraced = await host.EnqueueAsync(new ProbePayload("untraced"));

        (await host.Database.JobColumnAsync<string>(first, "trace_parent")).Should().Be(request.Id, "trace_parent is stored at enqueue");
        (await host.Database.IsNullAsync(untraced, "trace_parent")).Should().BeTrue();
        while (await host.RunOneAsync(Probe) is not null)
        {
        }

        Activity Execute(JobId id) => stopped.Single(a => a.Source.Name == JobActivitySource.Name && Equals(a.GetTagItem("ninjaqueue.job_id"), id.Value));
        Activity a1 = Execute(first);
        Activity a2 = Execute(second);
        Activity a3 = Execute(untraced);

        foreach (Activity a in new[] { a1, a2 })
        {
            a.OperationName.Should().Be("ninjaqueue.execute");
            a.Kind.Should().Be(ActivityKind.Consumer);
            a.TraceId.Should().Be(request.TraceId, "request → job is one trace");
            a.ParentSpanId.Should().Be(request.SpanId);
            a.GetTagItem("ninjaqueue.job_type").Should().Be(Probe);
            a.GetTagItem("ninjaqueue.outcome").Should().Be("Succeeded");
        }

        a1.SpanId.Should().NotBe(a2.SpanId, "one activity per job");
        a3.TraceId.Should().NotBe(request.TraceId);
        a3.ParentSpanId.Should().Be(default(ActivitySpanId));
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Health_check_reports_Healthy_Degraded_and_Unhealthy(DatabaseKind kind)
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, kind);

        await using (WorkerTestHost running = WorkerTestHost.Create(db, b => b.AddJobHandler<ProbeHandler>()))
        {
            await running.StartAsync();
            HealthCheckResult healthy = await running.HealthAsync();
            healthy.Status.Should().Be(HealthStatus.Healthy, healthy.Description);
            healthy.Data["provider"].Should().Be(db.IsPostgres ? "PostgreSQL" : "SqlServer");
            healthy.Data["shutdown_budget_s"].Should().Be(27d);
            healthy.Data["worker_id"].Should().Be(running.Worker.WorkerId);
            if (!db.IsPostgres)
            {
                healthy.Data["is_read_committed_snapshot_on"].Should().Be(kind == DatabaseKind.SqlServerRcsiOn);
                healthy.Data["compatibility_level"].Should().Be(160);
            }
        }

        await using (WorkerTestHost backlog = WorkerTestHost.Create(db, b => b.AddJobHandler<ProbeHandler>(), o =>
        {
            o.Worker.Enabled = false;
            o.OldestPendingDegradedAfter = TimeSpan.FromSeconds(1);
        }))
        {
            await backlog.EnqueueAsync(new ProbePayload("waiting"));
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            IReadOnlyList<QueueSample> samples = await backlog.InScopeAsync(sp => sp.GetRequiredService<IJobMaintenanceStore>().SampleQueuesAsync(CancellationToken.None));
            backlog.Get<JobMetrics>().UpdateSamples(samples);

            HealthCheckResult degraded = await backlog.HealthAsync();
            degraded.Status.Should().Be(HealthStatus.Degraded);
            degraded.Description.Should().Contain("queue 'default' oldest ready job waited");
        }

        string unreachable = db.IsPostgres
            ? new NpgsqlConnectionStringBuilder(db.ConnectionString) { Database = "nkj_missing_database", Timeout = 3 }.ConnectionString
            : new SqlConnectionStringBuilder(db.ConnectionString) { InitialCatalog = "nkj_missing_database", ConnectTimeout = 3, ConnectRetryCount = 0 }.ConnectionString;
        await using WorkerTestHost down = WorkerTestHost.Create(db, b => b.AddJobHandler<ProbeHandler>(), o => o.Worker.Enabled = false, unreachable);
        HealthCheckResult unhealthy = await down.HealthAsync();
        unhealthy.Status.Should().Be(HealthStatus.Unhealthy);
        unhealthy.Exception.Should().NotBeNull("the cause is reported (SqlClient/EF surface it as DbException or InvalidOperationException)");
    }

    private static Dictionary<string, object?> Tags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        Dictionary<string, object?> result = [with(StringComparer.Ordinal)];
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            result[tag.Key] = tag.Value;
        }

        return result;
    }
}