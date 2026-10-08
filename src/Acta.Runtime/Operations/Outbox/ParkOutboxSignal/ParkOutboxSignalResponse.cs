namespace Acta.Runtime;

/// <summary>
/// Park admission outcome: <c>Action</c> uses the <c>ControlAction</c> ids (1 = the command is now the
/// pending one, 3 = rejected while another is pending), and <c>PendingSinceUtc</c> is the pending
/// command's park instant - the age the rejection reports to the operator.
/// </summary>
[DbProjection]
internal sealed record ParkOutboxSignalResponse(byte Action, DateTime? PendingSinceUtc);
