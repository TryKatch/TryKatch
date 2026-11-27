using System.Text;
using Shouldly;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class OutboxPayloadSizeTests
{
    [TestMethod]
    public void CountsUtf8AndPreservesSpacesAndEscapedQuotesInsideStrings()
    {
        const string compact = """{"space key":["a, b: c","x\" y","z\\","é"]}""";
        const string stored = """{"space key": ["a, b: c", "x\" y", "z\\", "é"]}""";
        int bytes = Encoding.UTF8.GetByteCount(compact);

        OutboxPayloadSize.FitsStoredJson(stored, bytes).ShouldBeTrue();
        OutboxPayloadSize.FitsStoredJson(stored, bytes - 1).ShouldBeFalse();
    }

    [TestMethod]
    [DataRow("1e10", "10000000000")]
    [DataRow("-0.00120e-8", "-0.0000000000120")]
    [DataRow("-0e-9", "0.000000000")]
    [DataRow("""{"\u0065":1e15}""", """{"e":1000000000000000}""")]
    public void AccountsForDecimalExpansionBeforeTracking(string wire, string compactStored)
    {
        int bytes = Encoding.UTF8.GetByteCount(compactStored);
        byte[] serialized = Encoding.UTF8.GetBytes(wire);
        serialized.Length.ShouldBeLessThan(bytes);
        OutboxPayloadSize.FitsSerializedJson(serialized, bytes).ShouldBeTrue();
        OutboxPayloadSize.FitsSerializedJson(serialized, bytes - 1).ShouldBeFalse();
        OutboxPayloadSize.FitsStoredJson(compactStored, bytes).ShouldBeTrue();
    }
}
