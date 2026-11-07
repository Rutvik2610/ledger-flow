using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace LedgerFlow.Infrastructure;

public class DesignTimeFactory : IDesignTimeDbContextFactory<LedgerDb>
{
    public LedgerDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<LedgerDb>().UseSqlServer(
        Environment.GetEnvironmentVariable("ConnectionStrings__Sql") ?? "Server=localhost;Database=LedgerFlow;Integrated Security=True;Encrypt=True").Options);
}
