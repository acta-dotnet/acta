# Acme Shop — API and two workers

A production-shape demo: an enqueue-only ASP.NET Core API and two worker processes that own separate
job namespaces. They coordinate only through the durable database; no process calls another.

```text
HTTP client -> API ---> Acta database <--- Payments worker ---(durable handoff)---> Shipping worker
                |                                                                        |
                +--------------------- shared Contracts projects -----------------------+
```

## Run it

Open `AcmeShop.slnx`, pick the **API + workers** launch profile in the toolbar dropdown, and press F5.
That starts all three processes together (both workers first, then the API) and opens the shop console
at <http://localhost:5000> in your browser. The dashboard is one click away at `/acta`.

From the command line, run each in its own terminal:

```bash
dotnet run --project demos/AcmeShop/Acme.Shop.Payments
dotnet run --project demos/AcmeShop/Acme.Shop.Shipping
dotnet run --project demos/AcmeShop/Acme.Shop.Api
```

Start a worker before enqueuing. The worker owns its job definitions, so an enqueue against a namespace
no worker has registered yet is rejected with `ENQ_ROUTE_UNKNOWN`.

No setup is required: the demo defaults to embedded SQLite in your temp folder. Set
`ACTA_LOCAL_PROVIDER=postgres` (or `sqlserver`) plus a connection string to run it on a server instead;
see `LocalDatabase.cs`, which is ordinary consumer code you can copy.

## Try it

```bash
curl -X POST http://localhost:5000/orders -H "Content-Type: application/json" -H "X-User-Id: u-1" \
  -d '{"orderId":"ORD-1042","amount":49.90,"lines":[{"sku":"SKU-1","quantity":2}]}'
```

The response carries a `jobRef` and a status URL. Watch the worker consoles: payments reserves stock and
charges the card, then hands off durably to shipping, which labels and dispatches.

| Endpoint | What it shows |
| --- | --- |
| `POST /orders` | Enqueue: the API returns as soon as the job row is committed |
| `GET /orders/{jobRef}` | Poll a job's durable status by its public ref |
| `GET /orders/{orderId}/shipping` | Read the result the shipping worker wrote |
| `POST /admin/orders/{jobRef}/fraud-decision` | Release a job that is suspended waiting on a signal |
| `/acta` | The operator dashboard, read plus controls |

## How it references Acta

The demo consumes the published Acta packages, exactly as your own app would: package references only,
versions in `demos/Directory.Packages.props`. It is not part of `Acta.slnx` and does not build against
`src/`, so what you read here is what you would write.
