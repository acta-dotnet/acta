using Acta.Emit.Shared;

namespace Acta.Emit.Features.Migrations;

/// <summary>
/// Records this build's object package for every provider and regenerates the hashes the installer
/// writes. Run it after editing any view or routine body; <c>Acta.Emit check</c> refuses a released
/// identity whose content moved, and names this command.
/// </summary>
internal static class ObjectsRecordCommand
{
    internal static int Run()
    {
        var repoRoot = RepoRoot.Find();
        ObjectPackageLedger.Record(repoRoot);
        ObjectPackageEmitter.Write(repoRoot);
        return 0;
    }
}
