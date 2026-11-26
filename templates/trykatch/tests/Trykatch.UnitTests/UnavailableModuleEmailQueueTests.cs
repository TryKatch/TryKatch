using System.Text.Json;
using Shouldly;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trykatch.Infrastructure;
using Trykatch.Modules;
using Trykatch.Infrastructure.Mailing;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class UnavailableModuleEmailQueueTests
{
    [TestMethod]
    public async Task HostWithoutComposedQueueResolvesUnavailableWithoutDatabaseOrSmtp()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:trykatchdb"] = "Host=queue-no-network.invalid;Database=unused;Username=unused;Password=unused;Timeout=1"
        }).Build();
        ServiceCollection services = new();
        services.AddInfrastructure(configuration, isDevelopment: true, isOpenApiGeneration: true);
        using ServiceProvider provider = services.BuildServiceProvider();

        ModuleWorkReceipt receipt = await provider.GetRequiredService<IModuleEmailQueue>().EnqueueAsync(Message("private"));

        receipt.State.ShouldBe(ModuleWorkState.Unavailable);
        receipt.WorkId.ShouldBe(Guid.Empty);
        receipt.FailureCode.ShouldBe("email_unavailable");
    }

    [TestMethod]
    public async Task DirectAdapterReturnsFixedUnavailableReceiptWithoutDisclosingIntent()
    {
        const string privateMarker = "recipient-payload-correlation-private-marker";
        UnavailableModuleEmailQueue queue = new();

        ModuleWorkReceipt receipt = await queue.EnqueueAsync(Message(privateMarker));

        receipt.ShouldBe(new ModuleWorkReceipt(Guid.Empty, ModuleWorkState.Unavailable, FailureCode: "email_unavailable"));
        receipt.ToString().ShouldNotContain(privateMarker);
    }

    [TestMethod]
    public async Task CanceledCallerTokenIsObservedBeforeUnavailableReceipt()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        UnavailableModuleEmailQueue queue = new();

        OperationCanceledException error = await Should.ThrowAsync<OperationCanceledException>(() =>
            queue.EnqueueAsync(Message("private"), cancellation.Token));

        error.CancellationToken.ShouldBe(cancellation.Token);
    }

    private static ModuleEmailMessage Message(string privateMarker)
    {
        using JsonDocument data = JsonDocument.Parse(JsonSerializer.Serialize(privateMarker));
        return new ModuleEmailMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new ModuleEmailTemplateRef("identity", "identity.reset", 1),
            new ModuleEmailDisclosurePolicyRef("identity", "identity.reset-disclose", 1),
            new ModuleEmailSourceRef("users", privateMarker),
            new ModuleEmailRecipientIntent(privateMarker),
            data.RootElement.Clone(),
            privateMarker);
    }
}
