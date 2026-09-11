using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Acta.Emit.Shared.Sql;

/// <summary>
/// The baseline stamp a provider's full baseline migration carries: a content hash of that migration,
/// so a body and the generation it claims cannot disagree. Emission, the drift gate, and the tests all
/// derive it here, which is why there is no second copy of the rule to keep in step.
/// </summary>
internal static partial class BaselineStamp
{
    /// <summary>
    /// Stands in for the stamp while the body that determines it is hashed, and is substituted for the
    /// computed literal before the file is written. It never survives into a written migration.
    /// </summary>
    internal const string Token = "{{baseline-stamp}}";

    private const string Prefix = "baseline-";

    // 128 bits of the SHA-256 digest, hex. Long enough that two cuts cannot collide by accident,
    // short enough to read whole in a migrations row and in the bootstrap's refusal message.
    private const int HexLength = 32;

    /// <summary>
    /// The stamp <paramref name="body"/> determines. Accepts a body carrying either the token or an
    /// already-substituted literal: both canonicalize to the same bytes, so emitting a file and
    /// re-hashing the committed file give the same answer.
    /// </summary>
    internal static string Of(string body) =>
        Prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(body))))[..HexLength];

    /// <summary>Replaces the token in <paramref name="body"/> with the stamp that body hashes to.</summary>
    internal static string Substitute(string body) => body.Replace(Token, Of(body), StringComparison.Ordinal);

    /// <summary>The stamp literal <paramref name="body"/> records, or null when it carries none.</summary>
    internal static string? Recorded(string body) => Literal().Match(body) is { Success: true } match ? match.Value : null;

    // Canonical form: UTF-8 without a byte-order mark, LF line endings, the token in the stamp's
    // position. Nothing else is normalized, so every other byte of the file is part of the identity
    // the stamp names; only the two things a checkout can legitimately change are neutralized.
    private static string Canonicalize(string body) => Literal().Replace(body.TrimStart('﻿').ReplaceLineEndings("\n"), Token);

    [GeneratedRegex("baseline-[0-9a-f]{32}", RegexOptions.CultureInvariant)]
    private static partial Regex Literal();
}
