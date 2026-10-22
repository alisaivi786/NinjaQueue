using System.Globalization;
using System.Security.Cryptography;

namespace NinjaQueue.Dashboard;

/// <summary>PBKDF2-SHA256 password hashes (OWASP 2023: 600,000 iterations), in the form <c>pbkdf2-sha256$iterations$salt$hash</c>.</summary>
internal static class DashboardPasswordHasher
{
    private const string Prefix = "pbkdf2-sha256";
    private const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    // At most this many hashes run at once: each costs about 0.3 s of CPU by design, so a flood of sign-ins queues here
    // instead of starving the host's own work.
    private static readonly SemaphoreSlim Gate = new(Math.Max(2, Environment.ProcessorCount / 2));

    // Verified against when the user does not exist, so an unknown name costs as much time as a wrong password.
    private static readonly Lazy<string> DummyHash = new(() => Hash(Guid.NewGuid().ToString("N")));

    /// <summary><see cref="Verify"/> behind the concurrency gate (the sign-in and password-change paths).</summary>
    public static async Task<bool> VerifyAsync(string password, string? stored, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return Verify(password, stored);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Join('$', Prefix, Iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool Verify(string password, string? stored)
    {
        string[] parts = (stored ?? DummyHash.Value).Split('$');
        if (parts.Length != 4 || parts[0] != Prefix || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int iterations)
            || iterations < 100_000)
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] expected = Convert.FromBase64String(parts[3]);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password ?? string.Empty, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected) && stored is not null;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}