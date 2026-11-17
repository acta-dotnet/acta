// How to save a record and start its job in one transaction
// `EnqueueAsync` takes your open transaction, so the order row and the job that fulfils it commit together, or
// neither exists.
using Acta;
using Acta.Learn.EnqueueInTransaction;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var provider = LocalDatabase.ResolveProvider(builder.Configuration);
if (!LocalDatabase.IsSqlite(provider))
{
    Console.WriteLine("This how-to keeps its orders table in the same SQLite file as Acta's tables.");
    Console.WriteLine("Unset ACTA_LOCAL_PROVIDER (or set it to sqlite) and run again.");
    return;
}

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<EnqueueInTransactionJobs>("enqueue-in-transaction");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// The application's own table lives in the database Acta uses, which is what lets one transaction cover both.
await using var connection = new SqliteConnection(LocalDatabase.ResolveConnectionString(builder.Configuration, provider));
await connection.OpenAsync();
await ExecuteAsync(null, "CREATE TABLE IF NOT EXISTS shop_orders (order_id TEXT PRIMARY KEY)");

// A run of its own, so a second run against the same file adds its own two orders.
var run = Guid.NewGuid().ToString("N")[..8];
var (kept, dropped) = ($"order-{run}-kept", $"order-{run}-dropped");

// learn: 1. Insert the record and enqueue its job on one transaction
// `connection` is a `SqliteConnection` the program opens on Acta's own connection string, so the orders table and
// Acta's tables are in one database.
// The transaction is yours: Acta writes the job through it and never commits it, rolls it back, or retries it. No
// worker sees the job before the commit.
EnqueueJobResponse committed;
await using (var transaction = connection.BeginTransaction())
{
    await ExecuteAsync(transaction, "INSERT INTO shop_orders (order_id) VALUES (@id)", kept);
    committed = await jobs.EnqueueAsync(transaction, new FulfilOrder(kept));
    await transaction.CommitAsync();
}

// learn: end

// learn: 2. Roll back, and neither the record nor the job exists
// The answer of an enqueue is provisional until the commit. After a rollback its job ref names nothing.
EnqueueJobResponse rolledBack;
await using (var transaction = connection.BeginTransaction())
{
    await ExecuteAsync(transaction, "INSERT INTO shop_orders (order_id) VALUES (@id)", dropped);
    rolledBack = await jobs.EnqueueAsync(transaction, new FulfilOrder(dropped));
    await transaction.RollbackAsync();
}

// learn: end

await jobs.WaitAsync(committed.JobRef);
Console.WriteLine($"committed:   order row {await OrderExistsAsync(kept)}, job {await JobAsync(committed.JobRef)}");
Console.WriteLine($"rolled back: order row {await OrderExistsAsync(dropped)}, job {await JobAsync(rolledBack.JobRef)}");

await host.StopAsync();

async Task<string> JobAsync(JobRef jobRef) => (await jobs.GetAsync(jobRef))?.Status.Code ?? "none";

async Task<string> OrderExistsAsync(string orderId)
{
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM shop_orders WHERE order_id = @id";
    command.Parameters.AddWithValue("@id", orderId);
    return (long)(await command.ExecuteScalarAsync())! == 1 ? "yes" : "no";
}

async Task ExecuteAsync(SqliteTransaction? transaction, string sql, string? orderId = null)
{
    await using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = sql;
    if (orderId is not null)
    {
        command.Parameters.AddWithValue("@id", orderId);
    }
    await command.ExecuteNonQueryAsync();
}

namespace Acta.Learn.EnqueueInTransaction
{
    public sealed record FulfilOrder(string OrderId);

    public static class FulfilOrderJob
    {
        [Job("fulfil-order")]
        public static void Handle(FulfilOrder order) => Console.WriteLine($"[{order.OrderId[^4..]}] order fulfilled");
    }
}
