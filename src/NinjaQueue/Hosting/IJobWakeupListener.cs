namespace NinjaQueue.Hosting;

/// <summary>
/// The push wake-up listener as the health check sees it (PostgreSQL LISTEN/NOTIFY, Release 2). Implemented by the storage
/// provider; absent when the provider has none.
/// </summary>
public interface IJobWakeupListener
{
    /// <summary>True while the listener connection is open and listening.</summary>
    bool IsListening { get; }
}