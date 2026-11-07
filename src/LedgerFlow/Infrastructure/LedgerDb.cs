using LedgerFlow.Domain;
using Microsoft.EntityFrameworkCore;
namespace LedgerFlow.Infrastructure;

public class LedgerDb(DbContextOptions<LedgerDb> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Operation> Operations => Set<Operation>();
    public DbSet<Entry> Entries => Set<Entry>();
    public DbSet<Idempotency> Idempotencies => Set<Idempotency>();
    public DbSet<Outbox> Outbox => Set<Outbox>();
    public DbSet<Notification> Notifications => Set<Notification>();
    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Account>().Property(x => x.Owner).HasMaxLength(200).UseCollation("Latin1_General_100_BIN2");
        m.Entity<Account>().Property(x => x.Currency).HasMaxLength(3);
        m.Entity<Account>().ToTable(t => t.HasCheckConstraint("CK_Balance", "[Balance] >= 0 OR [Owner] = '__funding__'"));
        m.Entity<Idempotency>().HasKey(x => new { x.Caller, x.Key });
        m.Entity<Idempotency>().Property(x => x.Caller).HasMaxLength(200).UseCollation("Latin1_General_100_BIN2");
        m.Entity<Idempotency>().Property(x => x.Key).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2");
        m.Entity<Entry>().HasOne<Operation>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<Entry>().HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<Entry>().HasIndex(x => new { x.AccountId, x.OperationId }).IsUnique();
        m.Entity<Operation>().ToTable(t => t.UseSqlOutputClause(false));
        m.Entity<Operation>().HasIndex(x => new { x.CreatedAt, x.Id });
        m.Entity<Outbox>().HasIndex(x => new { x.PublishedAt, x.NextAttempt });
    }
}
