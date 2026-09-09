using System.Diagnostics.Metrics;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace TrykatchApp.ServiceDefaults.Observability;

internal sealed class ObservabilityPolicy
{
    private static readonly string[] SignalNames = ["TRACES", "METRICS", "LOGS"];
    private static readonly HashSet<string> AllowedResourceAttributes = new(StringComparer.Ordinal)
    {
        "service.name", "service.namespace", "service.version", "service.instance.id", "deployment.environment.name"
    };

    private ObservabilityPolicy(IReadOnlyDictionary<string, object> resourceAttributes, Uri? endpoint,
        OtlpExportProtocol protocol, IReadOnlyDictionary<string, string> headers, Sampler sampler)
    {
        ResourceAttributes = resourceAttributes;
        Endpoint = endpoint;
        Protocol = protocol;
        Headers = headers;
        HeadersText = string.Join(',', headers.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
        Sampler = sampler;
    }

    public IReadOnlyDictionary<string, object> ResourceAttributes { get; }
    public Uri? Endpoint { get; }
    public OtlpExportProtocol Protocol { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public string HeadersText { get; }
    public Sampler Sampler { get; }
    public bool ExportEnabled => Endpoint is not null;

    public static ObservabilityPolicy Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        Dictionary<string, string> configuredResource = ParsePairs(configuration["OTEL_RESOURCE_ATTRIBUTES"], "resource attributes");
        string serviceName = First(configuration["OTEL_SERVICE_NAME"], configuredResource.GetValueOrDefault("service.name"), environment.ApplicationName);
        string serviceNamespace = First(configuredResource.GetValueOrDefault("service.namespace"), configuration["Telemetry:ServiceNamespace"], "trykatch");
        string serviceVersion = First(configuredResource.GetValueOrDefault("service.version"), configuration["TRYKATCH_RELEASE_VERSION"],
            Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, "development");
        string instanceId = First(configuredResource.GetValueOrDefault("service.instance.id"), Guid.CreateVersion7().ToString("N"));
        string deploymentEnvironment = First(configuredResource.GetValueOrDefault("deployment.environment.name"),
            configuration["OTEL_DEPLOYMENT_ENVIRONMENT"], NormalizeEnvironment(environment.EnvironmentName));

        var resource = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["service.name"] = ValidateIdentity(serviceName, "service.name"),
            ["service.namespace"] = ValidateIdentity(serviceNamespace, "service.namespace"),
            ["service.version"] = ValidateIdentity(serviceVersion, "service.version"),
            ["service.instance.id"] = ValidateIdentity(instanceId, "service.instance.id"),
            ["deployment.environment.name"] = ValidateIdentity(deploymentEnvironment, "deployment.environment.name")
        };

        foreach (string key in configuredResource.Keys)
        {
            if (!AllowedResourceAttributes.Contains(key))
            {
                throw Invalid($"OTEL_RESOURCE_ATTRIBUTES contains unsupported attribute '{key}'.");
            }
        }

        string? endpointText = EmptyToNull(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        EnsureSignalOverridesAgree(configuration, "ENDPOINT", endpointText);
        string? protocolText = EmptyToNull(configuration["OTEL_EXPORTER_OTLP_PROTOCOL"]);
        EnsureSignalOverridesAgree(configuration, "PROTOCOL", protocolText);
        string? headersText = ResolveHeaders(configuration);
        EnsureSignalOverridesAgree(configuration, "HEADERS", headersText);

        Uri? endpoint = null;
        OtlpExportProtocol protocol = OtlpExportProtocol.HttpProtobuf;
        Dictionary<string, string> headers = [];
        if (endpointText is not null)
        {
            if (!Uri.TryCreate(endpointText, UriKind.Absolute, out endpoint) || endpoint.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            {
                throw Invalid("OTEL_EXPORTER_OTLP_ENDPOINT must be an absolute HTTP(S) base endpoint without credentials, query, or fragment.");
            }
            protocol = protocolText?.Trim().ToLowerInvariant() switch
            {
                null or "http/protobuf" => OtlpExportProtocol.HttpProtobuf,
                "grpc" => OtlpExportProtocol.Grpc,
                _ => throw Invalid("OTEL_EXPORTER_OTLP_PROTOCOL must be 'http/protobuf' or 'grpc'.")
            };
            headers = ParsePairs(headersText, "exporter headers");
        }

        double ratio = ResolveSamplingRatio(configuration, environment);
        return new ObservabilityPolicy(resource, endpoint, protocol, headers,
            new ParentBasedSampler(new TraceIdRatioBasedSampler(ratio)));
    }

    public static MetricStreamConfiguration MetricView(Instrument instrument)
    {
        string[] tagKeys = instrument.Meter.Name switch
        {
            "TrykatchApp.Outbox" when instrument.Name == "trykatch.outbox.dispatches" => ["outcome"],
            "TrykatchApp.Outbox" => [],
            "Microsoft.AspNetCore.Hosting" => ["http.request.method", "http.response.status_code", "http.route", "network.protocol.version", "url.scheme"],
            "Npgsql" => ["db.operation.name", "db.namespace", "server.address", "server.port"],
            _ => []
        };
        return new MetricStreamConfiguration { TagKeys = tagKeys, CardinalityLimit = 200 };
    }

    private static double ResolveSamplingRatio(IConfiguration configuration, IHostEnvironment environment)
    {
        string? sampler = EmptyToNull(configuration["OTEL_TRACES_SAMPLER"]);
        if (sampler is not null && !string.Equals(sampler, "parentbased_traceidratio", StringComparison.OrdinalIgnoreCase))
            throw Invalid("OTEL_TRACES_SAMPLER must be 'parentbased_traceidratio'.");
        string ratioText = EmptyToNull(configuration["OTEL_TRACES_SAMPLER_ARG"]) ?? (environment.IsDevelopment() ? "1" : "0.10");
        if (!double.TryParse(ratioText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double ratio) || ratio is < 0 or > 1)
            throw Invalid("OTEL_TRACES_SAMPLER_ARG must be a number from 0 through 1.");
        return ratio;
    }

    private static void EnsureSignalOverridesAgree(IConfiguration configuration, string suffix, string? common)
    {
        foreach (string signal in SignalNames)
        {
            string key = $"OTEL_EXPORTER_OTLP_{signal}_{suffix}";
            string? value = EmptyToNull(configuration[key]);
            if (value is not null && !string.Equals(value, common, StringComparison.Ordinal))
                throw Invalid($"{key} conflicts with the common OTLP export configuration; independent destinations are not supported.");
        }
    }

    private static Dictionary<string, string> ParsePairs(string? text, string description)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (string pair in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = pair.IndexOf('=');
            if (separator < 1 || separator == pair.Length - 1) throw Invalid($"OTLP {description} are malformed.");
            string key = pair[..separator].Trim();
            string value = Uri.UnescapeDataString(pair[(separator + 1)..].Trim());
            if (key.Length > 128 || value.Length > 1024 || !result.TryAdd(key, value))
                throw Invalid($"OTLP {description} are invalid or exceed policy limits.");
        }
        return result;
    }

    private static string? ResolveHeaders(IConfiguration configuration)
    {
        string? inline = EmptyToNull(configuration["OTEL_EXPORTER_OTLP_HEADERS"]);
        string? file = EmptyToNull(configuration["OTEL_EXPORTER_OTLP_HEADERS_FILE"]);
        if (inline is not null && file is not null)
            throw Invalid("Configure either OTEL_EXPORTER_OTLP_HEADERS or OTEL_EXPORTER_OTLP_HEADERS_FILE, not both.");
        if (file is null) return inline;
        try
        {
            FileInfo info = new(file);
            if (!info.Exists || info.Length is 0 or > 4096) throw Invalid("The OTLP exporter headers file is missing, empty, or too large.");
            return File.ReadAllText(info.FullName).Trim();
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw Invalid("The OTLP exporter headers file could not be read.");
        }
    }

    private static string NormalizeEnvironment(string value) => value.Trim().ToLowerInvariant() switch
    {
        "development" => "development", "staging" => "staging", "production" => "production", _ => "other"
    };

    private static string ValidateIdentity(string value, string key)
    {
        string trimmed = value.Trim();
        if (trimmed.Length is 0 or > 128 || trimmed.Any(char.IsControl)) throw Invalid($"The resolved {key} is invalid.");
        return trimmed;
    }

    private static string First(params string?[] candidates) => candidates.Select(EmptyToNull).First(value => value is not null)!;
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static InvalidOperationException Invalid(string message) =>
        new($"Invalid observability configuration: {message} Values and exporter headers are not included in this error.");
}
