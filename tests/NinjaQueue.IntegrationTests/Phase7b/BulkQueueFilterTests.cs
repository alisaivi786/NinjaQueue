using NinjaQueue.Admin;
using NinjaQueue.IntegrationTests.Phase4;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase7b;

/// <summary>
/// The bulk filter's queue selector (§17.9): <see cref="BulkJobFilter.Queue"/> narrows the keyset scan exactly like
/// <see cref="JobQuery.Queue"/> narrows the list, alone or combined with the other filters, within the caller's scopes.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "7b")]
[Trait("Gate", "G15")]
public sealed class BulkQueueFilterTests(JobsDatabaseFixture fixture)
{
    private const string Reports = "reports";

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_bulk_filter_with_a_queue_selects_only_that_queues_jobs(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        JobId defaultA = await host.EnqueueAsync(new P7bTenantPayload(P7b.TenantA, "a"), new JobOptions { TenantId = P7b.TenantA });
        JobId reportsA = await host.EnqueueAsync(new P7bTenantPayload(P7b.TenantA, "r"), new JobOptions { TenantId = P7b.TenantA, Queue = Reports });
        JobId reportsB = await host.EnqueueAsync(new P7bTenantPayload(P7b.TenantB, "rb"), new JobOptions { TenantId = P7b.TenantB, Queue = Reports });

        (await FindAsync(host, new BulkJobFilter { Queue = Reports }, null)).Should().BeEquivalentTo(new[] { reportsA, reportsB });
        (await FindAsync(host, new BulkJobFilter { Queue = "default" }, null)).Should().Equal(defaultA);
        (await FindAsync(host, new BulkJobFilter(), null)).Should().HaveCount(3, "no queue means every queue");
        (await FindAsync(host, new BulkJobFilter { Queue = Reports, Status = JobStatus.Enqueued, JobType = P7b.Keyed }, null))
            .Should().HaveCount(2, "the queue combines with the other filters");
        (await FindAsync(host, new BulkJobFilter { Queue = Reports, JobType = P7b.Step }, null)).Should().BeEmpty();
        (await FindAsync(host, new BulkJobFilter { Queue = Reports }, [P7b.TenantA])).Should().Equal(new[] { reportsA }, "tenant scoping still applies");
        (await FindAsync(host, new BulkJobFilter { Queue = "missing" }, null)).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_queued_bulk_cancel_by_queue_leaves_other_queues_alone(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        JobId inDefault = await host.EnqueueAsync(new P7bTenantPayload(P7b.TenantA, "a"), new JobOptions { TenantId = P7b.TenantA });
        JobId inReports = await host.EnqueueAsync(new P7bTenantPayload(P7b.TenantA, "r"), new JobOptions { TenantId = P7b.TenantA, Queue = Reports });
        host.Get<TestTenants>().AllowedTenantScopes = [P7b.TenantA];

        BulkSubmission queued = await host.AdminAsync(a => a.BulkAsync(
            new BulkRequest
            {
                Operation = AdminOperationKind.BulkCancel,
                Filter = new BulkJobFilter { Queue = Reports },
                Reason = "drain the reports queue",
                IdempotencyKey = "queue-filter-1",
            },
            P7b.Actor));
        queued.Kind.Should().Be(BulkSubmissionKind.Queued);

        host.Get<TestTenants>().AllowedTenantScopes = null;
        (await host.RunOneAsync("NinjaQueue.Admin.BulkOperation.v1")).Should().Be(Execution.ExecutionResult.Succeeded);
        BulkOperationReport report = (await host.AdminAsync(a => a.GetBulkOperationAsync(queued.OperationId!.Value)))!;
        (report.Status, report.Requested, report.Applied).Should().Be((AdminOperationStatus.Completed, 1, 1));
        (await host.StatusAsync(inReports)).Should().Be(JobStatus.Cancelled);
        (await host.StatusAsync(inDefault)).Should().Be(JobStatus.Enqueued);
        await P7b.AssertInvariantsAsync(host.Database);
    }

    private static async Task<List<JobId>> FindAsync(Phase4Host host, BulkJobFilter filter, IReadOnlyCollection<Guid>? scopes)
    {
        IReadOnlyList<(long Id, JobId JobId)> found = await host.InScopeAsync(sp =>
            sp.GetRequiredService<IJobAdministrationStore>().FindBulkJobsAsync(filter, scopes, 0, 50, CancellationToken.None));
        return [.. found.Select(x => x.JobId)];
    }
}