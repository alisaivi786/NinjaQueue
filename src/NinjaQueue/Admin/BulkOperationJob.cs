using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NinjaQueue.Admin;

/// <summary>The payload of the bulk-operation system job.</summary>
/// <param name="OperationId">The <c>job_admin_operations.guid_id</c> to run.</param>
public sealed record BulkOperationPayload(Guid OperationId);

/// <summary>
/// Runs a filter-based bulk admin operation (§17.9) as the engine's own job: keyset-paged, one transaction per job, resumable
/// from the stored cursor. Registered by <c>AddNinjaQueue</c>; one active per tenant through <c>unique_key = bulk-admin:{tenant}</c>.
/// </summary>
[JobType(Code, SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.System, MaxAttempts = 5, Timeout = "02:00:00")]
internal sealed class BulkOperationJobHandler(JobAdminService admin) : IJobHandler<BulkOperationPayload>
{
    /// <summary>The job code.</summary>
    public const string Code = "NinjaQueue.Admin.BulkOperation.v1";

    public async Task<JobResult> HandleAsync(BulkOperationPayload payload, JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await admin.RunBulkOperationAsync(payload.OperationId, cancellationToken);
        return JobResult.Success();
    }
}

/// <summary>Masks <see cref="SensitiveAttribute"/> properties (by their JSON names, nested objects included) with <c>"***"</c>.</summary>
internal sealed class DefaultJobPayloadRedactor : IJobPayloadRedactor
{
    public static readonly DefaultJobPayloadRedactor Instance = new();

    private const string Mask = "***";
    private static readonly ConcurrentDictionary<Type, (Dictionary<string, Type> Nested, HashSet<string> Sensitive)> Shapes = new();

    public string Redact(string jobType, Type payloadType, string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(payloadType);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(payloadJson);
        }
        catch (JsonException)
        {
            // An unreadable payload is never shown.
            return "{}";
        }

        Redact(root, payloadType, 0);
        return root?.ToJsonString() ?? "null";
    }

    private static void Redact(JsonNode? node, Type type, int depth)
    {
        if (node is not JsonObject obj || depth > 8)
        {
            return;
        }

        (Dictionary<string, Type> nested, HashSet<string> sensitive) = Shapes.GetOrAdd(type, Describe);
        foreach (string name in obj.Select(p => p.Key).ToList())
        {
            if (sensitive.Contains(name))
            {
                obj[name] = Mask;
            }
            else if (nested.TryGetValue(name, out Type? child))
            {
                Redact(obj[name], child, depth + 1);
            }
        }
    }

    private static (Dictionary<string, Type>, HashSet<string>) Describe(Type type)
    {
        Dictionary<string, Type> nested = [with(StringComparer.OrdinalIgnoreCase)];
        HashSet<string> sensitive = [with(StringComparer.OrdinalIgnoreCase)];
        ParameterInfo[] ctorParameters = type.GetConstructors().SelectMany(c => c.GetParameters()).ToArray();
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            string name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            bool isSensitive = property.GetCustomAttribute<SensitiveAttribute>() is not null
                || ctorParameters.Any(p => string.Equals(p.Name, property.Name, StringComparison.Ordinal) && p.GetCustomAttribute<SensitiveAttribute>() is not null);
            if (isSensitive)
            {
                sensitive.Add(name);
            }
            else if (property.PropertyType.IsClass && property.PropertyType != typeof(string) && !typeof(System.Collections.IEnumerable).IsAssignableFrom(property.PropertyType))
            {
                nested[name] = property.PropertyType;
            }
        }

        return (nested, sensitive);
    }
}