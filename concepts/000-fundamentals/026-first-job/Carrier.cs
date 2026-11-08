// File: Carrier.cs
namespace Shipping;

public class Carrier
{
    // How many bookings in a row time out before the carrier answers again.
    public int TimeoutsLeft { get; set; }

    // Virtual so a test can stand in for it; the real carrier client goes here.
    public virtual Task<string> BookAsync(int orderId, CancellationToken ct)
    {
        if (TimeoutsLeft > 0)
        {
            TimeoutsLeft--;
            throw new TimeoutException($"Carrier did not answer; order {orderId} not booked.");
        }
        return Task.FromResult($"AP{orderId:D8}");
    }
}
