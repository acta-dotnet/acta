using Acta.Emit.Features.Docs;
using Acta.Emit.Features.Migrations;
using Acta.Emit.Features.Verify;

namespace Acta.Emit.Cli;

internal static class CommandRouter
{
    internal static int Run(string[] args) =>
        args switch
        {
            [] or ["--help"] or ["-h"] => Usage.Print(),
            ["docs"] => DocsCommand.Run(),
            ["check"] => CheckCommand.Run(),
            ["schema", "reset"] => SchemaResetCommand.Run(force: false),
            ["schema", "reset", "--force"] => SchemaResetCommand.Run(force: true),
            ["schema", "add"] => SchemaAddCommand.Run(name: null),
            ["schema", "add", "--name", var n] => SchemaAddCommand.Run(n),
            ["schema", "amend"] => SchemaAmendCommand.Run(name: null),
            ["schema", "amend", "--name", var n] => SchemaAmendCommand.Run(n),
            _ => Usage.Unknown(args),
        };
}
