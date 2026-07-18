namespace Acme.Shop.Api.Domain;

// Acme Shop's order store, written by the App (not Acta). In-memory here; a database in production.
public interface IOrderStore
{
    // Records a new order, returning false when one already exists for this user and id, so intake
    // stays idempotent without a separate read.
    bool Save(OrderRecord order);

    void Append(OrderEvent orderEvent);
}
