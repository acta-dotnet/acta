namespace System.Runtime.CompilerServices;

/// <summary>
/// Compiler shim for init-only setters. The generator's <c>readonly record struct</c> wire
/// types emit init accessors, which the C# compiler binds against this type; netstandard2.0
/// (required for Roslyn components, RS1041) does not ship it.
/// </summary>
#pragma warning disable S2094 // Compiler shim: the compiler only needs the named marker type.
internal static class IsExternalInit { }
#pragma warning restore S2094
