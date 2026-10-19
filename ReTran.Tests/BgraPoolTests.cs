using ReTran.Core.Capture;
using Xunit;

namespace RetranCore.Tests;

public class BgraPoolTests
{
    [Fact]
    public void Rent_AfterReturn_GivesSameArrayBack()
    {
        var pool = new BgraPool();
        byte[] first = pool.Rent(1024);
        pool.Return(first);
        Assert.Same(first, pool.Rent(1024));
    }

    [Fact]
    public void Rent_WrongSize_AllocatesNewArrayOfExactLength()
    {
        var pool = new BgraPool();
        byte[] big = pool.Rent(2048);
        pool.Return(big);
        byte[] rented = pool.Rent(1024);
        Assert.NotSame(big, rented);
        Assert.Equal(1024, rented.Length);
    }

    [Fact]
    public void Rent_ConcurrentRents_GiveDistinctArrays()
    {
        var pool = new BgraPool();
        byte[] taken = pool.Rent(16);
        Assert.NotSame(taken, pool.Rent(16));
        pool.Return(taken);
    }

    [Fact]
    public void Return_NullAndEmpty_DoNotThrow()
    {
        var pool = new BgraPool();
        pool.Return(null);
        pool.Return([]);
        Assert.Equal(16, pool.Rent(16).Length);
    }
}
