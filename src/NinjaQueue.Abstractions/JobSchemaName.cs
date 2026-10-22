using System.Text.RegularExpressions;

namespace NinjaQueue;

/// <summary>The database schema that holds the job tables. It is interpolated into SQL, so it must be a plain lowercase
/// identifier.</summary>
public static partial class JobSchemaName
{
    /// <summary>The default schema name.</summary>
    public const string Default = "ninjaqueue";

    /// <summary>Returns <paramref name="schema"/> if it is a lowercase identifier (letters, digits, underscore; 1–63
    /// characters, not starting with a digit), and throws otherwise.</summary>
    public static string Validate(string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        return IdentifierPattern().IsMatch(schema)
            ? schema
            : throw new ArgumentException($"Schema '{schema}' must be a lowercase identifier: [a-z_][a-z0-9_]*, at most 63 characters.", nameof(schema));
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}