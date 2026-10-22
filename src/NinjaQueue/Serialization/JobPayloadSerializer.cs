using System.Text;
using System.Text.Json;

namespace NinjaQueue.Serialization;

/// <summary>
/// Payload JSON (System.Text.Json, web defaults). The payload type always comes from the registry entry for the stored
/// job code, never from the stored data. Payloads carry ids, not employee data or secrets; their size is capped at enqueue.
/// </summary>
public sealed class JobPayloadSerializer(int maxPayloadBytes)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The size cap in UTF-8 bytes.</summary>
    public int MaxPayloadBytes { get; } = maxPayloadBytes;

    /// <summary>Serializes a payload and enforces the size cap.</summary>
    public string Serialize(object payload, Type payloadType)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(payloadType);
        string json = JsonSerializer.Serialize(payload, payloadType, JsonOptions);
        int bytes = Encoding.UTF8.GetByteCount(json);
        return bytes > MaxPayloadBytes
            ? throw new ArgumentException($"The {payloadType.Name} payload is {bytes} bytes; the limit is {MaxPayloadBytes}. Payloads carry ids, not data.", nameof(payload))
            : json;
    }

    /// <summary>Deserializes a stored payload. A failure is permanent (the job is dead-lettered).</summary>
    public static object Deserialize(string? json, Type payloadType)
    {
        ArgumentNullException.ThrowIfNull(payloadType);
        return JsonSerializer.Deserialize(json ?? "null", payloadType, JsonOptions)
            ?? throw new JsonException($"The stored payload deserialized to null for {payloadType.Name}.");
    }
}