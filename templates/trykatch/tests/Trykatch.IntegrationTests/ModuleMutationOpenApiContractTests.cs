using System.Text.Json;
using Shouldly;

namespace Trykatch.IntegrationTests;

[TestClass, TestCategory("Integration")]
public sealed class ModuleMutationOpenApiContractTests
{
    private static readonly string[] EnrolledOperations =
        ["Projects_Create", "Projects_Update", "Projects_Archive", "Projects_Restore", "Projects_Delete"];

    [TestMethod]
    [DataRow("Projects_Create")]
    [DataRow("Projects_Update")]
    [DataRow("Projects_Archive")]
    [DataRow("Projects_Restore")]
    [DataRow("Projects_Delete")]
    public void GeneratedContractDescribesOptionalMutationKeyAndFinitePolicy(string operationId)
    {
        using JsonDocument document = ReadGeneratedContract();
        JsonElement operation = Operations(document.RootElement).Single(value =>
            value.GetProperty("operationId").GetString() == operationId);
        JsonElement[] headers = Parameters(operation).Where(value =>
            value.GetProperty("name").GetString() == "Idempotency-Key").ToArray();
        headers.Length.ShouldBe(1, "a generated client must discover the opt-in header exactly once");
        JsonElement header = headers[0];
        header.GetProperty("in").GetString().ShouldBe("header");
        (header.TryGetProperty("required", out JsonElement required) && required.GetBoolean()).ShouldBeFalse();
        JsonElement schema = header.GetProperty("schema");
        schema.GetProperty("type").GetString().ShouldBe("string");
        schema.GetProperty("minLength").GetInt32().ShouldBe(1);
        schema.GetProperty("maxLength").GetInt32().ShouldBe(128);
        schema.GetProperty("pattern").GetString().ShouldBe("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$");
        JsonElement policy = operation.GetProperty("x-trykatch-idempotency");
        policy.GetProperty("keyRequired").GetBoolean().ShouldBeFalse();
        policy.GetProperty("replayLifetimeSeconds").GetInt64().ShouldBe(86_400);
        policy.GetProperty("maximumRequestBytes").GetInt32().ShouldBe(65_536);
        policy.GetProperty("maximumResponseBytes").GetInt32().ShouldBe(262_144);
        policy.GetProperty("responseLimitAlsoBoundedByAtomicStaging").GetBoolean().ShouldBeTrue();
        operation.TryGetProperty("x-trykatch-assistant-tool", out _).ShouldBeFalse(
            "documenting mutation replay must not enable mutation assistant execution");
    }

    [TestMethod]
    public void GeneratedContractDoesNotAdvertiseReplayOnUnenrolledOperations()
    {
        using JsonDocument document = ReadGeneratedContract();
        JsonElement[] ordinary = Operations(document.RootElement).Where(value =>
            !EnrolledOperations.Contains(value.GetProperty("operationId").GetString(), StringComparer.Ordinal)).ToArray();
        ordinary.Length.ShouldBeGreaterThan(0);
        foreach (JsonElement operation in ordinary)
        {
            Parameters(operation).Any(value => value.GetProperty("name").GetString() == "Idempotency-Key").ShouldBeFalse();
            operation.TryGetProperty("x-trykatch-idempotency", out _).ShouldBeFalse();
        }
    }

    private static JsonElement[] Parameters(JsonElement operation) =>
        operation.TryGetProperty("parameters", out JsonElement values) ? values.EnumerateArray().ToArray() : [];

    private static IEnumerable<JsonElement> Operations(JsonElement document) =>
        document.GetProperty("paths").EnumerateObject().SelectMany(path => path.Value.EnumerateObject())
            .Where(value => value.Value.ValueKind == JsonValueKind.Object && value.Value.TryGetProperty("operationId", out _))
            .Select(value => value.Value);

    private static JsonDocument ReadGeneratedContract()
    {
        string assembly = typeof(global::Program).Assembly.GetName().Name!;
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!directory.EnumerateFiles("*.slnx").Any()) continue;
            string web = Path.Combine(directory.FullName, "web", "packages", "api-client", "openapi", assembly + ".json");
            string backend = Path.Combine(directory.FullName, "src", "API", assembly, "obj", "openapi", assembly + ".json");
            string contract = File.Exists(web) ? web : backend;
            File.Exists(contract).ShouldBeTrue("the normal API build must generate the shipped OpenAPI contract before tests");
            return JsonDocument.Parse(File.ReadAllBytes(contract));
        }
        throw new DirectoryNotFoundException("The generated application's root was not found.");
    }
}
