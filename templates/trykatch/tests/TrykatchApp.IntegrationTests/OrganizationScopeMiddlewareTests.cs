using System.Security.Claims;
using TrykatchApp.Api.Security;
using TrykatchApp.Application.Organizations;
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

        await middleware.InvokeAsync(context, new StubWorkspaceCookie(organizationId), resolver, initializer);

        nextWasCalled.ShouldBeTrue();
        resolver.ResolvedOrganizationId.ShouldBe(organizationId);
        initializer.Access.ShouldBe(expected);
    }

    [TestMethod]
    public async Task ScopedEndpointWithoutWorkspaceContextIsRejected()
    {
        OrganizationScopeMiddleware middleware = new(_ => Task.CompletedTask);
        DefaultHttpContext context = AuthenticatedContext(Guid.CreateVersion7(), scoped: true);

        await middleware.InvokeAsync(context, new StubWorkspaceCookie(null), new RecordingResolver(null), new RecordingInitializer());

        context.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
    }

    [TestMethod]
    public async Task UnscopedEndpointDoesNotRequireWorkspaceContext()
    {
        bool nextWasCalled = false;
        OrganizationScopeMiddleware middleware = new(_ => { nextWasCalled = true; return Task.CompletedTask; });
        DefaultHttpContext context = AuthenticatedContext(Guid.CreateVersion7(), scoped: false);

        await middleware.InvokeAsync(context, new StubWorkspaceCookie(null), new RecordingResolver(null), new RecordingInitializer());

        nextWasCalled.ShouldBeTrue();
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
}
