namespace LedgerFlow.Domain;

public class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Owner { get; set; } = "";
    public string Currency { get; set; } = "USD";
    public long Balance { get; set; }
    public bool Closed { get; set; }
}
public class Operation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "Transfer";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid From { get; set; }
    public Guid To { get; set; }
    public long Amount { get; set; }
    public string Currency { get; set; } = "";
}
public class Entry
{
    public long Id { get; set; }
    public Guid OperationId { get; set; }
    public Guid AccountId { get; set; }
    public long Amount { get; set; }
}
public class Idempotency
{
    public string Caller { get; set; } = "";
    public string Key { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int Status { get; set; }
    public string Response { get; set; } = "";
}
public class Outbox
{
    public Guid Id { get; set; }
    public string Payload { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextAttempt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
public class Notification
{
    public Guid Id { get; set; }
    public Guid TransferId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}
public record TransferRequest(Guid From, Guid To, long Amount);
public record TransferPosted(Guid EventId, int Version, Guid TransferId, Guid From, Guid To, long Amount, string Currency, DateTimeOffset CreatedAt);
public record Outcome(int Status, string Json);
