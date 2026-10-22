using Microsoft.Extensions.Configuration;
using NinjaQueue.EntityFrameworkCore;
using NinjaQueue.EntityFrameworkCore.Migrations;

namespace NinjaQueue.IntegrationTests.Schema;

/// <summary>
/// NinjaQueue's EF Core migrations on PostgreSQL 16 and SQL Server 2022 (RCSI ON and OFF): the pinned schema contract (G13),
/// up and down on clean and existing databases, parity with the EF model, a custom schema name, and the migrator an
/// application uses (migrate on start, idempotent script, uninstall).
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Gate", "G13")]
public sealed class SchemaMigrationTests(JobsDatabaseFixture fixture)
{
    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Clean_database_up_matches_the_pinned_schema_then_down_removes_the_tables(DatabaseKind kind)
    {
        TestDatabase db = await fixture.CreateDatabaseAsync(kind);

        db.MigrateUp();
        SchemaManifest.AssertMatches(await SchemaSnapshot.ReadAsync(db, JobSchemaName.Default));
        await AssertSeededLeasesAsync(db, JobSchemaName.Default);
        (await SchemaSnapshot.TablesAsync(db, JobSchemaName.Default)).Should().Contain(NinjaQueueDbContextOptionsExtensions.MigrationsHistoryTable,
            "the migration history lives in NinjaQueue's own schema");

        db.MigrateDown();
        (await SchemaSnapshot.TablesAsync(db, JobSchemaName.Default)).Should().Equal(NinjaQueueDbContextOptionsExtensions.MigrationsHistoryTable);

        // Re-runnable: up after down gives the same schema again, and a second up is a no-op.
        db.MigrateUp();
        db.MigrateUp();
        SchemaManifest.AssertMatches(await SchemaSnapshot.ReadAsync(db, JobSchemaName.Default));
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Existing_database_keeps_its_own_tables_data_and_EF_history_through_up_and_down(DatabaseKind kind)
    {
        TestDatabase db = await fixture.CreateDatabaseAsync(kind);
        await db.ExecuteAsync("CREATE SCHEMA host_app");
        await db.ExecuteAsync("CREATE TABLE host_app.orders (id int NOT NULL PRIMARY KEY, reference varchar(50) NOT NULL)");
        await db.ExecuteAsync("INSERT INTO host_app.orders (id, reference) VALUES (1, 'A-1'), (2, 'A-2')");
        await db.ExecuteAsync(db.IsPostgres
            ? """CREATE TABLE public."__EFMigrationsHistory" ("MigrationId" varchar(150) NOT NULL PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL)"""
            : "CREATE TABLE dbo.__EFMigrationsHistory (MigrationId nvarchar(150) NOT NULL PRIMARY KEY, ProductVersion nvarchar(32) NOT NULL)");
        string history = db.IsPostgres ? "public.\"__EFMigrationsHistory\"" : "dbo.__EFMigrationsHistory";
        await db.ExecuteAsync($"INSERT INTO {history} VALUES ('20260101000000_AppInitial', '10.0.0')");

        db.MigrateUp();
        SchemaManifest.AssertMatches(await SchemaSnapshot.ReadAsync(db, JobSchemaName.Default));
        (await db.ScalarAsync<int>("SELECT count(*) FROM host_app.orders")).Should().Be(2);
        (await db.ScalarAsync<int>($"SELECT count(*) FROM {history}")).Should().Be(1, "the application's own EF history is never touched");

        db.MigrateDown();
        (await db.ScalarAsync<int>("SELECT count(*) FROM host_app.orders")).Should().Be(2);
        (await db.ScalarAsync<int>($"SELECT count(*) FROM {history}")).Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Ef_model_builds_exactly_the_migration_schema(DatabaseKind kind)
    {
        TestDatabase migrated = await fixture.CreateDatabaseAsync(kind);
        migrated.MigrateUp();

        TestDatabase modelBuilt = await fixture.CreateDatabaseAsync(kind);
        await using (NinjaQueueDbContext context = modelBuilt.CreateNinjaQueueContext())
        {
            await context.Database.EnsureCreatedAsync();
        }

        SchemaSnapshot expected = await SchemaSnapshot.ReadAsync(migrated, JobSchemaName.Default);
        SchemaSnapshot actual = await SchemaSnapshot.ReadAsync(modelBuilt, JobSchemaName.Default);

        SchemaManifest.AssertMatches(actual);
        actual.CheckDefinitions.Should().Equal(expected.CheckDefinitions, "the EF model and the migration share the CHECK SQL");
        actual.IndexFilters.Should().Equal(expected.IndexFilters, "the EF model and the migration share the filter SQL");
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_custom_schema_name_gets_the_same_schema_and_its_own_history(DatabaseKind kind)
    {
        const string schema = "acme_jobs";
        TestDatabase db = await fixture.CreateDatabaseAsync(kind);

        db.MigrateUp(schema: schema);

        SchemaManifest.AssertMatches(await SchemaSnapshot.ReadAsync(db, schema));
        await AssertSeededLeasesAsync(db, schema);
        (await SchemaSnapshot.TablesAsync(db, schema)).Should().Contain(NinjaQueueDbContextOptionsExtensions.MigrationsHistoryTable);
        (await SchemaSnapshot.SchemaExistsAsync(db, JobSchemaName.Default)).Should().BeFalse("nothing is created outside the configured schema");
        if (!db.IsPostgres)
        {
            (await db.ScalarAsync<string>($"SELECT lock_escalation_desc FROM sys.tables WHERE object_id = OBJECT_ID('{schema}.job_queue')"))
                .Should().Be("DISABLE", "a claim never escalates to a job_queue table lock");
        }
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task The_application_migrator_migrates_scripts_and_uninstalls(DatabaseKind kind)
    {
        TestDatabase db = await fixture.CreateDatabaseAsync(kind);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddTestHostPersistence(db);
        services.AddNinjaQueue(new ConfigurationBuilder().Build()).UseTestDatabase(db);
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        INinjaQueueSchemaMigrator migrator = scope.ServiceProvider.GetRequiredService<INinjaQueueSchemaMigrator>();

        await migrator.MigrateAsync();
        await migrator.MigrateAsync();
        SchemaManifest.AssertMatches(await SchemaSnapshot.ReadAsync(db, JobSchemaName.Default));
        (await migrator.GetAppliedMigrationsAsync()).Should().Equal("20261022000000_InitialSchema", "20261023000000_Dashboard");

        string script = await migrator.GenerateScriptAsync();
        script.Should().Contain("background_jobs").And.Contain(NinjaQueueDbContextOptionsExtensions.MigrationsHistoryTable)
            .And.Contain("20261022000000_InitialSchema").And.Contain("dashboard_users");

        await migrator.UninstallAsync();
        (await SchemaSnapshot.SchemaExistsAsync(db, JobSchemaName.Default)).Should().BeFalse("uninstall removes the tables, the history and the schema");
    }

    private static async Task AssertSeededLeasesAsync(TestDatabase db, string schema)
    {
        List<object?[]> rows = await db.QueryAsync($"SELECT name, owner, token, expires_at_utc FROM {schema}.job_leases ORDER BY name");
        rows.Select(r => (string)r[0]!).Should().Equal(SchemaManifest.SeededLeases);
        rows.Should().OnlyContain(r => r[1] == null && r[2] == null);
        rows.Select(r => ToUtc(r[3]!)).Should().OnlyContain(x => x == DateTimeOffset.UnixEpoch, "seeded leases start expired (§15.9)");
    }

    private static DateTimeOffset ToUtc(object value) => value switch
    {
        DateTimeOffset dto => dto.ToUniversalTime(),
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
        _ => throw new InvalidOperationException(value.GetType().Name),
    };
}