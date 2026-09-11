using System.Net;
using Shouldly;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class FederationNetworkGuardTests
{
    [TestMethod]
    [DataRow("10.0.0.1")]
    [DataRow("100.64.0.1")]
    [DataRow("127.0.0.1")]
    [DataRow("169.254.169.254")]
    [DataRow("172.16.0.1")]
    [DataRow("192.168.0.1")]
    [DataRow("fc00::1")]
    [DataRow("fd12:3456:789a::1")]
    [DataRow("::ffff:10.0.0.1")]
    public void PrivateMetadataAndMappedAddressesAreRejected(string value) =>
        FederationNetworkGuard.IsPrivateOrMetadataAddress(IPAddress.Parse(value)).ShouldBeTrue();

    [TestMethod]
    [DataRow("8.8.8.8")]
    [DataRow("2606:4700:4700::1111")]
    public void PublicAddressesAreAccepted(string value) =>
        FederationNetworkGuard.IsPrivateOrMetadataAddress(IPAddress.Parse(value)).ShouldBeFalse();

    [TestMethod]
    public void MixedDnsAnswersAreRejectedWhenAnyAddressIsPrivate() =>
        Should.Throw<InvalidOperationException>(() => FederationNetworkGuard.EnsureAddressesAreAllowed(
            [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.0.0.1")],
            allowPrivateAddresses: false));

    [TestMethod]
    public void DiscoveryHandlerNeverFollowsRedirects()
    {
        using SocketsHttpHandler handler = FederationNetworkGuard.CreateHandler(allowInsecureLoopbackIssuer: false);

        handler.AllowAutoRedirect.ShouldBeFalse();
        handler.UseProxy.ShouldBeFalse();
    }

    [TestMethod]
    [DataRow("localhost")]
    [DataRow("host.docker.internal")]
    [DataRow("127.0.0.1")]
    [DataRow("::1")]
    public void ExplicitLoopbackHostsUseTheSameDevelopmentExceptionAtEveryBoundary(string host) =>
        FederationNetworkGuard.IsExplicitLoopbackHost(host).ShouldBeTrue();

    [TestMethod]
    [DataRow("10.0.0.1")]
    [DataRow("identity.example.com")]
    public void NonLoopbackHostsNeverReceiveTheDevelopmentException(string host) =>
        FederationNetworkGuard.IsExplicitLoopbackHost(host).ShouldBeFalse();

    [TestMethod]
    public async Task DiscoveryBodyIsRejectedBeforeStreamingPastTheLimit()
    {
        byte[] oversized = new byte[256 * 1024 + 1];
        using StreamContent content = new(new MemoryStream(oversized, writable: false));

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            FederationProviderProbe.ReadBoundedDiscoveryAsync(content, CancellationToken.None));

        exception.Message.ShouldContain("256 KiB");
    }
}
