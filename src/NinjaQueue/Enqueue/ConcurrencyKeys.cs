using System.Globalization;

namespace NinjaQueue.Enqueue;

/// <summary>
/// The running-concurrency keys stored in <c>job_queue.concurrency_key</c> (§14.5). A caller's key is stored as is. A job of a
/// type with <see cref="JobTypeAttribute.MaxRunningPerTenant"/> = N is enqueued with the slot <b>group</b>
/// <c>ninja:slots:{N}:{type}</c>; the claim leases it under one free slot key <c>{group}#{k}</c> (0 ≤ k &lt; N), so the
/// unique index on running keys caps the tenant at N running jobs of the type. The <c>ninja:</c> prefix is reserved.
/// </summary>
public static class ConcurrencyKeys
{
    /// <summary>The reserved prefix callers cannot use.</summary>
    public const string ReservedPrefix = "ninja:";

    /// <summary>The prefix of a slot group.</summary>
    public const string SlotPrefix = "ninja:slots:";

    /// <summary>The highest per-tenant cap.</summary>
    public const int MaxSlots = 100;

    /// <summary>The slot group for <paramref name="jobType"/> capped at <paramref name="slots"/>.</summary>
    public static string SlotGroup(string jobType, int slots) => string.Create(CultureInfo.InvariantCulture, $"{SlotPrefix}{slots}:{jobType}");

    /// <summary>The longest slot group plus its <c>#k</c> suffix for a code of <paramref name="jobTypeLength"/> characters.</summary>
    public static int MaxSlotKeyLength(int jobTypeLength) => SlotPrefix.Length + 3 + 1 + jobTypeLength + 3;

    /// <summary>The slot key for slot <paramref name="slot"/> of <paramref name="group"/>.</summary>
    public static string SlotKey(string group, int slot) => string.Create(CultureInfo.InvariantCulture, $"{group}#{slot}");

    /// <summary>
    /// When <paramref name="key"/> is a slot group or slot key, returns true with its group and slot count. A former slot key
    /// (a slot job that was leased once and went back to the queue) maps back to its group.
    /// </summary>
    public static bool TryParseSlot(string key, out string group, out int slots)
    {
        ArgumentNullException.ThrowIfNull(key);
        group = "";
        slots = 0;
        if (!key.StartsWith(SlotPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        int hash = key.IndexOf('#', StringComparison.Ordinal);
        group = hash < 0 ? key : key[..hash];
        int colon = group.IndexOf(':', SlotPrefix.Length);
        return colon > SlotPrefix.Length
            && int.TryParse(group.AsSpan(SlotPrefix.Length, colon - SlotPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out slots)
            && slots is >= 1 and <= MaxSlots;
    }
}