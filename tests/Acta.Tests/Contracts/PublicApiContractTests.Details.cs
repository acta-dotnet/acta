using Xunit;

namespace Acta.Tests.Contracts;

// A tuple element's name, a nullable reference and a notnull constraint are attributes beside a signature, not types in
// it, so a renderer that reads the types alone prints one line before and after any of them changes. Each declaration
// here is one member, and each differs from its neighbour in one such detail: the lines they render differ too.
public sealed partial class PublicApiContractTests
{
    private interface INamedTuple
    {
        ValueTask<(bool Found, T? Value)> M<T>();
    }

    private interface IRenamedTuple
    {
        ValueTask<(bool Exists, T? Value)> M<T>();
    }

    private interface IUnnamedTuple
    {
        ValueTask<(bool, T?)> M<T>();
    }

    private interface ITupleOfNotNull
    {
        ValueTask<(bool Found, T Value)> M<T>();
    }

    private interface INullableReferences
    {
        string? M(string? key, out string? text, string?[]? rest);
    }

    private interface INotNullReferences
    {
        string M(string key, out string text, string[] rest);
    }

    private interface IValueTypes
    {
        KeyValuePair<string, string?>? M<T>(int? count, (T First, string? Second) pair, Dictionary<string, List<string?>> map)
            where T : struct;
    }

    private interface INullableProperty
    {
        IReadOnlyList<string?>? M { get; }
    }

    private interface INotNullProperty
    {
        IReadOnlyList<string> M { get; }
    }

    private interface INullableIndex
    {
        string this[string? key] { get; }
    }

    private interface INotNullIndex
    {
        string this[string key] { get; }
    }

    private interface INotNullBesideAType
    {
        T M<T>()
            where T : notnull, IDisposable;
    }

    private interface ITypeConstraint
    {
        T M<T>()
            where T : IDisposable;
    }

    private interface INotNullConstraint
    {
        T M<T>()
            where T : notnull;
    }

    private interface INoConstraint
    {
        T M<T>();
    }

    private interface IClassConstraint
    {
        T M<T>()
            where T : class;
    }

    private interface INullableClassConstraint
    {
        T M<T>()
            where T : class?;
    }

    private const string Tuple = "System.Threading.Tasks.ValueTask<System.ValueTuple<";

    [Theory]
    [InlineData(typeof(INamedTuple), Tuple + "System.Boolean Found, T? Value>> M<T>()")]
    [InlineData(typeof(IRenamedTuple), Tuple + "System.Boolean Exists, T? Value>> M<T>()")]
    [InlineData(typeof(IUnnamedTuple), Tuple + "System.Boolean, T?>> M<T>()")]
    [InlineData(typeof(ITupleOfNotNull), Tuple + "System.Boolean Found, T Value>> M<T>()")]
    [InlineData(typeof(INullableReferences), "System.String? M(System.String? key, out System.String? text, System.String?[]? rest)")]
    [InlineData(typeof(INotNullReferences), "System.String M(System.String key, out System.String text, System.String[] rest)")]
    [InlineData(
        typeof(IValueTypes),
        "System.Nullable<System.Collections.Generic.KeyValuePair<System.String, System.String?>> M<T>(System.Nullable<System.Int32> count, "
            + "System.ValueTuple<T First, System.String? Second> pair, "
            + "System.Collections.Generic.Dictionary<System.String, System.Collections.Generic.List<System.String?>> map) where T : struct"
    )]
    [InlineData(typeof(INullableProperty), "System.Collections.Generic.IReadOnlyList<System.String?>? M { get; }")]
    [InlineData(typeof(INotNullProperty), "System.Collections.Generic.IReadOnlyList<System.String> M { get; }")]
    [InlineData(typeof(INullableIndex), "System.String this[System.String?] { get; }")]
    [InlineData(typeof(INotNullIndex), "System.String this[System.String] { get; }")]
    [InlineData(typeof(INotNullBesideAType), "T M<T>() where T : notnull, System.IDisposable")]
    [InlineData(typeof(ITypeConstraint), "T M<T>() where T : System.IDisposable")]
    [InlineData(typeof(INotNullConstraint), "T M<T>() where T : notnull")]
    [InlineData(typeof(INoConstraint), "T M<T>()")]
    [InlineData(typeof(IClassConstraint), "T M<T>() where T : class")]
    [InlineData(typeof(INullableClassConstraint), "T M<T>() where T : class?")]
    public void A_detail_metadata_keeps_beside_a_signature_is_on_its_line(Type declaration, string line) =>
        Assert.Equal([line], Members(declaration));
}
