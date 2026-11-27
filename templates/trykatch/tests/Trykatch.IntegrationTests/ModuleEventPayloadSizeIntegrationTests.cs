using System.Text;
using System.Text.Json;
using Shouldly;
using Trykatch.Application.Execution;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleEventDeliveryIntegrationTests
{
    [TestMethod]
    public async Task ExactEventAndConsumerPayloadLimitSurvivesJsonbFormattingAndExecutesOnce()
    {
        await using EventFixture fixture = await EventFixture.StartAsync(consumerCount: 1);
        SeededEvent seeded = await fixture.SeedEventAsync(exactPayloadBoundary: true);
        (await fixture.ReadRenderedSizeAsync(seeded.MessageId)).ShouldBeGreaterThan(4096);

        await fixture.FanOutAsync();
        (await fixture.CountDeliveriesAsync(seeded.MessageId)).ShouldBe(1);
        Guid delivery = await fixture.DeliveryIdAsync(seeded.MessageId, ConsumerOne);

        (await fixture.ProcessAsync(delivery)).State.ShouldBe(ModuleExecutionState.Completed);
        (await fixture.ProcessAsync(delivery)).State.ShouldBe(ModuleExecutionState.AlreadyCompleted);
        (await fixture.ReadEffectCountAsync(seeded.OrganizationId, ConsumerOne)).ShouldBe(1);
        (await fixture.ReadInboxCountAsync(seeded.OrganizationId, seeded.MessageId)).ShouldBe(1);
    }

    private sealed partial class EventFixture
    {
        private static string ExactBoundaryPayload(EventPayload payload)
        {
            string prefix = JsonSerializer.Serialize(payload, WebJson)[..^1] + ",\"padding\":\"";
            const string suffix = "\"}";
            int paddingLength = 4096 - Encoding.UTF8.GetByteCount(prefix + suffix);
            string result = prefix + new string('x', paddingLength) + suffix;
            Encoding.UTF8.GetByteCount(result).ShouldBe(4096);
            return result;
        }

        public async Task<int> ReadRenderedSizeAsync(Guid messageId) => (int)(await ScalarOwnerAsync(
            "SELECT octet_length(\"Payload\"::text) FROM platform.outbox_messages WHERE \"Id\"=@id",
            ("id", messageId)))!;
    }
}
