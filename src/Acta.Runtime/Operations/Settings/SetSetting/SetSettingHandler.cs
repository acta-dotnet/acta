using System.Text;
using Microsoft.Extensions.Options;

[assembly: Acta.Runtime.DbProjection(typeof(Acta.SetSettingResponse), ByName = true)]

namespace Acta.Runtime;

/// <summary>
/// <see cref="ISettings.SetAsync"/>: checks the name, scope targets, and description, encodes the value
/// as UTF-8 under the inline payload cap, and stamps the operator; the SQL does the rest.
/// </summary>
internal sealed class SetSettingHandler(IDbSession session, ISqlDialect dialect, IOptions<JobsOptions> options)
{
    public async ValueTask<SetSettingResponse> HandleAsync(SetSettingCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command.Name, "name");
        ArgumentNullException.ThrowIfNull(command.Value, "value");
        IdentifierSyntax.ValidateUserDottedKebab(command.Name, "name", AdminTextLimits.SettingName);
        SettingScope.Validate(command.NamespaceName, command.JobName);
        CatalogValidation.ValidateSetting(command.Description);

        var value = Encoding.UTF8.GetBytes(command.Value);
        var cap = options.Value.MaxInlinePayloadBytes;
        if (value.Length > cap)
        {
            throw new PayloadTooLargeException($"setting '{command.Name}'", value.Length, cap);
        }

        var actor = JobControlActor.Operator(command.ActorKey);

        return await session.ExecuteSingleAsync(
                new StoreCommand("SetSetting"),
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Setting.Name, command.Name));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Setting.ValueFormatId, JobPayloadFormat.Text.Id));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Setting.Value, value));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Setting.Description, command.Description));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ScopeNamespaceName, command.NamespaceName));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ScopeJobName, command.JobName));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorCode, actor.ActorCode));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorKey, actor.ActorKey));
                    cmd.Parameters.Add(
                        dialect.CreateParameter(
                            ActaSchema.JobEvent.ReasonMessage,
                            command.ReasonMessage.Truncate(ActaTextLimits.ReasonMessage)
                        )
                    );
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ExpectedRowVersionOptional, command.ExpectedVersion));
                },
                DbProjectionResolver.Resolve<SetSettingResponse>(),
                ct
            ) ?? throw new InvalidOperationException("SetSetting returned no rows; it must return exactly one (action, version) row.");
    }
}
