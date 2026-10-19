using System.Security.Cryptography;
using System.Text;

namespace Acta.Emit.Shared.Sql;

/// <summary>One routine or operator view as a migration writes it: the installed name and the statement, without a batch separator.</summary>
internal sealed record SqlObject(string Name, string Statement)
{
    /// <summary>The snapshot's diff key: SHA-256 of the statement as written, with line endings normalized so a checkout cannot move it.</summary>
    internal string Hash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Statement.ReplaceLineEndings("\n"))));
}
