using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using Serilog.Parsing;
using Shouldly;
using Trykatch.ServiceDefaults.Observability;

namespace Trykatch.IntegrationTests;

[TestClass]
public sealed class SafeRequestLoggingTests
{
    [TestMethod]
    [DataRow(200)]
    [DataRow(401)]
    [DataRow(403)]
    public async Task CompletionUsesTheEndpointTemplateInsteadOfPrivateRequestData(int statusCode)
    {
        var logger = new RecordingLogger();
        DefaultHttpContext context = CreateContext();
        var middleware = new SafeRequestLoggingMiddleware(c =>
        {
            c.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        }, logger);

        await middleware.InvokeAsync(context);

        logger.Events.Count.ShouldBe(1);
        logger.Events[0]["RouteTemplate"].ShouldBe("/projects/{id}");
        logger.Events[0]["StatusCode"].ShouldBe(statusCode);
        logger.Events[0]["Outcome"].ShouldBe("completed");
        logger.Messages.Single().ShouldNotContain("planted-secret");
    }

    [TestMethod]
    public async Task UnmatchedRequestsNeverLogRawPathsOrQueries()
    {
        var logger = new RecordingLogger();
        DefaultHttpContext context = CreateContext();
        context.SetEndpoint(null);
        var middleware = new SafeRequestLoggingMiddleware(c =>
        {
            c.Response.StatusCode = 404;
            return Task.CompletedTask;
        }, logger);

        await middleware.InvokeAsync(context);

        logger.Events.Single()["RouteTemplate"].ShouldBe("unmatched");
        logger.Messages.Single().ShouldNotContain("planted-secret");
    }

    [TestMethod]
    public async Task FailureLogsOnceWithoutSecretsAndPreservesTheOriginalException()
    {
        var logger = new RecordingLogger();
        var failure = new InvalidOperationException("planted-secret failure");
        var middleware = new SafeRequestLoggingMiddleware(_ => Task.FromException(failure), logger);

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(() => middleware.InvokeAsync(CreateContext()));

        thrown.ShouldBeSameAs(failure);
        logger.Events.Single()["Outcome"].ShouldBe("failed");
        logger.Events.Single()["StatusCode"].ShouldBe(500);
        logger.Exceptions.Single().ShouldBeNull();
        logger.Messages.Single().ShouldNotContain("planted-secret");
    }

    [TestMethod]
    public async Task ClientAbortIsDistinctFromAnUnrelatedCancellation()
    {
        foreach (bool aborted in new[] { false, true })
        {
            var logger = new RecordingLogger();
            DefaultHttpContext context = CreateContext();
            using var cancellation = new CancellationTokenSource();
            context.RequestAborted = cancellation.Token;
            if (aborted) cancellation.Cancel();
            var failure = new OperationCanceledException("planted-secret", cancellation.Token);
            var middleware = new SafeRequestLoggingMiddleware(_ => Task.FromException(failure), logger);

            OperationCanceledException? thrown = null;
            try
            {
                await middleware.InvokeAsync(context);
            }
            catch (OperationCanceledException exception)
            {
                thrown = exception;
            }

            thrown.ShouldBeSameAs(failure);
            logger.Events.Single()["Outcome"].ShouldBe(aborted ? "client_aborted" : "failed");
            logger.Events.Single()["StatusCode"].ShouldBe(aborted ? 499 : 500);
            logger.Messages.Single().ShouldNotContain("planted-secret");
        }
    }

    [TestMethod]
    public async Task AbortedRequestThatReturnsNormallyIsNotReportedAsSuccessful()
    {
        var logger = new RecordingLogger();
        DefaultHttpContext context = CreateContext();
        context.RequestAborted = new CancellationToken(canceled: true);
        var middleware = new SafeRequestLoggingMiddleware(_ => Task.CompletedTask, logger);

        await middleware.InvokeAsync(context);

        logger.Events.Single()["Outcome"].ShouldBe("client_aborted");
        logger.Events.Single()["StatusCode"].ShouldBe(499);
    }

    [TestMethod]
    [DataRow(200, 0)]
    [DataRow(503, 1)]
    public async Task SuccessfulOperationalRequestsAreSuppressedButFailuresAreNot(int statusCode, int eventCount)
    {
        var logger = new RecordingLogger();
        DefaultHttpContext context = CreateContext();
        context.Request.Path = "/health/ready";
        var middleware = new SafeRequestLoggingMiddleware(c =>
        {
            c.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        }, logger);

        await middleware.InvokeAsync(context);

        logger.Events.Count.ShouldBe(eventCount);
    }

    [TestMethod]
    public async Task RouteAndMethodValuesAreBounded()
    {
        var logger = new RecordingLogger();
        DefaultHttpContext context = CreateContext(new string('a', 161));
        context.Request.Method = "planted-secret";
        var middleware = new SafeRequestLoggingMiddleware(_ => Task.CompletedTask, logger);

        await middleware.InvokeAsync(context);

        logger.Events.Single()["RouteTemplate"].ShouldBe("unmatched");
        logger.Events.Single()["RequestMethod"].ShouldBe("OTHER");
    }

    private static DefaultHttpContext CreateContext(string route = "/projects/{id}")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/projects/planted-secret";
        context.Request.QueryString = new QueryString("?token=planted-secret");
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(route), 0, EndpointMetadataCollection.Empty, "project"));
        return context;
    }

    [TestMethod]
    public async Task CompletionSurvivesTheActualPrivacySinkWithoutLeakingAdditionalProperties()
    {
        var logger = new RecordingLogger();
        var middleware = new SafeRequestLoggingMiddleware(_ => Task.CompletedTask, logger);
        await middleware.InvokeAsync(CreateContext());
        Dictionary<string, object?> entry = logger.Events.Single();
        string template = (string)entry["{OriginalFormat}"]!;
        var properties = entry.Where(x => x.Key != "{OriginalFormat}")
            .Select(x => new LogEventProperty(x.Key, new ScalarValue(x.Value))).ToList();
        properties.Add(new LogEventProperty("RawPath", new ScalarValue("planted-secret")));
        var original = new LogEvent(DateTimeOffset.UtcNow, Serilog.Events.LogEventLevel.Information,
            new InvalidOperationException("planted-secret"), new MessageTemplateParser().Parse(template), properties);

        LogEvent sanitized = SafeTelemetrySink.Sanitize(original);

        sanitized.MessageTemplate.Text.ShouldBe(template);
        sanitized.Properties["Outcome"].ShouldBe(new ScalarValue("completed"));
        sanitized.Properties["ResponseStarted"].ShouldBe(new ScalarValue(false));
        sanitized.Properties.ContainsKey("RawPath").ShouldBeFalse();
        sanitized.Exception.ShouldBeNull();
        sanitized.RenderMessage(CultureInfo.InvariantCulture).ShouldNotContain("planted-secret");
    }

    private sealed class RecordingLogger : ILogger<SafeRequestLoggingMiddleware>
    {
        public List<Dictionary<string, object?>> Events { get; } = [];
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Events.Add(((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(x => x.Key, x => x.Value));
            Messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
        }
    }
}
