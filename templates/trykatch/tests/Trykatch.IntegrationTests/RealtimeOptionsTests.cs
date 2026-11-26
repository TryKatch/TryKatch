using Microsoft.Extensions.Options;
using Shouldly;
using Trykatch.Api.Realtime;

namespace Trykatch.IntegrationTests;

[TestClass]
public sealed class RealtimeOptionsTests
{
    [TestMethod]
    public void DefaultsAndLocalOnlyModeDoNotRequireAnApplicationNamespace()
    {
        RealtimeOptions defaults = new();
        defaults.Mode.ShouldBe(RealtimeMode.Disabled);
        Validate(defaults).Succeeded.ShouldBeTrue();

        Validate(new RealtimeOptions { Mode = RealtimeMode.LocalOnly }).Succeeded.ShouldBeTrue();
        Validate(new RealtimeOptions { Mode = RealtimeMode.Disabled, ApplicationNamespace = "trykatch-local" }).Succeeded.ShouldBeTrue();
    }

    [TestMethod]
    public void DistributedModeRequiresValidExplicitNamespaceAndNamesAreValidatedInEveryMode()
    {
        Validate(new RealtimeOptions { Mode = RealtimeMode.Distributed }).Failed.ShouldBeTrue();
        Validate(new RealtimeOptions { Mode = RealtimeMode.Distributed, ApplicationNamespace = "a" }).Succeeded.ShouldBeTrue();
        Validate(new RealtimeOptions { Mode = RealtimeMode.Distributed, ApplicationNamespace = new string('a', 120) }).Succeeded.ShouldBeTrue();

        foreach (string invalidNamespace in new[]
        {
            "Upper-case", "-leading", "trailing-", "double--separator", new string('a', 121)
        })
        {
            Validate(new RealtimeOptions { Mode = RealtimeMode.Distributed, ApplicationNamespace = invalidNamespace }).Failed.ShouldBeTrue();
            Validate(new RealtimeOptions { Mode = RealtimeMode.Disabled, ApplicationNamespace = invalidNamespace }).Failed.ShouldBeTrue();
            Validate(new RealtimeOptions { Mode = RealtimeMode.LocalOnly, ApplicationNamespace = invalidNamespace }).Failed.ShouldBeTrue();
        }
    }

    [TestMethod]
    public void RejectsUndefinedModeAndValuesOutsideFiniteHostBounds()
    {
        Validate(new RealtimeOptions { Mode = (RealtimeMode)999 }).Failed.ShouldBeTrue();

        RealtimeOptions[] outsideBounds =
        [
            new() { MaximumConnections = 0 }, new() { MaximumConnections = 257 },
            new() { MaximumSubscriptions = 0 }, new() { MaximumSubscriptions = 17 },
            new() { MaximumConcurrentSends = 0 }, new() { MaximumConcurrentSends = 17 },
            new() { PollInterval = TimeSpan.FromMilliseconds(99) }, new() { PollInterval = TimeSpan.FromMilliseconds(30_001) },
            new() { SendTimeout = TimeSpan.FromMilliseconds(99) }, new() { SendTimeout = TimeSpan.FromMilliseconds(10_001) },
            new() { AuthorityTimeout = TimeSpan.FromMilliseconds(99) }, new() { AuthorityTimeout = TimeSpan.FromMilliseconds(30_001) }
        ];

        foreach (RealtimeOptions options in outsideBounds)
            Validate(options).Failed.ShouldBeTrue();
    }

    [TestMethod]
    public void AcceptsInclusiveHostBounds()
    {
        Validate(new RealtimeOptions
        {
            MaximumConnections = 1,
            MaximumSubscriptions = 1,
            MaximumConcurrentSends = 1,
            PollInterval = TimeSpan.FromMilliseconds(100),
            SendTimeout = TimeSpan.FromMilliseconds(100),
            AuthorityTimeout = TimeSpan.FromMilliseconds(100)
        }).Succeeded.ShouldBeTrue();

        Validate(new RealtimeOptions
        {
            MaximumConnections = 256,
            MaximumSubscriptions = 16,
            MaximumConcurrentSends = 16,
            PollInterval = TimeSpan.FromSeconds(30),
            SendTimeout = TimeSpan.FromSeconds(10),
            AuthorityTimeout = TimeSpan.FromSeconds(30)
        }).Succeeded.ShouldBeTrue();
    }

    [TestMethod]
    public void FailureMessagesAreBoundedAndDoNotEchoUntrustedNamespaceInput()
    {
        const string sentinel = "SENTINEL-CREDENTIAL-LIKE-PATH-DO-NOT-ECHO";
        ValidateOptionsResult result = Validate(new RealtimeOptions
        {
            Mode = RealtimeMode.Distributed,
            ApplicationNamespace = sentinel
        });

        result.Failed.ShouldBeTrue();
        string[] failures = result.Failures.ToArray();
        failures.ShouldNotBeEmpty();
        failures.Length.ShouldBeLessThanOrEqualTo(8);
        foreach (string failure in failures)
        {
            failure.Length.ShouldBeLessThanOrEqualTo(160);
            failure.ShouldNotContain(sentinel);
        }
    }

    private static ValidateOptionsResult Validate(RealtimeOptions options) =>
        new RealtimeOptionsValidator().Validate(Options.DefaultName, options);
}
