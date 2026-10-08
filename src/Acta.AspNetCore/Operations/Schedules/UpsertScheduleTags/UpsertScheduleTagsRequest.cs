namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /schedules/{jobNamespace}/{jobName}/{scheduleName}/tags</c>: the tag's name and its optional value.</summary>
internal sealed record UpsertScheduleTagsRequest(string? Name, string? Value) : IUpsertTagsRequest;
