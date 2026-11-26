using System.Text;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Trykatch.Modules.AspNetCore;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ModuleRequestFingerprintTests
{
    private const string Key = "request-123";

    [TestMethod]
    [DataRow("{\"b\":2.00,\"a\":1e0}", "{\"a\":1,\"b\":2}")]
    [DataRow("{\"value\":1000}", "{\"value\":1e3}")]
    public void CanonicalHashIgnoresObjectOrderAndEquivalentNumberSpellings(string first, string second)
    {
        ModuleCanonicalJson.ComputeHash(first).ShouldBe(ModuleCanonicalJson.ComputeHash(second));
    }

    [TestMethod]
    public void CanonicalHashMatchesGoldenVectorsAndRetainsArrayOrder()
    {
        ModuleCanonicalJson.ComputeHash("{\"a\":1}")
            .ShouldBe("533E2D9500A389F8ABF32F851E66F98FFCDE79CB3F5719F46B80A5FC52F944EE");
        ModuleCanonicalJson.ComputeHash("{\"x\":2,\"items\":[1,2]}")
            .ShouldBe("74D8B6FE227FF3A194249F22DB5CDB84CB2C4754666AAF7D4CBF2EAF582AA001");
        ModuleCanonicalJson.ComputeHash("[1,2]").ShouldNotBe(ModuleCanonicalJson.ComputeHash("[2,1]"));
    }

    [TestMethod]
    public void OutboxHashCompatibilityWrapperKeepsExistingCanonicalDigest()
    {
        const string payload = "{\"b\":2.00,\"a\":1e0}";
        OutboxPayloadHash.Compute(payload)
            .ShouldBe("3FC8740ACA49DFB85FF73B061896191326CA8BC24D3CFB4D8B1BAECEA8786058");
        OutboxPayloadHash.Compute(payload).ShouldBe(ModuleCanonicalJson.ComputeHash(payload));
    }

    [TestMethod]
    public async Task EquivalentJsonRequestsHaveTheSameFingerprintAndRewindTheBody()
    {
        DefaultHttpContext first = JsonRequest("POST", "/api/v1/projects", "?source=web", "{\"name\":\"P\",\"size\":1.0}");
        DefaultHttpContext second = JsonRequest("POST", "/api/v1/projects", "?source=web", "{\"size\":1e0,\"name\":\"P\"}");
        AddKey(first.Request, Key);
        AddKey(second.Request, Key);

        ModulePreparedMutationRequest left = (await PrepareAsync(first.Request))!;
        ModulePreparedMutationRequest right = (await PrepareAsync(second.Request))!;

        left.KeyHash.ShouldBe(right.KeyHash);
        left.RequestHash.ShouldBe(right.RequestHash);
        first.Request.Body.Position.ShouldBe(0);
        second.Request.Body.Position.ShouldBe(0);
    }

    [TestMethod]
    public async Task FingerprintSeparatesMethodPathQueryBodyAndNormalizedMediaType()
    {
        string baseline = (await FingerprintAsync(JsonRequest("POST", "/api/v1/projects", "?a=1&b=2", "{\"name\":\"P\"}", "application/json; charset=utf-8"))).RequestHash;
        (await FingerprintAsync(JsonRequest("PUT", "/api/v1/projects", "?a=1&b=2", "{\"name\":\"P\"}", "application/json; charset=utf-8"))).RequestHash.ShouldNotBe(baseline);
        (await FingerprintAsync(JsonRequest("POST", "/api/v1/projects/", "?a=1&b=2", "{\"name\":\"P\"}", "application/json; charset=utf-8"))).RequestHash.ShouldNotBe(baseline);
        (await FingerprintAsync(JsonRequest("POST", "/api/v1/projects", "?b=2&a=1", "{\"name\":\"P\"}", "application/json; charset=utf-8"))).RequestHash.ShouldNotBe(baseline);
        (await FingerprintAsync(JsonRequest("POST", "/api/v1/projects", "?a=1&b=2", "{\"name\":\"Q\"}", "application/json; charset=utf-8"))).RequestHash.ShouldNotBe(baseline);
        (await FingerprintAsync(JsonRequest("POST", "/api/v1/projects", "?a=1&b=2", "{\"name\":\"P\"}", "application/vnd.trykatch.project+json"))).RequestHash.ShouldNotBe(baseline);
    }

    [TestMethod]
    public async Task PathBaseIsIncludedAndTheKeyChangesOnlyTheKeyHash()
    {
        DefaultHttpContext withPathBase = JsonRequest("POST", "/projects", "", "{}", "application/json; charset=utf-8");
        withPathBase.Request.PathBase = "/api";
        ModulePreparedMutationRequest first = await FingerprintAsync(withPathBase);

        DefaultHttpContext sameRequestDifferentKey = JsonRequest("POST", "/projects", "", "{}", "Application/JSON; Charset=UTF-8");
        sameRequestDifferentKey.Request.PathBase = "/api";
        AddKey(sameRequestDifferentKey.Request, "request-456");
        ModulePreparedMutationRequest second = (await PrepareAsync(sameRequestDifferentKey.Request))!;

        second.RequestHash.ShouldBe(first.RequestHash);
        second.KeyHash.ShouldNotBe(first.KeyHash);

        ModulePreparedMutationRequest withoutPathBase = await FingerprintAsync(JsonRequest("POST", "/projects", "", "{}"));
        withoutPathBase.RequestHash.ShouldNotBe(first.RequestHash);
    }

    [TestMethod]
    public async Task AbsentBodyHasAnIdentityDistinctFromJsonNull()
    {
        DefaultHttpContext absent = new();
        absent.Request.Method = "POST";
        absent.Request.Path = "/projects";
        DefaultHttpContext jsonNull = JsonRequest("POST", "/projects", "", "null");
        (await FingerprintAsync(absent)).RequestHash.ShouldNotBe((await FingerprintAsync(jsonNull)).RequestHash);
    }

    [TestMethod]
    public async Task OptionalMissingKeyDoesNotReadOrBufferTheRequestBody()
    {
        DefaultHttpContext context = new();
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/projects";
        CountingReadStream body = new(Encoding.UTF8.GetBytes("unread"), canSeek: false);
        context.Request.Body = body;

        (await ModuleRequestFingerprint.PrepareAsync(context.Request, ModuleMutationPolicy.Default)).ShouldBeNull();
        body.BytesRead.ShouldBe(0);
    }

    [TestMethod]
    public async Task RequiredMissingAndMalformedOrRepeatedKeysAreRejectedAsBadRequests()
    {
        DefaultHttpContext missing = JsonRequest("POST", "/projects", "", "{}");
        await ExpectFailureAsync(missing.Request, RequiredPolicy(), StatusCodes.Status400BadRequest);

        foreach (string invalid in new[] { "", " starts-wrong", "two words", "é", "_starts-wrong", new string('a', 129) })
        {
            DefaultHttpContext context = JsonRequest("POST", "/projects", "", "{}");
            context.Request.Headers["Idempotency-Key"] = invalid;
            await ExpectFailureAsync(context.Request, ModuleMutationPolicy.Default, StatusCodes.Status400BadRequest);
        }

        DefaultHttpContext duplicate = JsonRequest("POST", "/projects", "", "{}");
        duplicate.Request.Headers.Append("Idempotency-Key", "first");
        duplicate.Request.Headers.Append("Idempotency-Key", "second");
        await ExpectFailureAsync(duplicate.Request, ModuleMutationPolicy.Default, StatusCodes.Status400BadRequest);
    }

    [TestMethod]
    public async Task UnsupportedMediaOrContentEncodingIsRejectedBeforeBodyRead()
    {
        foreach ((string? contentType, string? encoding) in new (string?, string?)[]
                 { ("text/plain", null), ("application/json", "gzip"), ("application/json; charset=iso-8859-1", null) })
        {
            DefaultHttpContext context = JsonRequest("POST", "/projects", "", "{}", contentType);
            AddKey(context.Request, Key);
            context.Request.Headers.ContentEncoding = encoding;
            CountingReadStream body = new(Encoding.UTF8.GetBytes("{}"), canSeek: false);
            context.Request.Body = body;

            await ExpectFailureAsync(context.Request, ModuleMutationPolicy.Default, StatusCodes.Status415UnsupportedMediaType);
            body.BytesRead.ShouldBe(0);
        }
    }

    [TestMethod]
    public async Task InvalidUtf8MalformedDuplicateAndTooDeepJsonAreRejected()
    {
        DefaultHttpContext invalidUtf8 = JsonBytesRequest([0x7b, 0x22, 0x78, 0x22, 0x3a, 0xff, 0x7d]);
        DefaultHttpContext malformed = JsonRequest("POST", "/projects", "", "{\"x\":}");
        DefaultHttpContext duplicate = JsonRequest("POST", "/projects", "", "{\"x\":1,\"x\":2}");
        string deepJson = new string('[', 66) + "0" + new string(']', 66);
        DefaultHttpContext tooDeep = JsonRequest("POST", "/projects", "", deepJson);

        foreach (DefaultHttpContext context in new[] { invalidUtf8, malformed, duplicate, tooDeep })
        {
            AddKey(context.Request, Key);
            await ExpectFailureAsync(context.Request, ModuleMutationPolicy.Default, StatusCodes.Status400BadRequest);
        }
    }

    [TestMethod]
    [DataRow("{\"name\":\"First\",\"Name\":\"Second\"}")]
    [DataRow("{\"items\":[{\"code\":1,\"Code\":2}]}")]
    [DataRow("{\"name\":\"First\",\"\\u004eame\":\"Second\"}")]
    public async Task CaseInsensitiveBindingAliasesAreRejectedBeforeFingerprinting(string body)
    {
        DefaultHttpContext context = JsonRequest("POST", "/projects", "", body);
        AddKey(context.Request, Key);
        await ExpectFailureAsync(context.Request, ModuleMutationPolicy.Default, StatusCodes.Status400BadRequest);
    }

    [TestMethod]
    public async Task DeclaredAndChunkedBodiesAreBoundedByThePolicy()
    {
        ModuleMutationPolicy policy = new(TimeSpan.FromHours(1), MaximumRequestBytes: 8);
        DefaultHttpContext declared = JsonRequest("POST", "/projects", "", "{\"x\":123}");
        AddKey(declared.Request, Key);
        declared.Request.ContentLength = 9;
        CountingReadStream declaredBody = new(Encoding.UTF8.GetBytes("{\"x\":123}"), canSeek: false);
        declared.Request.Body = declaredBody;
        await ExpectFailureAsync(declared.Request, policy, StatusCodes.Status413PayloadTooLarge);
        declaredBody.BytesRead.ShouldBe(0);

        DefaultHttpContext chunked = new();
        chunked.Request.Method = "POST";
        chunked.Request.Path = "/projects";
        chunked.Request.ContentType = "application/json";
        AddKey(chunked.Request, Key);
        chunked.Request.Body = new CountingReadStream(Encoding.UTF8.GetBytes("{\"x\":123456789}"), canSeek: false);
        await ExpectFailureAsync(chunked.Request, policy, StatusCodes.Status413PayloadTooLarge);
        ((CountingReadStream)chunked.Request.Body).BytesRead.ShouldBeLessThanOrEqualTo(9);
    }

    [TestMethod]
    public async Task CancellationIsObservedDuringPreparation()
    {
        DefaultHttpContext context = JsonRequest("POST", "/projects", "", "{}");
        AddKey(context.Request, Key);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            ModuleRequestFingerprint.PrepareAsync(context.Request, ModuleMutationPolicy.Default, cancellation.Token));
    }

    private static ModuleMutationPolicy RequiredPolicy() => new(TimeSpan.FromHours(1), KeyRequired: true);

    private static Task<ModulePreparedMutationRequest?> PrepareAsync(HttpRequest request) =>
        ModuleRequestFingerprint.PrepareAsync(request, ModuleMutationPolicy.Default);

    private static async Task<ModulePreparedMutationRequest> FingerprintAsync(DefaultHttpContext context)
    {
        AddKey(context.Request, Key);
        return (await PrepareAsync(context.Request))!;
    }

    private static DefaultHttpContext JsonRequest(
        string method,
        string path,
        string query,
        string body,
        string? contentType = "application/json")
    {
        DefaultHttpContext context = new();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return context;
    }

    private static DefaultHttpContext JsonBytesRequest(byte[] body)
    {
        DefaultHttpContext context = new();
        context.Request.Method = "POST";
        context.Request.Path = "/projects";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(body);
        return context;
    }

    private static void AddKey(HttpRequest request, string key) => request.Headers.Append("Idempotency-Key", key);

    private static async Task ExpectFailureAsync(HttpRequest request, ModuleMutationPolicy policy, int expectedStatus)
    {
        ModuleMutationRequestException exception = await Should.ThrowAsync<ModuleMutationRequestException>(
            () => ModuleRequestFingerprint.PrepareAsync(request, policy));
        exception.StatusCode.ShouldBe(expectedStatus);
        exception.Code.ShouldNotBeNullOrWhiteSpace();
    }

    private sealed class CountingReadStream(byte[] data, bool canSeek) : Stream
    {
        private int position;
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => canSeek;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position
        {
            get => position;
            set
            {
                if (!CanSeek) throw new NotSupportedException();
                position = checked((int)value);
            }
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = Math.Min(count, data.Length - position);
            Array.Copy(data, position, buffer, offset, read);
            position += read;
            BytesRead += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = Math.Min(buffer.Length, data.Length - position);
            data.AsMemory(position, read).CopyTo(buffer);
            position += read;
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
