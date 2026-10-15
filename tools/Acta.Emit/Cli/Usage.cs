namespace Acta.Emit.Cli;

internal static class Usage
{
    internal static int Print()
    {
        Console.WriteLine(
            """
            Acta.Emit: emits committed repo artifacts from the IEntity source of truth.

            Usage:
              dotnet run --project tools/Acta.Emit -- docs
              dotnet run --project tools/Acta.Emit -- check
              dotnet run --project tools/Acta.Emit -- schema reset --force
              dotnet run --project tools/Acta.Emit -- schema add --name add_tenant

            Subcommands:
              docs                         Emit docs/reference/data-model.md + docs/reference/code-families.md
              check                        Verify docs are current AND the snapshot equals the live model
              schema reset [--force]       Delete all migrations + the snapshot (deletes only; --force required)
              schema add [--name <n>]      Emit M{yyyyMMddHHmmss}_{n}.sql (UTC now) for every provider; save the snapshot
                                           (--name is snake_case, required once a baseline exists and refused before it;
                                           the baseline is "init")

            To redo a migration that has not shipped: git checkout the snapshot, delete the migration's files, schema add again.
            """
        );
        return 0;
    }

    internal static int Unknown(string[] args)
    {
        Console.Error.WriteLine($"Unrecognized arguments: {string.Join(' ', args)}");
        Console.Error.WriteLine("Run with --help for usage.");
        return 2;
    }
}
