namespace Acta.Emit.Cli;

internal static class Usage
{
    internal static int Print()
    {
        Console.WriteLine(
            """
            Acta.Emit — emits committed repo artifacts from the IEntity source of truth.

            Usage:
              dotnet run --project tools/Acta.Emit -- docs
              dotnet run --project tools/Acta.Emit -- check
              dotnet run --project tools/Acta.Emit -- schema reset --force
              dotnet run --project tools/Acta.Emit -- schema add --name add_tenant
              dotnet run --project tools/Acta.Emit -- schema amend

            Subcommands:
              docs                         Emit docs/reference/data-model.md + docs/reference/code-families.md
              check                        Verify docs are current AND the snapshot equals the live model
              schema reset [--force]       Delete all migrations + the snapshot (deletes only; --force required)
              schema add [--name <n>]      Emit the next migration M{N} for every provider; advance the snapshot
                                           (name optional → genesis "init", else "change"; snake_case)
              schema amend [--name <n>]    Rewrite the tip migration M{N} in place (keeps each provider's name;
                                           --name to rename)
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
