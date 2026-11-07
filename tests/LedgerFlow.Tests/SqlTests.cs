using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LedgerFlow.Application;
using LedgerFlow.Domain;
using LedgerFlow.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace LedgerFlow.Tests;

// Explicit opt-in: absence is a skip, never a substitute database.
public sealed class SqlFactAttribute : FactAttribute
{ public SqlFactAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LEDGERFLOW_TEST_SQL"))) Skip = "Set LEDGERFLOW_TEST_SQL to run real SQL Server integration tests."; } }
public class SqlTests
{
    private static string Connection => Environment.GetEnvironmentVariable("LEDGERFLOW_TEST_SQL")!;
    private sealed class Factory : IDbContextFactory<LedgerDb>
    { public LedgerDb CreateDbContext() => new(new DbContextOptionsBuilder<LedgerDb>().UseSqlServer(Connection).Options); }
    private static async Task<(Factory factory, Account a, Account b)> Setup()
    {
        var factory = new Factory(); await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
        var a = new Account { Owner = "test-" + Guid.NewGuid().ToString(), Balance = 100, Currency = "USD" }; var b = new Account { Owner = "recipient", Currency = "USD" };
        // Test fixture funding is represented with balanced entries, just like the development endpoint.
        var system = new Account { Owner = "__funding__", Balance = -100, Currency = "USD" };
        var operation = new Operation { Kind = "DevelopmentFunding", From = system.Id, To = a.Id, Amount = 100, Currency = "USD" };
        db.Accounts.AddRange(a, b, system); db.Operations.Add(operation); db.Entries.AddRange(new Entry { AccountId = system.Id, OperationId = operation.Id, Amount = -100 }, new Entry { AccountId = a.Id, OperationId = operation.Id, Amount = 100 }); await db.SaveChangesAsync(); return (factory, a, b);
    }
    [SqlFact]
    public async Task Transfer_replays_and_rejects_payload_conflict()
    {
        var (f, a, b) = await Setup(); var service = new Transfers(f); var r = new TransferRequest(a.Id, b.Id, 30);
        var first = await service.Post(a.Owner, "same", r, default); Assert.Equal(201, first.Status); Assert.Equal(first, await service.Post(a.Owner, "same", r, default));
        Assert.Equal(409, (await service.Post(a.Owner, "same", r with { Amount = 31 }, default)).Status);
        await using var db = f.CreateDbContext(); Assert.Equal(70, (await db.Accounts.FindAsync(a.Id))!.Balance);
        var op = JsonSerializer.Deserialize<Operation>(first.Json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(0, await db.Entries.Where(x => x.OperationId == op.Id).SumAsync(x => x.Amount)); Assert.Equal(2, await db.Entries.CountAsync(x => x.OperationId == op.Id));
        Assert.True(await db.Outbox.AnyAsync(x => x.Payload.Contains(op.Id.ToString())));
    }
    [SqlFact]
    public async Task Keys_and_subjects_are_case_sensitive_and_caller_scoped()
    {
        var (f, a, b) = await Setup(); var service = new Transfers(f);
        Assert.Equal(201, (await service.Post(a.Owner, "Key", new(a.Id, b.Id, 10), default)).Status);
        Assert.Equal(201, (await service.Post(a.Owner, "key", new(a.Id, b.Id, 10), default)).Status);
        Assert.Equal(404, (await service.Post(a.Owner.ToUpperInvariant(), "Key", new(a.Id, b.Id, 10), default)).Status);
        await using var db = f.CreateDbContext(); Assert.Equal(80, (await db.Accounts.FindAsync(a.Id))!.Balance);
    }
    [SqlFact]
    public async Task Concurrent_same_key_posts_once()
    {
        var (f, a, b) = await Setup(); var tasks = Enumerable.Range(0, 12).Select(_ => new Transfers(f).Post(a.Owner, "concurrent", new(a.Id, b.Id, 20), default));
        var results = await Task.WhenAll(tasks); Assert.All(results, x => Assert.Equal(results[0], x)); Assert.Equal(201, results[0].Status);
        await using var db = f.CreateDbContext(); Assert.Equal(80, (await db.Accounts.FindAsync(a.Id))!.Balance); Assert.Equal(1, await db.Operations.CountAsync(x => x.From == a.Id));
    }
    [SqlFact]
    public async Task Concurrent_distinct_keys_cannot_overspend()
    {
        var (f, a, b) = await Setup(); var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => new Transfers(f).Post(a.Owner, i.ToString(), new(a.Id, b.Id, 30), default)));
        Assert.Equal(3, results.Count(x => x.Status == 201)); Assert.Equal(9, results.Count(x => x.Status == 409));
        await using var db = f.CreateDbContext(); Assert.Equal(10, (await db.Accounts.FindAsync(a.Id))!.Balance); Assert.Equal(90, (await db.Accounts.FindAsync(b.Id))!.Balance);
        Assert.Equal(0, await db.Entries.Where(x => x.AccountId == a.Id || x.AccountId == b.Id).SumAsync(x => x.Amount) - 100);
    }
    [SqlFact]
    public async Task Validation_ownership_currency_and_insufficient_funds_leave_no_partial_transfer()
    {
        var (f, a, b) = await Setup(); var service = new Transfers(f);
        Assert.Equal(404, (await service.Post("intruder", "one", new(a.Id, b.Id, 1), default)).Status);
        Assert.Equal(409, (await service.Post(a.Owner, "two", new(a.Id, b.Id, 101), default)).Status);
        Assert.Equal(400, (await service.Post(a.Owner, "three", new(a.Id, a.Id, 1), default)).Status);
        Assert.Equal(400, (await service.Post(a.Owner, "four", new(a.Id, b.Id, 0), default)).Status);
        await using var db = f.CreateDbContext(); var target = await db.Accounts.FindAsync(b.Id); target!.Currency = "INR"; await db.SaveChangesAsync();
        Assert.Equal(400, (await service.Post(a.Owner, "five", new(a.Id, b.Id, 1), default)).Status);
        Assert.False(await db.Operations.AnyAsync(x => x.From == a.Id)); Assert.Equal(100, (await db.Accounts.FindAsync(a.Id))!.Balance);
        target.Closed = true; target.Currency = "USD"; await db.SaveChangesAsync(); Assert.Equal(409, (await service.Post(a.Owner, "six", new(a.Id, b.Id, 1), default)).Status);
    }
    [SqlFact]
    public async Task Database_failure_rolls_back_balances_ledger_and_idempotency()
    {
        var (f, a, b) = await Setup();
        await using var db = f.CreateDbContext();
        // Deliberate trigger failure after modifications have begun, not a validation failure.
        await db.Database.ExecuteSqlRawAsync("CREATE OR ALTER TRIGGER TestRejectTransfer ON Operations AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE Amount = 77) THROW 51001, 'Injected failure', 1; END");
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => new Transfers(f).Post(a.Owner, "rollback", new(a.Id, b.Id, 77), default));
            await using var check = f.CreateDbContext();
            Assert.Equal(100, (await check.Accounts.FindAsync(a.Id))!.Balance);
            Assert.Equal(0, (await check.Accounts.FindAsync(b.Id))!.Balance);
            Assert.False(await check.Operations.AnyAsync(x => x.From == a.Id));
            Assert.False(await check.Idempotencies.AnyAsync(x => x.Caller == a.Owner));
            Assert.False(await check.Entries.AnyAsync(x => x.AccountId == b.Id));
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER TestRejectTransfer"); }
    }
    [SqlFact]
    public async Task Duplicate_events_create_one_notification()
    {
        var (f, a, b) = await Setup(); var consumer = new NotificationConsumer(f); var evt = new TransferPosted(Guid.NewGuid(), 1, Guid.NewGuid(), a.Id, b.Id, 1, "USD", DateTimeOffset.UtcNow);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => consumer.Consume(evt, default)));
        await using var db = f.CreateDbContext(); Assert.Equal(1, await db.Notifications.CountAsync(x => x.Id == evt.EventId));
    }

    private sealed class SwitchTransport(NotificationConsumer consumer) : IEventTransport
    {
        public bool Fail = true;
        public Task Publish(TransferPosted evt, CancellationToken ct) => Fail ? Task.FromException(new TimeoutException("Injected broker failure")) : consumer.Consume(evt, ct);
    }
    [SqlFact]
    public async Task Outbox_retries_failure_then_publishes_and_records_notification()
    {
        var (f, a, b) = await Setup(); var result = await new Transfers(f).Post(a.Owner, "outbox", new(a.Id, b.Id, 5), default); Assert.Equal(201, result.Status);
        var operation = JsonSerializer.Deserialize<Operation>(result.Json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var consumer = new NotificationConsumer(f); var transport = new SwitchTransport(consumer);
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IDbContextFactory<LedgerDb>>(f); services.AddScoped(_ => f.CreateDbContext()); services.AddSingleton<IEventTransport>(transport);
        await using var provider = services.BuildServiceProvider();
        var worker = new OutboxWorker(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OutboxWorker>>());
        await worker.Dispatch(default);
        await using (var db = f.CreateDbContext())
        {
            var row = await db.Outbox.SingleAsync(x => x.Payload.Contains(operation.Id.ToString())); Assert.Null(row.PublishedAt); Assert.Equal(1, row.Attempts); Assert.Equal("TimeoutException", row.LastError); row.NextAttempt = DateTimeOffset.UtcNow.AddSeconds(-1); await db.SaveChangesAsync();
        }
        transport.Fail = false; await worker.Dispatch(default);
        await using (var db = f.CreateDbContext())
        {
            var row = await db.Outbox.SingleAsync(x => x.Payload.Contains(operation.Id.ToString())); Assert.NotNull(row.PublishedAt); Assert.Null(row.LastError); Assert.True(await db.Notifications.AnyAsync(x => x.Id == row.Id));
        }
    }
    private sealed class AppFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development").ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { { "ConnectionStrings:Sql", Connection }, { "Auth:Development", "true" }, { "Redis", "localhost:6399" }, { "RateLimit:PerMinute", "1000" } }));
    }
    [SqlFact]
    public async Task Http_auth_cache_fallback_and_history_pagination()
    {
        var (f, a, b) = await Setup(); await using var app = new AppFactory(); var http = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/accounts")).StatusCode);
        var token = await (await http.PostAsync("/dev/token/alice", null)).Content.ReadFromJsonAsync<JsonElement>(); http.DefaultRequestHeaders.Authorization = new("Bearer", token.GetProperty("access_token").GetString());
        var account = await (await http.PostAsJsonAsync("/api/accounts", new { Currency = "USD" })).Content.ReadFromJsonAsync<Account>();
        Assert.NotNull(account); Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/api/accounts/{account.Id}")).StatusCode); // Redis deliberately unavailable.
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/accounts/{a.Id}/balance")).StatusCode);
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync($"/api/dev/accounts/{account.Id}/fund", new { Amount = 10 })).StatusCode);
        var first = await http.GetFromJsonAsync<Operation[]>($"/api/accounts/{account.Id}/history?page=1&size=2"); var second = await http.GetFromJsonAsync<Operation[]>($"/api/accounts/{account.Id}/history?page=2&size=2");
        Assert.Equal(2, first!.Length); Assert.Single(second!); Assert.Empty(first.Select(x => x.Id).Intersect(second!.Select(x => x.Id)));
        var repeat = await http.GetFromJsonAsync<Operation[]>($"/api/accounts/{account.Id}/history?page=1&size=2"); Assert.Equal(first.Select(x => x.Id), repeat!.Select(x => x.Id));
    }
}
