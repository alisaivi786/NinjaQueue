using NinjaQueue.Registry;

namespace NinjaQueue.IntegrationTests.Phase3;

/// <summary>
/// Phase 3 exit (§9): outbox atomicity, the ambient-transaction rule, tenant-scoped idempotency and unique keys, rejection of
/// unknown codes and of the Release 2 options, on PostgreSQL and SQL Server RCSI ON and OFF.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "3")]
public sealed class EnqueueTests(JobsDatabaseFixture fixture)
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    private Task<JobsHost> HostAsync(DatabaseKind kind)
        => JobsHost.StartAsync(fixture, kind, b => b.AddJobHandler<CreateWidgetsHandler>().AddJobHandler<SystemPingHandler>());

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Staged_job_commits_and_rolls_back_with_the_business_write(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);

        JobId committed = await host.StageInTransactionAsync(new WidgetPayload(TenantA, "w"), new JobOptions { TenantId = TenantA }, widget: "business-1");
        (await host.StatusAsync(committed)).Should().Be(JobStatus.Enqueued);
        (await host.CountQueueAsync(committed)).Should().Be(1);
        (await host.EventsAsync(committed)).Should().Equal(JobEventCodes.JobEnqueued);
        (await host.CountWidgetsAsync("business-1")).Should().Be(1);

        Func<Task> rolledBack = () => host.StageInTransactionAsync(new WidgetPayload(TenantA, "w"), new JobOptions { TenantId = TenantA }, widget: "business-2", failBeforeCommit: true);
        await rolledBack.Should().ThrowAsync<InvalidOperationException>();
        (await host.CountWidgetsAsync("business-2")).Should().Be(0);
        (await host.CountJobsAsync()).Should().Be(1, "the job rolled back with the business write");
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Stage_without_a_transaction_is_refused_and_writes_nothing(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);

        Func<Task> noUnitOfWork = () => host.InScopeAsync(sp => sp.GetRequiredService<IJobEnqueuer>().StageAsync(new SystemPingPayload("x")));
        await noUnitOfWork.Should().ThrowAsync<NoAmbientTransactionException>();

        Func<Task> noTransaction = () => host.InScopeAsync(sp => sp.GetRequiredService<AppUnitOfWork>()
            .UseAsync((_, ct) => sp.GetRequiredService<IJobEnqueuer>().StageAsync(new SystemPingPayload("x"), null, ct)));
        await noTransaction.Should().ThrowAsync<NoAmbientTransactionException>();

        (await host.CountJobsAsync()).Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Idempotency_key_dedupes_per_tenant(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        JobOptions a = new() { TenantId = TenantA, IdempotencyKey = "provision:r1" };

        JobId first = await host.EnqueueAsync(new WidgetPayload(TenantA, "w"), a);
        JobId again = await host.StageInTransactionAsync(new WidgetPayload(TenantA, "w"), a);
        JobId otherTenant = await host.EnqueueAsync(new WidgetPayload(TenantB, "w"), a with { TenantId = TenantB });

        again.Should().Be(first);
        otherTenant.Should().NotBe(first);
        (await host.CountJobsAsync()).Should().Be(2);
        (await host.EventsAsync(first)).Should().Equal(JobEventCodes.JobEnqueued, JobEventCodes.JobDeduplicated);

        Func<Task> strict = () => host.EnqueueAsync(new WidgetPayload(TenantA, "w"), a with { OnIdempotencyConflict = IdempotencyConflict.Throw });
        (await strict.Should().ThrowAsync<JobConflictException>()).Which.ExistingJobId.Should().Be(first);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Concurrent_enqueues_with_one_idempotency_key_create_one_job(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        JobOptions options = new() { TenantId = TenantA, IdempotencyKey = "race:1" };

        JobId[] ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => host.EnqueueAsync(new WidgetPayload(TenantA, "w"), options)));

        ids.Distinct().Should().ContainSingle();
        (await host.CountJobsAsync()).Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Unique_key_allows_one_active_job_per_tenant_and_frees_on_completion(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        JobOptions unique = new() { TenantId = TenantA, UniqueKey = "company:provision" };

        JobId holder = await host.EnqueueAsync(new WidgetPayload(TenantA, "u"), unique);
        (await host.EnqueueAsync(new WidgetPayload(TenantA, "u2"), unique)).Should().Be(holder, "ReturnExisting is the default");
        Func<Task> reject = () => host.StageInTransactionAsync(new WidgetPayload(TenantA, "u3"), unique with { OnUniqueKeyConflict = UniqueKeyConflict.Reject });
        (await reject.Should().ThrowAsync<JobConflictException>()).Which.ExistingJobId.Should().Be(holder);
        JobId otherTenant = await host.EnqueueAsync(new WidgetPayload(TenantB, "u"), unique with { TenantId = TenantB });
        otherTenant.Should().NotBe(holder);
        (await host.CountJobsAsync()).Should().Be(2, "the rejected and returned enqueues left no rows");

        (await host.RunOneAsync("Test.Widgets.Create.v1")).Should().NotBeNull();
        (await host.RunOneAsync("Test.Widgets.Create.v1")).Should().NotBeNull();
        (await host.StatusAsync(holder)).Should().Be(JobStatus.Succeeded);

        JobId next = await host.EnqueueAsync(new WidgetPayload(TenantA, "u4"), unique);
        next.Should().NotBe(holder, "the reservation is released when the holder becomes terminal");
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Invalid_release_2_options_unknown_payloads_and_bad_tenancy_are_rejected(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);

        // Release 2 accepts ConcurrencyKey and ContinueAfter; invalid values are still rejected in the request.
        await FluentActions.Awaiting(() => host.EnqueueAsync(new WidgetPayload(TenantA, "x"), new JobOptions { TenantId = TenantA, ConcurrencyKey = "ninja:slots:1:x" }))
            .Should().ThrowAsync<ArgumentException>("the ninja: prefix is reserved");
        await FluentActions.Awaiting(() => host.EnqueueAsync(new WidgetPayload(TenantA, "x"), new JobOptions { TenantId = TenantA, ContinueAfter = new JobId(Guid.NewGuid()) }))
            .Should().ThrowAsync<ArgumentException>("the parent must exist in the same tenant");
        await FluentActions.Awaiting(() => host.EnqueueAsync(new ExternalPayload("x")))
            .Should().ThrowAsync<InvalidOperationException>("an unregistered payload fails in the request, not in the worker");
        await FluentActions.Awaiting(() => host.EnqueueAsync(new WidgetPayload(TenantA, "x")))
            .Should().ThrowAsync<ArgumentException>("a tenant-owned job needs a tenant");
        await FluentActions.Awaiting(() => host.EnqueueAsync(new SystemPingPayload("x"), new JobOptions { TenantId = TenantA }))
            .Should().ThrowAsync<ArgumentException>("a system job takes no tenant");
        (await host.CountJobsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task External_ledger_handler_without_an_ambiguity_policy_refuses_to_start()
    {
        await using JobsHost host = await JobsHost.StartAsync(fixture, DatabaseKind.Postgres, b => b.AddJobHandler<ExternalSendHandler>());
        FluentActions.Invoking(() => host.Get<JobHandlerRegistry>())
            .Should().Throw<JobRegistryException>().Which.Errors.Should().ContainSingle(e => e.Contains("OnAmbiguous", StringComparison.Ordinal));
    }
}