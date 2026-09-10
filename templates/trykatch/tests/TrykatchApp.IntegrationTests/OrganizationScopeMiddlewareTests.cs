using System.Security.Claims;
using TrykatchApp.Api.Security;
using TrykatchApp.Application.Organizations;
using TrykatchApp.Domain.Organizations;
using TrykatchApp.Infrastructure.Organizations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace TrykatchApp.IntegrationTests;

[TestClass]
public sealed class OrganizationScopeMiddlewareTests
{
    [TestMethod]
    public async Task ProtectedWorkspaceCookieResolvesScopedEndpoint()
    {
        Guid actorId = Guid.CreateVersion7();
        Guid organizationId = Guid.CreateVersion7();
        OrganizationAccess expected = new(organizationId, "acme", actorId, Guid.CreateVersion7(), new HashSet<string>());
        RecordingResolver resolver = new(expected);
        RecordingInitializer initializer = new();
        bool nextWasCalled = false;
        OrganizationScopeMiddleware middleware = new(_ => { nextWasCalled = true; return Task.CompletedTask; });
        DefaultHttpContext context = AuthenticatedContext(actorId, scoped: true);

        await middleware.InvokeAsync(context, new StubWorkspaceCookie(organizationId), resolver, initializer, new StubDataPlacement(organizationId));

        nextWasCalled.ShouldBeTrue();
        resolver.ResolvedOrganizationId.ShouldBe(organizationId);
        initializer.Access.ShouldBe(expected);
    }

    [TestMethod]
    public async Task ScopedEndpointWithoutWorkspaceContextIsRejected()
    {
        OrganizationScopeMiddleware middleware = new(_ => Task.CompletedTask);
        DefaultHttpContext context = AuthenticatedContext(Guid.CreateVersion7(), scoped: true);

        await middleware.InvokeAsync(context, new StubWorkspaceCookie(null), new RecordingResolver(null), new RecordingInitializer(), new StubDataPlacement(Guid.Empty));

        context.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
    }

    [TestMethod]
    public async Task UnscopedEndpointDoesNotRequireWorkspaceContext()
    {
        bool nextWasCalled = false;
        OrganizationScopeMiddleware middleware = new(_ => { nextWasCalled = true; return Task.CompletedTask; });
        DefaultHttpContext context = AuthenticatedContext(Guid.CreateVersion7(), scoped: false);

        await middleware.InvokeAsync(context, new StubWorkspaceCookie(null), new RecordingResolver(null), new RecordingInitializer(), new StubDataPlacement(Guid.Empty));

        nextWasCalled.ShouldBeTrue();
    }

    [TestMethod]
    public void OrganizationContextCannotChangeAfterRequestInitialization()
    {
        OrganizationContext context = new();
        context.Initialize(new(
            Guid.CreateVersion7(),
            "organization-a",
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            new HashSet<string>()));

        Should.Throw<InvalidOperationException>(() => context.Initialize(new(
            Guid.CreateVersion7(),
            "organization-b",
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            new HashSet<string>())));
    }

    [TestMethod]
    public async Task DedicatedRouteIsRejectedUntilThisHostCanActuallyRouteIt()
    {
        Guid actorId = Guid.CreateVersion7();
        Guid organizationId = Guid.CreateVersion7();
        OrganizationAccess access = new(organizationId, "acme", actorId, Guid.CreateVersion7(), new HashSet<string>());
        bool nextWasCalled = false;
        OrganizationScopeMiddleware middleware = new(_ => { nextWasCalled = true; return Task.CompletedTask; });
        DefaultHttpContext context = AuthenticatedContext(actorId, scoped: true);

        await middleware.InvokeAsync(
            context,
            new StubWorkspaceCookie(organizationId),
            new RecordingResolver(access),
            new RecordingInitializer(),
            new StubDataPlacement(organizationId, OrganizationDataPlacementKind.Dedicated));

        context.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        nextWasCalled.ShouldBeFalse();
    }

    private static DefaultHttpContext AuthenticatedContext(Guid actorId, bool scoped)
    {
        DefaultHttpContext context = new();
        context.RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, actorId.ToString())],
            authenticationType: "Test"));
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, scoped ? new EndpointMetadataCollection(new OrganizationScopedAttribute()) : new EndpointMetadataCollection(), "test"));
        return context;
    }

    private sealed class StubWorkspaceCookie(Guid? organizationId) : IWorkspaceContextCookie
    {
        public bool TryRead(HttpContext context, out Guid value)
        {
            value = organizationId ?? Guid.Empty;
            return organizationId.HasValue;
        }

        public void Write(HttpContext context, Guid value, bool persistent) { }
        public void Clear(HttpContext context) { }
    }

    private sealed class RecordingResolver(OrganizationAccess? access) : IOrganizationAccessResolver
    {
        public Guid? ResolvedOrganizationId { get; private set; }

        public Task<OrganizationAccess?> ResolveAsync(Guid actorId, Guid organizationId, CancellationToken cancellationToken = default)
        {
            ResolvedOrganizationId = organizationId;
            return Task.FromResult(access);
        }
    }

    private sealed class RecordingInitializer : IOrganizationContextInitializer
    {
        public OrganizationAccess? Access { get; private set; }
        public void Initialize(OrganizationAccess access) => Access = access;
    }

    private sealed class StubDataPlacement(
        Guid organizationId,
        OrganizationDataPlacementKind kind = OrganizationDataPlacementKind.Shared) : IOrganizationDataPlacement
    {
        public Task<OrganizationDataPlacementResult> ProvisionAsync(
            OrganizationDataPlacementRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new OrganizationDataPlacementResult(OrganizationProvisioningState.Ready, Route(request.OrganizationId)));

        public Task<OrganizationDataRoute> ResolveAsync(Guid requestedOrganizationId, CancellationToken cancellationToken) =>
            Task.FromResult(Route(requestedOrganizationId));

        private OrganizationDataRoute Route(Guid requestedOrganizationId) => new(
            requestedOrganizationId == Guid.Empty ? organizationId : requestedOrganizationId,
            kind,
            "postgres",
            "test",
            "test",
            "1.0.0");
    }
}
