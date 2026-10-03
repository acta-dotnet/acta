namespace Acta.Runtime;

/// <summary><see cref="ISettings.GetAsync"/>: checks the name and the scope targets, then one point read.</summary>
internal sealed class GetSettingHandler(IDbSession session, ISqlDialect dialect)
{
    public ValueTask<SettingDetail?> HandleAsync(GetSettingQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query.Name, "name");
        // Reads check the shape only and accept the sys prefix.
        IdentifierSyntax.ValidateDottedKebab(query.Name, "name", AdminTextLimits.SettingName);
        SettingScope.Validate(query.NamespaceName, query.JobName);

        return new(
            session.QuerySingleAsync(
                "GetSetting",
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Setting.Name, query.Name));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ScopeNamespaceName, query.NamespaceName));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ScopeJobName, query.JobName));
                },
                DbProjectionResolver.Resolve<SettingDetail>(),
                ct
            )
        );
    }
}
