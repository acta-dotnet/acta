namespace Acta.Runtime;

/// <summary>Whether the attempt's outcome was recorded; false when the row moved since delivery selected it.</summary>
internal readonly record struct UpdateAlertDeliveryResponse(bool Updated);
