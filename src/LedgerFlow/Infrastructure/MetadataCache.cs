using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
namespace LedgerFlow.Infrastructure;

public record Metadata(Guid Id, string Owner, string Currency, bool Closed);
public class MetadataCache(LedgerDb db, IConnectionMultiplexer redis, ILogger<MetadataCache> log)
{
    public async Task<Metadata?> Get(Guid id, string owner, CancellationToken ct)
    {
        var key = $"ledgerflow:metadata:v1:{owner}:{id}";
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(key).WaitAsync(ct);
            if (value.HasValue) return JsonSerializer.Deserialize<Metadata>((string)value!);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException) { log.LogWarning("Metadata cache read unavailable: {Reason}", ex.GetType().Name); }
        var account = await db.Accounts.AsNoTracking().Where(x => x.Id == id && x.Owner == owner).Select(x => new Metadata(x.Id, x.Owner, x.Currency, x.Closed)).SingleOrDefaultAsync(ct);
        if (account != null) try { await redis.GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(account), TimeSpan.FromSeconds(60)).WaitAsync(ct); }
            catch (Exception ex) when (ex is RedisException or TimeoutException) { log.LogWarning("Metadata cache write unavailable: {Reason}", ex.GetType().Name); }
        return account;
    }
}
