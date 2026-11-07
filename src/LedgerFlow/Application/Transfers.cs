using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LedgerFlow.Domain;
using LedgerFlow.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
namespace LedgerFlow.Application;

public class Transfers(IDbContextFactory<LedgerDb> factory, ILogger<Transfers>? logger = null)
{
    public static readonly Meter Meter = new("LedgerFlow");
    public static readonly Counter<long> Results = Meter.CreateCounter<long>("ledgerflow.transfer.outcomes");
    public static string? Validate(TransferRequest r) => r.Amount <= 0 || r.Amount > 1_000_000_000_000 ? "Amount must be 1..1000000000000 minor units." : r.From == r.To ? "Self transfers are forbidden." : null;
    public async Task<Outcome> Post(string caller, string key, TransferRequest request, CancellationToken ct)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
        for (var attempt = 0; ; attempt++)
        {
            try { return await Execute(caller, key, fingerprint, request, ct); }
            catch (Exception ex) when (IsDeadlock(ex) && attempt < 3)
            { await Task.Delay(TimeSpan.FromMilliseconds(50 * (1 << attempt) + Random.Shared.Next(50)), ct); }
        }
    }
    private static bool IsDeadlock(Exception ex) => ex is SqlException { Number: 1205 or 51003 } || ex is DbUpdateException { InnerException: SqlException { Number: 1205 } };
    private async Task<Outcome> Execute(string caller, string key, string fingerprint, TransferRequest r, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Transaction-owned application locks work across API instances and disappear on rollback.
        await Lock(db, "key:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(caller + "\n" + key))), ct);
        var prior = await db.Idempotencies.FindAsync([caller, key], ct);
        if (prior != null) return prior.Fingerprint == fingerprint ? new(prior.Status, prior.Response) : Error(409, "Idempotency key reused with a different request.");
        Guid? postedId = null;
        Outcome result;
        var invalid = Validate(r);
        if (invalid != null) result = Error(400, invalid);
        else
        {
            foreach (var id in new[] { r.From, r.To }.Order()) await Lock(db, "account:" + id, ct);
            var from = await db.Accounts.FindAsync([r.From], ct);
            var to = await db.Accounts.FindAsync([r.To], ct);
            if (from == null || from.Owner != caller || to == null || to.Owner == "__funding__") result = Error(404, "Account unavailable.");
            else if (from.Closed || to.Closed) result = Error(409, "Closed accounts cannot transfer.");
            else if (from.Currency != to.Currency) result = Error(400, "Currency conversion is unsupported.");
            else if (from.Balance < r.Amount) result = Error(409, "Insufficient funds.");
            else if (to.Balance > long.MaxValue - r.Amount) result = Error(409, "Destination balance limit exceeded.");
            else
            {
                var operation = new Operation { From = from.Id, To = to.Id, Amount = r.Amount, Currency = from.Currency };
                from.Balance -= r.Amount; to.Balance += r.Amount;
                postedId = operation.Id;
                db.Operations.Add(operation);
                db.Entries.AddRange(new Entry { OperationId = operation.Id, AccountId = from.Id, Amount = -r.Amount }, new Entry { OperationId = operation.Id, AccountId = to.Id, Amount = r.Amount });
                var evt = new TransferPosted(Guid.NewGuid(), 1, operation.Id, from.Id, to.Id, r.Amount, from.Currency, operation.CreatedAt);
                db.Outbox.Add(new Outbox { Id = evt.EventId, Payload = JsonSerializer.Serialize(evt) });
                result = new(201, JsonSerializer.Serialize(operation, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
        }
        db.Idempotencies.Add(new Idempotency { Caller = caller, Key = key, Fingerprint = fingerprint, Status = result.Status, Response = result.Json });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (postedId != null) logger?.LogInformation("Committed transfer {TransferId}", postedId);
        Results.Add(1, new KeyValuePair<string, object?>("status", result.Status));
        return result;
    }
    public static Outcome Error(int status, string detail) => new(status, JsonSerializer.Serialize(new { type = "about:blank", title = detail, status }));
    public static async Task Lock(LedgerDb db, string resource, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r = sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=5000; IF @r = -3 THROW 51003, 'Deadlock victim; retry request.', 1; IF @r < 0 THROW 51000, 'Lock unavailable; retry request with same key.', 1;", ct);
    }
}
