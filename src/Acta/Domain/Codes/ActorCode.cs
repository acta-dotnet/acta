using System.Text.Json.Serialization;

namespace Acta;

[JsonConverter(typeof(ActorCodeJsonConverter))]
[CodeKind("actor")]
public enum ActorCode : byte
{
    [Code("unspecified", "Actor not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code(
        "sys",
        "Acta-shipped system [Job] (a reserved name prefixed with sys.) OR Acta runtime path that decided a transition. Actor is null."
    )]
    Sys = 10,

    [Code(
        "operator",
        "A human or admin tool acting from outside any job context (endpoint, dashboard, admin script). Actor carries the authenticated principal name as-is (e.g. http.User.Identity.Name); null when the host did not authenticate."
    )]
    Operator = 20,

    [Code(
        "job",
        "An in-handler ctx call recorded as its own event: a ctx.NoteAsync note (Actor is null) or a ctx.ResetStateAsync state reset (Actor is the job id)."
    )]
    Job = 50,

    [Code(
        "worker",
        "Worker-process lifecycle (worker.started, worker.stopped, worker.died) and the events an attempt's completion writes, including a transition the handler asked for through ctx (reschedule, sleep or signal wait, fail, cancel, pause), whose job.handler-* reason names the handler as the decider. Actor is null; the worker is named by worker_id, and its row is kept as long as events can name it."
    )]
    Worker = 70,
}
