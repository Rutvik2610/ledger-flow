using Azure.Messaging.ServiceBus;
using LedgerFlow.Domain;
using LedgerFlow.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace LedgerFlow.Tests;

public sealed class AzureFactAttribute : FactAttribute
{
    public AzureFactAttribute() { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LEDGERFLOW_TEST_SERVICEBUS")) || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LEDGERFLOW_TEST_QUEUE"))) Skip = "Requires a dedicated Azure Service Bus test queue and secure credentials."; }
}
public class AzureLiveTests
{
    [AzureFact]
    public async Task Real_adapter_roundtrips_stable_event_contract()
    {
        var connection = Environment.GetEnvironmentVariable("LEDGERFLOW_TEST_SERVICEBUS")!; var queue = Environment.GetEnvironmentVariable("LEDGERFLOW_TEST_QUEUE")!;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { { "ServiceBus:ConnectionString", connection }, { "ServiceBus:Queue", queue } }).Build();
        await using var transport = new AzureTransport(config); await using var client = new ServiceBusClient(connection); await using var receiver = client.CreateReceiver(queue);
        var evt = new TransferPosted(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "USD", DateTimeOffset.UtcNow);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); await transport.Publish(evt, timeout.Token);
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30), timeout.Token); Assert.NotNull(message);
        Assert.Equal(evt.EventId.ToString(), message.MessageId); Assert.Equal("ledgerflow.transfer-posted.v1", message.Subject); Assert.Equal(evt, message.Body.ToObjectFromJson<TransferPosted>());
        await receiver.CompleteMessageAsync(message, timeout.Token);
    }
}
