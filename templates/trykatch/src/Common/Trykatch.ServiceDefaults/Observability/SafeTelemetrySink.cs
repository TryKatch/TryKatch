using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Parsing;
using Serilog.Sinks.OpenTelemetry;

namespace Trykatch.ServiceDefaults.Observability;

internal sealed class SafeTelemetrySink : ILogEventSink, IDisposable
{
    private const int MaximumStringLength = 256;
    private static readonly MessageTemplate GenericTemplate = new MessageTemplateParser().Parse("Application event {EventId} from {SourceContext}");
    private static readonly HashSet<string> AllowedProperties = new(StringComparer.Ordinal)
    {
        "EventId", "EventName", "SourceContext", "RequestMethod", "RouteTemplate", "StatusCode",
        "ElapsedMilliseconds", "MessageId", "MessageType", "DeliveryAttempt", "ExceptionType", "Outcome", "ResponseStarted"
    };
    private static readonly HashSet<string> ApprovedTemplates = new(StringComparer.Ordinal)
    {
        "HTTP request {RequestMethod} {RouteTemplate} completed with {Outcome}, diagnostic status {StatusCode}, response started {ResponseStarted}, in {ElapsedMilliseconds:0.0000} ms",
        "Outbox message {MessageId} ({MessageType}) published on attempt {DeliveryAttempt}",
        "Outbox message {MessageId} failed on attempt {DeliveryAttempt} with {ExceptionType}",
        "Outbox transport completed late with {ExceptionType}",
        "Outbox database cycle failed with {ExceptionType} classified as {Outcome}"
    };
    private readonly Logger sink;

    public SafeTelemetrySink(ObservabilityPolicy policy)
    {
        LoggerConfiguration configuration = new LoggerConfiguration().WriteTo.Console(new RenderedCompactJsonFormatter());
        if (policy.ExportEnabled)
        {
            configuration.WriteTo.OpenTelemetry(options =>
            {
                options.Endpoint = policy.Endpoint!.AbsoluteUri;
                options.Protocol = policy.Protocol == OpenTelemetry.Exporter.OtlpExportProtocol.Grpc ? OtlpProtocol.Grpc : OtlpProtocol.HttpProtobuf;
                options.Headers = policy.Headers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                options.ResourceAttributes = policy.ResourceAttributes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                options.IncludedData = IncludedData.TraceIdField | IncludedData.SpanIdField |
                    IncludedData.MessageTemplateTextAttribute | IncludedData.SourceContextAttribute;
                options.OnBeginSuppressInstrumentation = OpenTelemetry.SuppressInstrumentationScope.Begin;
            }, ignoreEnvironment: true);
        }
        sink = configuration.CreateLogger();
    }

    public void Emit(LogEvent logEvent) => sink.Write(Sanitize(logEvent));

    internal static LogEvent Sanitize(LogEvent logEvent)
    {
        List<LogEventProperty> properties = [];
        foreach ((string name, LogEventPropertyValue value) in logEvent.Properties)
        {
            if (AllowedProperties.Contains(name) && TrySanitizeScalar(value, out LogEventPropertyValue? safeValue))
                properties.Add(new LogEventProperty(name, safeValue!));
        }
        if (logEvent.Exception is not null)
            properties.Add(new LogEventProperty("ExceptionType", new ScalarValue(logEvent.Exception.GetType().FullName ?? logEvent.Exception.GetType().Name)));
        MessageTemplate template = ApprovedTemplates.Contains(logEvent.MessageTemplate.Text) ? logEvent.MessageTemplate : GenericTemplate;
        LogEvent sanitized = logEvent.TraceId is { } traceId && logEvent.SpanId is { } spanId
            ? new LogEvent(logEvent.Timestamp, logEvent.Level, null, template, properties, traceId, spanId)
            : new LogEvent(logEvent.Timestamp, logEvent.Level, null, template, properties);
        return sanitized;
    }

    public void Dispose() => sink.Dispose();

    private static bool TrySanitizeScalar(LogEventPropertyValue value, out LogEventPropertyValue? safe)
    {
        safe = null;
        if (value is not ScalarValue scalar) return false;
        object? scalarValue = scalar.Value;
        if (scalarValue is string text)
        {
            safe = new ScalarValue(text.Length <= MaximumStringLength ? text : text[..MaximumStringLength]);
            return true;
        }
        if (scalarValue is null or bool or byte or short or int or long or float or double or decimal or Guid)
        {
            safe = scalar;
            return true;
        }
        return false;
    }
}
