namespace Acta;

/// <summary>
/// <see cref="SchedulesOperations.AdmitFiringsAsync"/>: the one atomic apply of an admission plan, lock order
/// documented in <c>docs/sql-internals.md</c> ("Admission"). Answers which slots applied; the admitter replans the rest.
/// </summary>
internal sealed class AdmitFiringsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<AdmitFiringsResponse> HandleAsync(AdmitFiringsCommand command, CancellationToken ct) =>
        new(
            await session.ExecuteAsync(
                new StoreCommand("AdmitFirings"),
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Namespace.NamespaceId, command.NamespaceId));
                    dialect.BindAdmitFirings(cmd, command);
                },
                DbProjectionResolver.Resolve<AdmitFiringsRow>(),
                ct
            )
        );
}
