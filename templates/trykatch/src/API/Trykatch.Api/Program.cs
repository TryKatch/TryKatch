using System.Reflection;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Validation.AspNetCore;
using Scalar.AspNetCore;
using Trykatch.Api.Development;
using Trykatch.Api.Modules;
using Trykatch.Api.OpenApi;
using Trykatch.Api.Security;
using Trykatch.Application;
using Trykatch.Identity;
using Trykatch.Infrastructure;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Trykatch.Modules.AspNetCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

bool isOpenApiGeneration = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
if (isOpenApiGeneration)
{
    builder.Configuration["ConnectionStrings:trykatchdb"] = "Host=localhost;Database=openapi;Username=openapi;Password=openapi";
}
else if (!builder.Environment.IsDevelopment())
{
    RuntimeDatabaseConnectionContract.Validate(builder.Configuration);
}

builder.AddServiceDefaults();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
ModuleCatalog moduleCatalog = builder.Services.AddModules(builder.Configuration, EnabledModules.All);
builder.Services.AddIdentity(builder.Configuration, builder.Environment.IsDevelopment(), isOpenApiGeneration);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AntiforgeryExceptionHandler>();
builder.Services.AddSingleton<IWorkspaceContextCookie, WorkspaceContextCookie>();
builder.Services.AddSingleton<IApplicationUrlResolver, ApplicationUrlResolver>();
builder.Services.ConfigureHttpJsonOptions(options => ConfigureStrictJson(options.SerializerOptions));
builder.Services.AddControllers()
    .AddJsonOptions(options => ConfigureStrictJson(options.JsonSerializerOptions))
    .ConfigureApplicationPartManager(parts =>
        parts.FeatureProviders.Add(new ModuleControllerFeatureProvider(moduleCatalog.ModuleIds)));
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<ModuleOpenApiDocumentTransformer>();
    options.AddOperationTransformer<ModuleOpenApiOperationTransformer>();
});
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "__Host-trykatch-csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
});
builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new AuthorizationPolicyBuilder(
            AuthenticationSchemes.ApplicationCookie,
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .Build();
    options.AddPolicy("PlatformAdministrator", policy => policy.RequireClaim("platform_admin", "true"));
});
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ApiAuthorizationMiddlewareResultHandler>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, PlatformPermissionAuthorizationHandler>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("account-security", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
    options.AddPolicy("account-recovery", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(15),
                QueueLimit = 0
            }));
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});
builder.Services.AddTrustedForwardedHeaders(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddDbContextCheck<PlatformDbContext>("postgres-platform", tags: ["ready"])
    .AddDbContextCheck<ApplicationDbContext>("postgres-application", tags: ["ready"])
    .AddDbContextCheck<IdentityDbContext>("postgres-identity", tags: ["ready"]);

WebApplication app = builder.Build();
if (!isOpenApiGeneration)
{
    await IdentitySeeder.SeedPlatformAdministratorAsync(app.Services, app.Configuration);
    if (app.Environment.IsDevelopment())
    {
        await DevelopmentDemoSeeder.SeedAsync(app.Services, app.Configuration);
    }
    await IdentitySeeder.SeedOpenIddictClientsAsync(app.Services, app.Configuration);
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("Referrer-Policy", "no-referrer");
    context.Response.Headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
    await next();
});
app.UseHttpsRedirection();
app.UseRouting();
app.UseServiceDefaults();
app.UseAuthentication();
app.UseRateLimiter();
app.UseMiddleware<PlatformDataTransactionMiddleware>();
app.UseMiddleware<OrganizationScopeMiddleware>();
app.UseMiddleware<OrganizationTransactionMiddleware>();
app.UseAuthorization();
app.UseAntiforgery();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference("/docs", options =>
    {
        options
            .WithTitle("Trykatch API")
            .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient)
            .ShowOperationId()
            .SortTagsAlphabetically();
        options.EnabledTargets = [ScalarTarget.CSharp, ScalarTarget.JavaScript];
        options.EnabledClients = [ScalarClient.HttpClient, ScalarClient.Fetch];
    });
}

app.MapControllers();
app.MapOrganizationModuleEndpoints();
app.MapPlatformModuleEndpoints();
app.MapDefaultEndpoints();
app.Run();

static void ConfigureStrictJson(JsonSerializerOptions options)
{
    options.RespectNullableAnnotations = true;
    options.RespectRequiredConstructorParameters = true;
}

public partial class Program;
