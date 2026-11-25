using CloudSelfService.Services.Transfers;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CloudSelfService.Tests;

public class PieceMemoryBudgetTests
{
    private const long Mi = 1024 * 1024;

    private static PieceMemoryBudget Budget(long? total = null, long? perUser = null)
    {
        var settings = new Dictionary<string, string?>();
        if (total is not null) settings["Transfers:MaxInFlightPieceBytes"] = total.ToString();
        if (perUser is not null) settings["Transfers:MaxInFlightPieceBytesPerUser"] = perUser.ToString();
        return new PieceMemoryBudget(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    [Fact]
    public void Defaults_AreTheAgreedValues()
    {
        var budget = Budget();

        Assert.Equal(268_435_456, budget.MaxBytes);
        Assert.Equal(67_108_864, budget.MaxBytesPerUser);
    }

    [Fact]
    public void TryReserve_WithinBothBudgets_Succeeds()
    {
        var budget = Budget(total: 64 * Mi, perUser: 32 * Mi);
        var user = Guid.NewGuid();

        Assert.True(budget.TryReserve(user, 16 * Mi, out var first));
        Assert.True(budget.TryReserve(user, 16 * Mi, out var second));
        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public void TryReserve_OverUserBudget_FailsAndReservesNothing()
    {
        var budget = Budget(total: 64 * Mi, perUser: 32 * Mi);
        var user = Guid.NewGuid();
        Assert.True(budget.TryReserve(user, 32 * Mi, out _));

        Assert.False(budget.TryReserve(user, 4 * Mi, out var refused));

        Assert.Null(refused);
        // The refused 4 MiB was not taken from the app-wide total: another user still gets all that is left.
        Assert.True(budget.TryReserve(Guid.NewGuid(), 32 * Mi, out _));
    }

    [Fact]
    public void TryReserve_OverGlobalBudget_FailsForAnyUser()
    {
        var budget = Budget(total: 32 * Mi, perUser: 32 * Mi);
        Assert.True(budget.TryReserve(Guid.NewGuid(), 16 * Mi, out _));
        Assert.True(budget.TryReserve(Guid.NewGuid(), 16 * Mi, out _));

        Assert.False(budget.TryReserve(Guid.NewGuid(), 4 * Mi, out _));
        Assert.False(budget.TryReserve(Guid.NewGuid(), 1, out _));
    }

    [Fact]
    public void Lease_DisposedTwice_ReleasesOnce()
    {
        var budget = Budget(total: 32 * Mi, perUser: 32 * Mi);
        var user = Guid.NewGuid();
        Assert.True(budget.TryReserve(user, 16 * Mi, out var held));
        Assert.True(budget.TryReserve(user, 16 * Mi, out var released));

        released!.Dispose();
        released.Dispose();

        Assert.True(budget.TryReserve(user, 16 * Mi, out _));
        Assert.False(budget.TryReserve(user, 1, out _));
        held!.Dispose();
    }

    [Fact]
    public void TryReserve_PieceLargerThanBudget_AdmittedOnlyWhenEmpty()
    {
        var budget = Budget(perUser: 16 * Mi);
        var user = Guid.NewGuid();

        Assert.True(budget.TryReserve(user, 32 * Mi, out var big));
        Assert.False(budget.TryReserve(user, 4 * Mi, out _));

        big!.Dispose();
        Assert.True(budget.TryReserve(user, 4 * Mi, out _));
    }

    [Fact]
    public void TryReserve_PieceLargerThanGlobalBudget_AdmittedOnlyWhenEmpty()
    {
        var budget = Budget(total: 16 * Mi, perUser: 64 * Mi);

        Assert.True(budget.TryReserve(Guid.NewGuid(), 32 * Mi, out var big));
        Assert.False(budget.TryReserve(Guid.NewGuid(), 4 * Mi, out _));
        big!.Dispose();
        Assert.True(budget.TryReserve(Guid.NewGuid(), 4 * Mi, out _));
    }

    [Fact]
    public void TryReserve_ZeroBytes_AdmittedEvenOverBudget()
    {
        // An oversized piece admitted into an empty budget leaves it over its limit; an empty piece still goes.
        var budget = Budget(total: 16 * Mi, perUser: 16 * Mi);
        var user = Guid.NewGuid();
        Assert.True(budget.TryReserve(user, 32 * Mi, out _));

        Assert.True(budget.TryReserve(user, 0, out var empty));
        empty.Dispose();
        Assert.False(budget.TryReserve(user, 1, out _));
    }

    [Fact]
    public async Task TryReserve_Concurrent_NeverExceedsBudget()
    {
        var budget = Budget(total: 64 * Mi, perUser: 64 * Mi);
        var users = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        long inUse = 0, peak = 0;

        await Task.WhenAll(Enumerable.Range(0, 16).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
            {
                if (!budget.TryReserve(users[(t + i) % users.Length], 4 * Mi, out var lease)) continue;
                var now = Interlocked.Add(ref inUse, 4 * Mi);
                long seen;
                while ((seen = Interlocked.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
                Interlocked.Add(ref inUse, -4 * Mi);
                lease!.Dispose();
            }
        })));

        Assert.True(peak <= 64 * Mi);
        Assert.True(budget.TryReserve(Guid.NewGuid(), 64 * Mi, out _));
    }
}
