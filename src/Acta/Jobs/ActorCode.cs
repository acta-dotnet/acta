using System.Text.Json.Serialization;

namespace Acta;

[JsonConverter(typeof(ActorCodeJsonConverter))]
[CodeKind("actor")]
public enum ActorCode : byte
{
    [Code(
        "sys",
        "Acta-shipped system [Job] (a reserved name prefixed with sys.) OR Acta runtime path that decided a transition. ActorKey = \"sys:{jobNamespace}:{jobName}\" or \"sys:{component}\"."
    )]
    Sys = 10,

    [Code(
        "operator",
        "A human or admin tool acting from outside any job context (endpoint, dashboard, admin script). ActorKey carries the authenticated principal name as-is (e.g. http.User.Identity.Name); null when the host did not authenticate."
    )]
    Operator = 20,

    [Code("job", "ctx.* throw-to-transition inside a user handler. ActorKey = \"{jobId}\".")]
    Job = 50,

    [Code(
        "worker",
        "Worker-process lifecycle (registered / heartbeat / dead-marking) AND worker-mediated hot-path actions (claim, lease renewal). ActorKey stores the acting worker's public ref as canonical lowercase uuid text; read projections render it as wrk_...."
    )]
    Worker = 70,
}
