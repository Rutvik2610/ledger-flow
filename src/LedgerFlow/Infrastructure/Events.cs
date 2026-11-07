using System.Text.Json;
using Azure.Messaging.ServiceBus;
using LedgerFlow.Application;
using LedgerFlow.Domain;
using Microsoft.EntityFrameworkCore;
namespace LedgerFlow.Infrastructure;

public interface IEventTransport { Task Publish(TransferPosted message, CancellationToken ct); }
public class NotificationConsumer(IDbContextFactory<LedgerDb> factory)
{
    public async Task Consume(TransferPosted evt, CancellationToken ct)
    {
        if (evt.Version != 1) throw new InvalidOperationException("Unsupported event version.");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Transfers.Lock(db, "inbox:" + evt.EventId, ct);
        if (!await db.Notifications.AnyAsync(x => x.Id == evt.EventId, ct))
        { db.Notifications.Add(new Notification { Id = evt.EventId, TransferId = evt.TransferId }); await db.SaveChangesAsync(ct); }
        await tx.CommitAsync(ct);
    }
}
public class LocalTransport(NotificationConsumer consumer) : IEventTransport
{ public Task Publish(TransferPosted message, CancellationToken ct) => consumer.Consume(message, ct); }
public sealed class AzureTransport : IEventTransport, IAsyncDisposable
{
    private readonly ServiceBusClient client;
    private readonly ServiceBusSender sender;
    public AzureTransport(IConfiguration config)
    {
        client = new ServiceBusClient(config["ServiceBus:ConnectionString"] ?? throw new InvalidOperationException("ServiceBus connection required."));
        sender = client.CreateSender(config["ServiceBus:Queue"] ?? "transfers");
    }
    public Task Publish(TransferPosted message, CancellationToken ct) => sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(message)) { MessageId = message.EventId.ToString(), Subject = "ledgerflow.transfer-posted.v1" }, ct);
    public async ValueTask DisposeAsync() { await sender.DisposeAsync(); await client.DisposeAsync(); }
}
public class AzureConsumer(IConfiguration config, NotificationConsumer consumer, ILogger<AzureConsumer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (config["Transport"] != "Azure") return;
        await using var client = new ServiceBusClient(config["ServiceBus:ConnectionString"]);
        await using var processor = client.CreateProcessor(config["ServiceBus:Queue"] ?? "transfers", new ServiceBusProcessorOptions { AutoCompleteMessages = false, MaxConcurrentCalls = 4 });
        processor.ProcessMessageAsync += async args =>
        {
            var evt = args.Message.Body.ToObjectFromJson<TransferPosted>() ?? throw new InvalidOperationException("Empty event");
            await consumer.Consume(evt, args.CancellationToken);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        };
        processor.ProcessErrorAsync += args => { log.LogError("Service Bus processing failed: {Reason}", args.Exception.GetType().Name); return Task.CompletedTask; };
        await processor.StartProcessingAsync(stoppingToken);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        await processor.StopProcessingAsync(CancellationToken.None);
    }
}
public class OutboxWorker(IServiceScopeFactory scopes, ILogger<OutboxWorker> log, IConfiguration? config = null) : BackgroundService
{
    private int MaxAttempts => Math.Clamp(config?.GetValue<int?>("Outbox:MaxAttempts") ?? 10, 1, 100);
    public static readonly System.Diagnostics.Metrics.Counter<long> Failures = Transfers.Meter.CreateCounter<long>("ledgerflow.outbox.failures");
    public static readonly System.Diagnostics.Metrics.Counter<long> Published = Transfers.Meter.CreateCounter<long>("ledgerflow.outbox.published");
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Dispatch(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError("Outbox iteration failed: {Reason}", ex.GetType().Name); }
            try { await Task.Delay(Math.Clamp(config?.GetValue<int?>("Outbox:PollMilliseconds") ?? 1000, 100, 60000), ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
    public async Task Dispatch(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDb>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Transfers.Lock(db, "outbox-dispatch", ct);
        var rows = await db.Outbox.Where(x => x.PublishedAt == null && x.Attempts < MaxAttempts && x.NextAttempt <= DateTimeOffset.UtcNow).OrderBy(x => x.CreatedAt).Take(20).ToListAsync(ct);
        foreach (var row in rows)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config?.GetValue<int?>("Outbox:PublishTimeoutSeconds") ?? 10, 1, 60)));
                await scope.ServiceProvider.GetRequiredService<IEventTransport>().Publish(JsonSerializer.Deserialize<TransferPosted>(row.Payload)!, timeout.Token);
                row.PublishedAt = DateTimeOffset.UtcNow; row.LastError = null; Published.Add(1);
                log.LogInformation("Published event {EventId}", row.Id);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { Failures.Add(1); row.Attempts++; row.LastError = ex.GetType().Name; row.NextAttempt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, row.Attempts))); }
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}
