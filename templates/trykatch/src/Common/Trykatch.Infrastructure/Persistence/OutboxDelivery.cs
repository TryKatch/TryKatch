using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Logging;
using Trykatch.Application.Outbox;

namespace Trykatch.Infrastructure.Persistence;

internal sealed partial class OutboxDelivery(
    IOutboxTransport transport,
    TimeProvider timeProvider,
    OutboxWorkerState state,
    ILogger<OutboxDelivery> logger)
{
    private static readonly ActivitySource ActivitySource = new("Trykatch.Outbox");
    private static readonly Meter Meter = new("Trykatch.Outbox");
    private static readonly Counter<long> DispatchCounter = Meter.CreateCounter<long>("trykatch.outbox.dispatches");
    private static readonly Histogram<double> DispatchDuration = Meter.CreateHistogram<double>("trykatch.outbox.dispatch.duration", "s");

    public async Task<bool> DeliverAsync(OutboxMessage message, TimeSpan timeout, CancellationToken stoppingToken)
    {
        if (message.ProcessedAt is not null || message.ExhaustedAt is not null) return false;
        stoppingToken.ThrowIfCancellationRequested();
        if (!OutboxContextReader.TryRead(message, out OutboxContext? context, out bool legacy))
        {
            message.ExhaustedAt = timeProvider.GetUtcNow();
            message.LastErrorCode = "outbox_context_invalid";
            message.LastErrorType = "context_validation";
            DispatchCounter.Add(1, new KeyValuePair<string, object?>("outcome", "context_invalid"));
            return false;
        }

        await state.WaitForTransportAsync(stoppingToken);
        stoppingToken.ThrowIfCancellationRequested();

        long started = Stopwatch.GetTimestamp();
        using Activity? activity = ActivitySource.StartActivity("outbox publish", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "trykatch.outbox");
        activity?.SetTag("messaging.operation.type", "publish");
        activity?.SetTag("messaging.message.id", message.Id);
        activity?.SetTag("messaging.destination.name", message.Type);

        using CancellationTokenSource deadline = new(timeout, timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, deadline.Token);
        try
        {
            Task publish = transport.PublishAsync(
                new OutboxEnvelope(message.Id, message.Type, message.Payload, message.OccurredAt, message.Attempts + 1)
                {
                    Context = legacy ? null : context,
                    PayloadHash = legacy ? null : message.PayloadHash
                },
                linked.Token);
            Task wait = Task.Delay(timeout, timeProvider, stoppingToken);
            if (await Task.WhenAny(publish, wait) != publish)
            {
                linked.Cancel();
                state.TransportBlocked(true, timeProvider.GetUtcNow());
                state.TrackTransport(ObserveLatePublicationAsync(publish));
                stoppingToken.ThrowIfCancellationRequested();
                RecordFailure(message, "transport_timeout", typeof(TimeoutException).FullName!, activity);
                return false;
            }

            await publish;
            message.ProcessedAt = timeProvider.GetUtcNow();
            message.LastErrorCode = null;
            message.LastErrorType = null;
            DispatchCounter.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            RecordFailure(message, "transport_timeout", typeof(TimeoutException).FullName!, activity);
            return false;
        }
        catch (Exception exception)
        {
            string exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
            string code = exception is TimeoutException ? "transport_timeout"
                : exception is HttpRequestException ? "transport_unavailable"
                : "transport_failure";
            RecordFailure(message, code, exceptionType, activity);
            return false;
        }
        finally
        {
            DispatchDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    private async Task ObserveLatePublicationAsync(Task publication)
    {
        try
        {
            await publication;
        }
        catch (Exception exception)
        {
            LogLateFault(logger, exception.GetType().FullName ?? exception.GetType().Name);
        }
        finally
        {
            state.TransportBlocked(false, timeProvider.GetUtcNow());
        }
    }

    private void RecordFailure(OutboxMessage message, string code, string type, Activity? activity)
    {
        message.Attempts++;
        message.LastErrorCode = code;
        message.LastErrorType = type.Length <= 240 ? type : type[..240];
        activity?.SetStatus(ActivityStatusCode.Error);
        activity?.SetTag("error.type", message.LastErrorType);
        DispatchCounter.Add(1, new KeyValuePair<string, object?>("outcome", code));
        LogFailed(logger, message.Id, message.Attempts, message.LastErrorType);
    }

    [LoggerMessage(EventId = 4201, Level = LogLevel.Error, Message = "Outbox message {MessageId} failed on attempt {DeliveryAttempt} with {ExceptionType}")]
    private static partial void LogFailed(ILogger logger, Guid messageId, int deliveryAttempt, string exceptionType);

    [LoggerMessage(EventId = 4202, Level = LogLevel.Error, Message = "Outbox transport completed late with {ExceptionType}")]
    private static partial void LogLateFault(ILogger logger, string exceptionType);
}

internal static class OutboxContextReader
{
    private const int SupportedVersion = 1;
    private const int MaximumPayloadBytes = 1_048_576;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static bool TryRead(OutboxMessage message, out OutboxContext? context, out bool legacy)
    {
        context = null;
        legacy = false;
        if (message.ContextVersion is null && message.ContextJson is null && message.PayloadHash is null)
        {
            legacy = true;
            return true;
        }

        if (message.ContextVersion != SupportedVersion
            || string.IsNullOrWhiteSpace(message.ContextJson)
            || string.IsNullOrWhiteSpace(message.PayloadHash)
            || Encoding.UTF8.GetByteCount(message.ContextJson) > 16_384
            || Encoding.UTF8.GetByteCount(message.Payload) > MaximumPayloadBytes)
            return false;

        OutboxContext? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<OutboxContext>(message.ContextJson, SerializerOptions);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }

        if (parsed is null
            || parsed.OrganizationId == Guid.Empty
            || parsed.EffectiveActorId == Guid.Empty
            || parsed.OriginActorId == Guid.Empty
            || parsed.MembershipId == Guid.Empty
            || !string.Equals(parsed.ExecutionKind, "UserCommand", StringComparison.Ordinal)
            || !IsStableKey(parsed.OperationKey)
            || parsed.OperationVersion <= 0
            || !IsStableKey(parsed.EventKey)
            || parsed.SchemaVersion <= 0
            || !string.Equals(parsed.EventKey, message.Type, StringComparison.Ordinal)
            || !IsSha256(parsed.CatalogVersion)
            || (parsed.CorrelationId is { Length: > 128 }))
            return false;

        if (!IsSha256(message.PayloadHash))
            return false;

        byte[] expectedHash;
        byte[] actualHash;
        try
        {
            expectedHash = Convert.FromHexString(message.PayloadHash);
            actualHash = Convert.FromHexString(OutboxPayloadHash.Compute(message.Payload));
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
            return false;

        context = parsed;
        return true;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsStableKey(string? value)
    {
        if (value is not { Length: > 0 and <= 120 } || value[0] is < 'a' or > 'z')
            return false;

        bool separator = false;
        foreach (char character in value)
        {
            bool currentSeparator = character is '.' or '-';
            if (!(character is >= 'a' and <= 'z' or >= '0' and <= '9' || currentSeparator)
                || (currentSeparator && separator))
                return false;
            separator = currentSeparator;
        }
        return !separator;
    }
}

internal static class OutboxPayloadHash
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.Default,
        Indented = false
    };

    public static string Compute(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        using MemoryStream canonical = new();
        using (Utf8JsonWriter writer = new(canonical, WriterOptions))
            WriteCanonical(document.RootElement, writer);
        return Convert.ToHexString(SHA256.HashData(canonical.ToArray()));
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                string? previousName = null;
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (string.Equals(previousName, property.Name, StringComparison.Ordinal))
                        throw new InvalidOperationException("Duplicate JSON object properties cannot be hashed safely.");
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                    previousName = property.Name;
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                    WriteCanonical(item, writer);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(NormalizeNumber(element.GetRawText()));
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException("Unsupported JSON token.");
        }
    }

    private static string NormalizeNumber(string token)
    {
        int exponentIndex = token.IndexOfAny(['e', 'E']);
        string significand = exponentIndex < 0 ? token : token[..exponentIndex];
        BigInteger exponent = exponentIndex < 0
            ? BigInteger.Zero
            : BigInteger.Parse(token[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        bool negative = significand[0] == '-';
        if (negative) significand = significand[1..];

        int decimalIndex = significand.IndexOf('.');
        int fractionalDigits = decimalIndex < 0 ? 0 : significand.Length - decimalIndex - 1;
        string digits = decimalIndex < 0 ? significand : significand.Remove(decimalIndex, 1);
        exponent -= fractionalDigits;
        digits = digits.TrimStart('0');
        if (digits.Length == 0)
            return "0";

        int end = digits.Length;
        while (end > 1 && digits[end - 1] == '0')
        {
            end--;
            exponent++;
        }
        digits = digits[..end];
        return string.Concat(negative ? "-" : string.Empty, digits, "e", exponent.ToString(CultureInfo.InvariantCulture));
    }
}
