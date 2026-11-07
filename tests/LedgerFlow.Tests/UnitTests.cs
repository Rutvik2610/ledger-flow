using LedgerFlow.Application;
using LedgerFlow.Domain;
using Xunit;
namespace LedgerFlow.Tests;

public class UnitTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1000000000001)]
    public void Invalid_amounts(long amount) => Assert.NotNull(Transfers.Validate(new(Guid.NewGuid(), Guid.NewGuid(), amount)));
    [Fact] public void Self_transfer_rejected() { var id = Guid.NewGuid(); Assert.NotNull(Transfers.Validate(new(id, id, 1))); }
    [Fact] public void Valid_transfer_shape() => Assert.Null(Transfers.Validate(new(Guid.NewGuid(), Guid.NewGuid(), 1)));
}
