namespace Acta;

/// <summary>
/// Provider-portable default-value selector for <see cref="DbColumnAttribute.Default"/>; the framework renders the native
/// DEFAULT clause. <see cref="None"/> emits none (the caller supplies the value at INSERT); <see cref="UtcNow"/> renders
/// <c>SYSUTCDATETIME()</c> / <c>now()</c>; <see cref="Zero"/> <c>0</c>. Each value suits only matching <see cref="DbKind"/>s
/// (<see cref="UtcNow"/> needs <see cref="DbKind.UtcInstant"/>), and the generator emits <c>ACTA0403</c> on a mismatch. An
/// INSERT into a column with a non-<see cref="None"/> default must omit it from the column list and let the DEFAULT fire.
/// </summary>
internal enum DbDefault
{
    /// <summary>No DEFAULT emitted. The caller must supply the value at INSERT.</summary>
    None = 0,

    /// <summary>DB UTC at row creation. Compatible only with <see cref="DbKind.UtcInstant"/>.</summary>
    UtcNow = 1,

    /// <summary>Numeric zero. Compatible with the integer kinds.</summary>
    Zero = 2,
}
