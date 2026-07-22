using Acta.Generators.Relational;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Acta.Tests.Generators;

/// <summary>
/// Drives <see cref="ActaSchemaGenerator"/> through Roslyn over small in-memory compilations and
/// asserts the schema diagnostics it reports. The Db* attributes are internal to Acta.Relational.Schema, so the
/// test source declares stand-ins with the same metadata names; the generator matches by name.
/// </summary>
public class SchemaGeneratorDiagnosticTests
{
    private const string Stubs = """
        namespace Acta.Relational.Schema
        {
            using System;

            internal enum DbKind { Boolean, Byte, Int16, Int32, Int64, Guid, UtcInstant, Decimal, AsciiString, UnicodeString, Bytes, BinaryPayload }

            internal enum DbDefault { None, UtcNow, Zero, EmptyString, NewGuid }

            internal enum DbForeignKeyAction { NoAction, Cascade, SetNull }

            [AttributeUsage(AttributeTargets.Class)]
            internal sealed class DbTableAttribute(string name) : Attribute
            {
                public string Name { get; } = name;
                public string? View { get; init; }
                public bool PageCompression { get; init; }
            }

            [AttributeUsage(AttributeTargets.Property)]
            internal sealed class DbColumnAttribute : Attribute
            {
                public DbColumnAttribute(string name) { Name = name; }
                public DbColumnAttribute(string name, DbKind kind) { Name = name; Kind = kind; HasExplicitKind = true; }
                public string Name { get; }
                public DbKind Kind { get; }
                public bool HasExplicitKind { get; }
                public int Size { get; init; }
                public int Precision { get; init; }
                public int Scale { get; init; }
                public DbDefault Default { get; init; }
            }

            [AttributeUsage(AttributeTargets.Class)]
            internal sealed class DbPrimaryKeyAttribute : Attribute
            {
                public string Name { get; init; } = "";
                public string[] Columns { get; init; } = [];
                public bool Manual { get; init; }
                public bool OptimizeForSequentialKey { get; init; }
            }

            [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
            internal sealed class DbIndexAttribute : Attribute
            {
                public string Name { get; init; } = "";
                public string[] Columns { get; init; } = [];
                public string[]? Includes { get; init; }
                public string[]? Descending { get; init; }
                public string? Filter { get; init; }
            }

            [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
            internal sealed class DbUniqueIndexAttribute : Attribute
            {
                public string Name { get; init; } = "";
                public string[] Columns { get; init; } = [];
                public string[]? Includes { get; init; }
                public string[]? Descending { get; init; }
                public string? Filter { get; init; }
            }

            [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
            internal sealed class DbCheckAttribute : Attribute
            {
                public string Name { get; init; } = "";
                public string Sql { get; init; } = "";
            }

            [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
            internal sealed class DbForeignKeyAttribute : Attribute
            {
                public string Name { get; init; } = "";
                public Type Target { get; init; } = typeof(object);
                public string TargetColumn { get; init; } = "";
                public string Column { get; init; } = "";
                public DbForeignKeyAction OnDelete { get; init; }
            }

            [AttributeUsage(AttributeTargets.Property)]
            internal sealed class DbConcurrencyTokenAttribute : Attribute { }

            [AttributeUsage(AttributeTargets.Property)]
            internal sealed class DbIgnoreAttribute : Attribute { }

            [AttributeUsage(AttributeTargets.Enum)]
            internal sealed class CodeKindAttribute(string codeKind) : Attribute
            {
                public string CodeKind { get; } = codeKind;
            }
        }

        """;

    private static GeneratorDriverRunResult RunGenerator(string entitySource)
    {
        var referencePaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);

        var compilation = CSharpCompilation.Create(
            "SchemaGeneratorDiagnosticTests",
            [CSharpSyntaxTree.ParseText(Stubs + entitySource)],
            referencePaths.Select(p => MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        return CSharpGeneratorDriver.Create(new ActaSchemaGenerator()).RunGenerators(compilation).GetRunResult();
    }

    private static Diagnostic[] Of(GeneratorDriverRunResult result, string id) => result.Diagnostics.Where(d => d.Id == id).ToArray();

    // ----------------------------------------------------------------------------------------
    // ACTA0401: schema declarations must be complete
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Entity_without_primary_key_errors_ACTA0401()
    {
        var result = RunGenerator(
            """
            namespace GenTests;
            using Acta.Relational.Schema;

            [DbTable("widget")]
            internal sealed class Widget
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }
            }
            """
        );

        var error = Assert.Single(Of(result, "ACTA0401"));
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
    }

    [Fact]
    public void Pk_name_without_prefix_errors_ACTA0401()
    {
        var result = RunGenerator(
            """
            namespace GenTests;
            using Acta.Relational.Schema;

            [DbTable("widget")]
            [DbPrimaryKey(Name = "primary_widget", Columns = ["id"])]
            internal sealed class Widget
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0401"));
    }

    [Fact]
    public void Pk_column_not_declared_errors_ACTA0401()
    {
        var result = RunGenerator(
            """
            namespace GenTests;
            using Acta.Relational.Schema;

            [DbTable("widget")]
            [DbPrimaryKey(Name = "pk_widget", Columns = ["missing"])]
            internal sealed class Widget
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0401"));
    }

    [Fact]
    public void Index_with_unknown_column_errors_ACTA0401()
    {
        var result = RunGenerator(
            """
            namespace GenTests;
            using Acta.Relational.Schema;

            [DbTable("widget")]
            [DbPrimaryKey(Name = "pk_widget", Columns = ["id"])]
            [DbIndex(Name = "ix_widget_missing", Columns = ["missing"])]
            internal sealed class Widget
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0401"));
    }

    [Fact]
    public void Index_name_without_prefix_errors_ACTA0401()
    {
        var result = RunGenerator(
            """
            namespace GenTests;
            using Acta.Relational.Schema;

            [DbTable("widget")]
            [DbPrimaryKey(Name = "pk_widget", Columns = ["id"])]
            [DbIndex(Name = "idx_widget_id", Columns = ["id"])]
            internal sealed class Widget
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0401"));
    }

    [Fact]
    public void Duplicate_table_name_errors_ACTA0401()
    {
        var result = RunGenerator(
            """
            namespace GenTests;
            using Acta.Relational.Schema;

            [DbTable("widget")]
            [DbPrimaryKey(Name = "pk_widget", Columns = ["id"])]
            internal sealed class Widget
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }
            }

            [DbTable("widget")]
            [DbPrimaryKey(Name = "pk_widget2", Columns = ["id"])]
            internal sealed class WidgetTwo
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }
            }
            """
        );

        Assert.Equal(2, Of(result, "ACTA0401").Length);
    }

    [Fact]
    public void Fk_with_unknown_local_column_errors_ACTA0401()
    {
        var result = RunGenerator(
            """
            namespace GenTests;
            using Acta.Relational.Schema;

            [DbTable("parent")]
            [DbPrimaryKey(Name = "pk_parent", Columns = ["id"])]
            internal sealed class Parent
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }
            }

            [DbTable("child")]
            [DbPrimaryKey(Name = "pk_child", Columns = ["id"])]
            [DbForeignKey(Name = "fk_child_parent", Target = typeof(Parent), TargetColumn = "id", Column = "missing")]
            internal sealed class Child
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0401"));
    }

    [Fact]
    public void Fk_with_unknown_target_column_errors_ACTA0401()
    {
        var result = RunGenerator(
            """
            namespace GenTests;
            using Acta.Relational.Schema;

            [DbTable("parent")]
            [DbPrimaryKey(Name = "pk_parent", Columns = ["id"])]
            internal sealed class Parent
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }
            }

            [DbTable("child")]
            [DbPrimaryKey(Name = "pk_child", Columns = ["id"])]
            [DbForeignKey(Name = "fk_child_parent", Target = typeof(Parent), TargetColumn = "nope", Column = "parent_id")]
            internal sealed class Child
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }

                [DbColumn("parent_id", DbKind.Int64)]
                public long ParentId { get; set; }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0401"));
    }

    [Fact]
    public void Fk_target_without_DbTable_errors_ACTA0401()
    {
        var result = RunGenerator(
            """
            namespace GenTests;
            using Acta.Relational.Schema;

            internal sealed class NotAnEntity
            {
            }

            [DbTable("child")]
            [DbPrimaryKey(Name = "pk_child", Columns = ["id"])]
            [DbForeignKey(Name = "fk_child_parent", Target = typeof(NotAnEntity), TargetColumn = "id", Column = "parent_id")]
            internal sealed class Child
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }

                [DbColumn("parent_id", DbKind.Int64)]
                public long ParentId { get; set; }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0401"));
    }

    // ----------------------------------------------------------------------------------------
    // ACTA0402: column mappings must match the CLR type
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void String_column_without_size_errors_ACTA0402()
    {
        var result = RunGenerator(
            """
            namespace GenTests;
            using Acta.Relational.Schema;

            [DbTable("widget")]
            [DbPrimaryKey(Name = "pk_widget", Columns = ["id"])]
            internal sealed class Widget
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }

                [DbColumn("name", DbKind.AsciiString)]
                public string Name { get; set; } = "";
            }
            """
        );

        var error = Assert.Single(Of(result, "ACTA0402"));
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
    }

    [Fact]
    public void Decimal_column_without_precision_errors_ACTA0402()
    {
        var result = RunGenerator(
            """
            namespace GenTests;
            using Acta.Relational.Schema;

            [DbTable("widget")]
            [DbPrimaryKey(Name = "pk_widget", Columns = ["id"])]
            internal sealed class Widget
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }

                [DbColumn("rate", DbKind.Decimal)]
                public decimal Rate { get; set; }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0402"));
    }

    [Fact]
    public void Kind_clr_mismatch_errors_ACTA0402()
    {
        var result = RunGenerator(
            """
            namespace GenTests;
            using Acta.Relational.Schema;

            [DbTable("widget")]
            [DbPrimaryKey(Name = "pk_widget", Columns = ["id"])]
            internal sealed class Widget
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }

                [DbColumn("count", DbKind.Int32)]
                public string Count { get; set; } = "";
            }
            """
        );

        Assert.Single(Of(result, "ACTA0402"));
    }

    [Fact]
    public void Code_column_on_long_backed_enum_errors_ACTA0402()
    {
        var result = RunGenerator(
            """
            namespace GenTests;
            using Acta.Relational.Schema;

            [CodeKind("widget-status")]
            internal enum WidgetStatusCode : long
            {
                Ready = 1,
            }

            [DbTable("widget")]
            [DbPrimaryKey(Name = "pk_widget", Columns = ["id"])]
            internal sealed class Widget
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }

                [DbColumn("status_code")]
                public WidgetStatusCode StatusCode { get; set; }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0402"));
    }

    // ----------------------------------------------------------------------------------------
    // ACTA0403: column defaults must match the kind and not fight allocation
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void UtcNow_default_on_non_utc_column_errors_ACTA0403()
    {
        var result = RunGenerator(
            """
            namespace GenTests;
            using Acta.Relational.Schema;

            [DbTable("widget")]
            [DbPrimaryKey(Name = "pk_widget", Columns = ["id"])]
            internal sealed class Widget
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }

                [DbColumn("count", DbKind.Int32, Default = DbDefault.UtcNow)]
                public int Count { get; set; }
            }
            """
        );

        var error = Assert.Single(Of(result, "ACTA0403"));
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
    }

    [Fact]
    public void EmptyString_default_on_guid_column_errors_ACTA0403()
    {
        var result = RunGenerator(
            """
            namespace GenTests;
            using Acta.Relational.Schema;

            [DbTable("widget")]
            [DbPrimaryKey(Name = "pk_widget", Columns = ["id"])]
            internal sealed class Widget
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }

                [DbColumn("token", DbKind.Guid, Default = DbDefault.EmptyString)]
                public System.Guid Token { get; set; }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0403"));
    }

    [Fact]
    public void Default_on_identity_allocated_pk_errors_ACTA0403()
    {
        var result = RunGenerator(
            """
            namespace GenTests;
            using Acta.Relational.Schema;

            [DbTable("widget")]
            [DbPrimaryKey(Name = "pk_widget", Columns = ["id"])]
            internal sealed class Widget
            {
                [DbColumn("id", DbKind.Int64, Default = DbDefault.Zero)]
                public long Id { get; set; }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0403"));
    }

    // ----------------------------------------------------------------------------------------
    // Clean entity
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Valid_entities_produce_no_diagnostics()
    {
        var result = RunGenerator(
            """
            namespace GenTests;
            using Acta.Relational.Schema;

            [DbTable("parent")]
            [DbPrimaryKey(Name = "pk_parent", Columns = ["id"])]
            [DbUniqueIndex(Name = "ux_parent_name", Columns = ["name"])]
            internal sealed class Parent
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }

                [DbColumn("name", DbKind.AsciiString, Size = 64)]
                public string Name { get; set; } = "";

                [DbColumn("created_at", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
                public System.DateTime CreatedAt { get; set; }

                [DbColumn("version", DbKind.Int32, Default = DbDefault.Zero)]
                [DbConcurrencyToken]
                public int Version { get; set; }
            }

            [DbTable("child")]
            [DbPrimaryKey(Name = "pk_child", Columns = ["id"])]
            [DbIndex(Name = "ix_child_parent", Columns = ["parent_id"])]
            [DbForeignKey(Name = "fk_child_parent", Target = typeof(Parent), TargetColumn = "id", Column = "parent_id", OnDelete = DbForeignKeyAction.Cascade)]
            internal sealed class Child
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }

                [DbColumn("parent_id", DbKind.Int64)]
                public long ParentId { get; set; }
            }
            """
        );

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Index_name_longer_than_63_chars_errors_ACTA0401()
    {
        var longName = "ix_widget_" + new string('a', 60); // 70 chars > 63
        var result = RunGenerator(
            $$"""
            namespace GenTests;
            using Acta.Relational.Schema;

            [DbTable("widget")]
            [DbPrimaryKey(Name = "pk_widget", Columns = ["id"])]
            [DbIndex(Name = "{{longName}}", Columns = ["id"])]
            internal sealed class Widget
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }
            }
            """
        );

        var error = Assert.Single(Of(result, "ACTA0401"));
        Assert.Contains("70", error.GetMessage());
    }

    [Fact]
    public void Synthetic_byte_check_name_longer_than_63_chars_errors_ACTA0401()
    {
        // Column alone is 55 chars (legal); the derived ck_widget_{column}_byte is 70 chars.
        var col = new string('a', 55);
        var result = RunGenerator(
            $$"""
            namespace GenTests;
            using Acta.Relational.Schema;

            [DbTable("widget")]
            [DbPrimaryKey(Name = "pk_widget", Columns = ["id"])]
            internal sealed class Widget
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }

                [DbColumn("{{col}}", DbKind.Byte)]
                public byte B { get; set; }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0401"));
    }

    [Fact]
    public void Column_name_60_chars_with_multibyte_chars_errors_ACTA0401()
    {
        // 60 chars total (legal under a char-count guard), but 55 'a' + 5 'é' = 65 UTF-8 bytes > 63.
        var col = new string('a', 55) + new string('é', 5);
        var result = RunGenerator(
            $$"""
            namespace GenTests;
            using Acta.Relational.Schema;

            [DbTable("widget")]
            [DbPrimaryKey(Name = "pk_widget", Columns = ["id"])]
            internal sealed class Widget
            {
                [DbColumn("id", DbKind.Int64)]
                public long Id { get; set; }

                [DbColumn("{{col}}", DbKind.Int64)]
                public long B { get; set; }
            }
            """
        );

        var error = Assert.Single(Of(result, "ACTA0401"));
        Assert.Contains("65", error.GetMessage());
    }
}
