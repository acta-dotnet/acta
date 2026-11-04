namespace Acta;

/// <summary>
/// <see cref="SchedulesOperations.AdmitScheduleFiringsAsync"/>: the one atomic apply of an admission plan, lock order
/// documented in <c>docs/sql-internals.md</c> ("Admission"). Answers which slots applied; the admitter replans the rest.
/// </summary>
internal sealed class AdmitScheduleFiringsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<AdmitScheduleFiringsResponse> HandleAsync(AdmitScheduleFiringsCommand command, CancellationToken ct) =>
        new(
            await session.ExecuteAsync(
                new StoreCommand("AdmitScheduleFirings"),
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Namespace.NamespaceId, command.NamespaceId));
                    dialect.BindAdmitScheduleFirings(cmd, command);
                },
                DbProjectionResolver.Resolve<AdmitScheduleFiringsRow>(),
                ct
            )
        );
}
