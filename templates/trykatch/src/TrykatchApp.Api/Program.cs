using System.Reflection;
using System.Threading.RateLimiting;
using TrykatchApp.Api.Development;
using TrykatchApp.Api.Modules;
using TrykatchApp.Api.OpenApi;
using TrykatchApp.Api.Security;
using TrykatchApp.Application;
using TrykatchApp.Identity;
using TrykatchApp.Infrastructure;
using TrykatchApp.Infrastructure.Persistence;
using TrykatchApp.Modules;
using TrykatchApp.Modules.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Validation.AspNetCore;
using Scalar.AspNetCore;

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

builder.AddServiceDefaults();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
TrykatchModuleCatalog moduleCatalog = builder.Services.AddTrykatchModules(builder.Configuration, EnabledModules.All);
builder.Services.AddTrykatchIdentity(builder.Configuration, builder.Environment.IsDevelopment(), isOpenApiGeneration);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AntiforgeryExceptionHandler>();
builder.Services.AddSingleton<IWorkspaceContextCookie, WorkspaceContextCookie>();
builder.Services.AddSingleton<IApplicationUrlResolver, ApplicationUrlResolver>();
builder.Services.AddControllers().ConfigureApplicationPartManager(parts =>
    parts.FeatureProviders.Add(new TrykatchModuleControllerFeatureProvider(moduleCatalog.ModuleIds)));
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<TrykatchOpenApiDocumentTransformer>();
    options.AddOperationTransformer<TrykatchOpenApiOperationTransformer>();
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
            TrykatchAuthenticationSchemes.ApplicationCookie,
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .Build();
    options.AddPolicy("PlatformAdministrator", policy => policy.RequireClaim("platform_admin", "true"));
});
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, PlatformPermissionAuthorizationHandler>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
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
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});
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
app.MapTrykatchOrganizationModuleEndpoints();
app.MapTrykatchPlatformModuleEndpoints();
app.MapDefaultEndpoints();
app.Run();

public partial class Program;
