using System.Reflection;
using NinjaQueue.EntityFrameworkCore;

namespace NinjaQueue.Tests;

/// <summary>G13 (part 2) and the architecture rules that do not need a database.</summary>
public sealed class SchemaDefinitionTests
{
    // §17.5: the canonical list, in the document's order.
    private static readonly string[] CanonicalEventCodes =
    [
        "JobEnqueued", "JobDeduplicated", "JobClaimed", "JobSucceeded", "JobRetryScheduled", "JobTimedOut", "JobCompletedAfterTimeout",
        "JobDeadLettered", "JobMaxAgeExceeded",
        "JobDeadLetterHandled", "JobDeadLetterHandlerFailed", "JobDeadLetterHandlerAbandoned",
        "JobLeaseExpired", "JobLeaseLost", "JobReleased", "JobSnoozed", "JobDeferred", "FencedStepKilled",
        "JobCancelRequested", "JobCancelled", "JobCancelTooLate",
        "JobRequeued", "JobReplaced", "BulkOperationApplied",
        "JobOrphaned", "JobOrphanCleared",
        "ContinuationReleased", "ContinuationCancelled", "ContinuationRestoreConflict",
        "RecurringSkippedOverlap", "RecurringPaused", "RecurringResumed", "RecurringTriggered",
        "SideEffectUnknown", "SideEffectDuplicateRisk",
        "FencedContractViolation", "JobStateConflict",
    ];

    [Fact]
    public void Event_codes_match_the_canonical_list()
    {
        JobEventCodes.All.Should().Equal(CanonicalEventCodes);

        string[] constants = typeof(JobEventCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.IsLiteral && x.FieldType == typeof(string))
            .Select(x => (string)x.GetRawConstantValue()!)
            .ToArray();
        constants.Should().BeEquivalentTo(CanonicalEventCodes, "every constant must be in All, and All must hold nothing else");
        CanonicalEventCodes.Should().OnlyContain(x => x.Length <= JobEventCodes.MaxLength);
    }

    [Fact]
    public void Check_constraints_are_built_from_the_pinned_values()
    {
        NinjaQueueSql.StatusCheck.Should().Be("status IN (1, 2, 3, 4, 10, 11, 12)");
        NinjaQueueSql.MaxAttemptsCheck.Should().Be("max_attempts BETWEEN 1 AND 1000");
        NinjaQueueSql.OutcomeCheck.Should().Be("outcome IS NULL OR outcome IN (1, 2, 3, 4, 5, 6, 7, 8)");
        NinjaQueueSql.TerminalReasonCheck.Should().Be("terminal_reason_code IS NULL OR terminal_reason_code IN (1, 2, 3, 4, 5, 6, 7, 8)");
        NinjaQueueSql.AwaitingFilter.Should().Be("status = 3");
        NinjaQueueSql.SideEffectStatusCheck.Should().Be("status IN (1, 2, 3, 4)");
    }

    [Theory]
    [InlineData("jobs")]
    [InlineData("ninjaqueue")]
    [InlineData("_j1")]
    public void Valid_schema_names_are_accepted(string schema) => JobSchemaName.Validate(schema).Should().Be(schema);

    [Theory]
    [InlineData("Jobs")]
    [InlineData("1jobs")]
    [InlineData("jobs;drop")]
    [InlineData("jobs.x")]
    [InlineData(" ")]
    public void Invalid_schema_names_are_rejected(string schema)
        => FluentActions.Invoking(() => JobSchemaName.Validate(schema)).Should().Throw<ArgumentException>();

    [Theory]
    [InlineData("GuidId", "guid_id")]
    [InlineData("AttemptsLeft", "attempts_left")]
    [InlineData("LeaseExpiresAtUtc", "lease_expires_at_utc")]
    [InlineData("Checkpoint", "checkpoint")]
    [InlineData("DurationMs", "duration_ms")]
    public void Column_names_are_snake_case(string property, string column)
        => NinjaQueueModelBuilderExtensions.ToSnakeCase(property).Should().Be(column);

    public static TheoryData<string> KitAssemblies() =>
    [
        typeof(JobStatus).Assembly.GetName().Name!,
        typeof(NinjaQueueModelBuilderExtensions).Assembly.GetName().Name!,
        "NinjaQueue.PostgreSql",
        "NinjaQueue.SqlServer",
        "NinjaQueue",
    ];

    // NinjaQueue is a standalone product: no application, no Ninja.Kit library, only framework, EF Core and database drivers.
    [Theory]
    [MemberData(nameof(KitAssemblies))]
    public void Kit_assemblies_do_not_reference_any_application_or_Ninja_Kit_library(string assemblyName)
        => Assembly.Load(assemblyName).GetReferencedAssemblies().Select(x => x.Name)
            .Should().NotContain(name => name != null
                && (name.StartsWith("OpsFusionHCM", StringComparison.Ordinal)
                    || name.StartsWith("FMSPrime", StringComparison.Ordinal)
                    || name.StartsWith("Ninja.MetaPlatform", StringComparison.Ordinal)
                    || name.StartsWith("Ninja.Kit", StringComparison.Ordinal)));

    // The same rule at the package level: a Ninja.Kit package can never be referenced, even one whose types are unused.
    [Fact]
    public void No_project_references_a_Ninja_Kit_package()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "NinjaQueue.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("NinjaQueue.slnx not found above the test output.");
        }

        string[] offenders = [.. Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Append(Path.Combine(root, "Directory.Packages.props"))
            .Append(Path.Combine(root, "Directory.Build.props"))
            .Where(path => File.ReadAllText(path).Contains("\"Ninja.Kit", StringComparison.Ordinal))];
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Abstractions_depend_on_nothing_but_the_framework()
        => typeof(JobStatus).Assembly.GetReferencedAssemblies().Select(x => x.Name)
            .Should().OnlyContain(name => name != null && (name.StartsWith("System", StringComparison.Ordinal) || name == "netstandard" || name == "mscorlib"));
}