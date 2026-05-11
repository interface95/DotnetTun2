using System.Net;
using DotnetTun.Abstractions.Dns;
using DotnetTun.Core.Dns;
using Xunit;

namespace DotnetTun.Core.Tests.Dns;

public sealed class FakeIpStoreTests
{
    [Fact]
    public void Allocate_AssignsAddressFromRange()
    {
        IFakeIpStore store = new FakeIpStore(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.3"));

        var address = store.Allocate("api.anthropic.com");

        Assert.Equal(IPAddress.Parse("198.18.0.1"), address);
    }

    [Fact]
    public void Allocate_SameDomainTwice_ReturnsSameAddress()
    {
        IFakeIpStore store = new FakeIpStore(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.10"));

        var first = store.Allocate("api.anthropic.com");
        var second = store.Allocate("API.anthropic.com.");

        Assert.Equal(first, second);
    }

    [Fact]
    public void TryResolve_RoundTripsAllocatedDomain()
    {
        IFakeIpStore store = new FakeIpStore(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.10"));
        var address = store.Allocate("api.anthropic.com");

        var resolved = store.TryResolve(address, out var domain);

        Assert.True(resolved);
        Assert.Equal("api.anthropic.com", domain);
    }

    [Fact]
    public void TryResolve_UnknownAddress_ReturnsFalse()
    {
        IFakeIpStore store = new FakeIpStore(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.10"));

        var resolved = store.TryResolve(IPAddress.Parse("198.18.0.5"), out var domain);

        Assert.False(resolved);
        Assert.Null(domain);
    }

    [Fact]
    public void Allocate_WhenRangeExhausted_Throws()
    {
        IFakeIpStore store = new FakeIpStore(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.1"));
        store.Allocate("a.example.com");

        Assert.Throws<InvalidOperationException>((Action)(() => store.Allocate("b.example.com")));
    }
}
