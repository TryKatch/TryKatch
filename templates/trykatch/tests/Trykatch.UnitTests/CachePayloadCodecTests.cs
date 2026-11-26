using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shouldly;
using Trykatch.Infrastructure.Caching;
using Trykatch.Modules.Projects.Domain;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class CachePayloadCodecTests
{
    [TestMethod]
    public void EntityPayloadIsRejectedEvenWhenNestedBehindAnObjectProperty()
    {
        Project entity = Project.Create(Guid.NewGuid(), "private entity payload", null, Guid.NewGuid());

        NotSupportedException failure = Should.Throw<NotSupportedException>(() =>
            CachePayloadCodec.Encode(new EntityContainer(entity)));

        failure.Message.ShouldNotContain(entity.Name);
    }

    [TestMethod]
    public void EncodeWritesVersionAndTypeBoundHeaderBeforeTypedJson()
    {
        byte[] payload = CachePayloadCodec.Encode(new CacheDto { Name = "detached" });

        payload.Length.ShouldBeGreaterThan(33);
        payload[0].ShouldBe((byte)1);
        SHA256.HashData(Encoding.UTF8.GetBytes(typeof(CacheDto).AssemblyQualifiedName!))
            .ShouldBe(payload.AsSpan(1, 32).ToArray());
        Encoding.UTF8.GetString(payload.AsSpan(33)).ShouldContain("detached");
    }

    [TestMethod]
    public void RepeatedRoundTripsProduceIndependentNestedGraphs()
    {
        CacheDto source = new()
        {
            Name = "original",
            Values = ["one", "two"],
            Child = new NestedDto { Value = "nested" }
        };
        byte[] payload = CachePayloadCodec.Encode(source);

        CachePayloadCodec.TryDecode<CacheDto>(payload, out CacheDto? first).ShouldBeTrue();
        CachePayloadCodec.TryDecode<CacheDto>(payload, out CacheDto? second).ShouldBeTrue();
        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        ReferenceEquals(first, second).ShouldBeFalse();
        ReferenceEquals(first.Child, second.Child).ShouldBeFalse();
        ReferenceEquals(first.Values, second.Values).ShouldBeFalse();

        first.Name = "changed";
        first.Child!.Value = "changed nested";
        first.Values.Add("three");
        second.Name.ShouldBe("original");
        second.Child!.Value.ShouldBe("nested");
        second.Values.ShouldBe(["one", "two"]);
    }

    [TestMethod]
    public void WrongVersionTruncatedHeaderAndDifferentDtoTypeAreRejected()
    {
        byte[] payload = CachePayloadCodec.Encode(new CacheDto { Name = "typed" });
        byte[] wrongVersion = payload.ToArray();
        wrongVersion[0] = 2;

        CachePayloadCodec.TryDecode<CacheDto>(wrongVersion, out CacheDto? _).ShouldBeFalse();
        CachePayloadCodec.TryDecode<CacheDto>(payload.AsSpan(0, 32), out CacheDto? _).ShouldBeFalse();
        CachePayloadCodec.TryDecode<OtherCacheDto>(payload, out OtherCacheDto? _).ShouldBeFalse();
    }

    [TestMethod]
    public void MalformedJsonIsRejectedWithoutExposingPayload()
    {
        const string rawValue = "private-cache-payload-material";
        byte[] validHeader = CachePayloadCodec.Encode(new CacheDto { Name = "header" })[..33];
        byte[] malformedPayload = [.. validHeader, .. Encoding.UTF8.GetBytes($"{{\"Name\":\"{rawValue}")];

        CachePayloadCodec.TryDecode<CacheDto>(malformedPayload, out CacheDto? value).ShouldBeFalse();
        value.ShouldBeNull();
    }

    [TestMethod]
    public void JsonNullAndPrimitiveValuesRoundTripNormally()
    {
        byte[] nullPayload = CachePayloadCodec.Encode<string?>(null);
        CachePayloadCodec.TryDecode<string?>(nullPayload, out string? nullValue).ShouldBeTrue();
        nullValue.ShouldBeNull();

        byte[] primitivePayload = CachePayloadCodec.Encode(42);
        CachePayloadCodec.TryDecode<int>(primitivePayload, out int primitiveValue).ShouldBeTrue();
        primitiveValue.ShouldBe(42);
    }

    [TestMethod]
    public void PayloadsDeeperThanSixteenLevelsAreRejected()
    {
        NestedNode tooDeep = BuildNestedNode(20);

        Should.Throw<JsonException>(() => CachePayloadCodec.Encode(tooDeep));
    }

    [TestMethod]
    public void LargePayloadCanRoundTripBecauseSizeLimitsBelongToTheAdapter()
    {
        string content = new('x', OrganizationCacheOptions.MaximumPayloadBytes + 256);
        byte[] payload = CachePayloadCodec.Encode(new CacheDto { Name = content });

        payload.Length.ShouldBeGreaterThan(OrganizationCacheOptions.MaximumPayloadBytes);
        CachePayloadCodec.TryDecode<CacheDto>(payload, out CacheDto? decoded).ShouldBeTrue();
        decoded!.Name.ShouldBe(content);
    }

    private static NestedNode BuildNestedNode(int count)
    {
        NestedNode node = new();
        for (int index = 1; index < count; index++)
            node = new NestedNode { Next = node };
        return node;
    }

    private sealed class CacheDto
    {
        public string Name { get; set; } = "";
        public List<string> Values { get; set; } = [];
        public NestedDto? Child { get; set; }
    }

    private sealed record EntityContainer(object Value);

    private sealed class NestedDto
    {
        public string Value { get; set; } = "";
    }

    private sealed class OtherCacheDto
    {
        public string Name { get; set; } = "";
    }

    private sealed class NestedNode
    {
        public NestedNode? Next { get; set; }
    }
}
