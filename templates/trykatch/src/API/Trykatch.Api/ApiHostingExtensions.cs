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
using Trykatch.Modules.AspNetCore.Assistant;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.RateLimiting;

namespace Trykatch.Api;

internal static class ApiHostingExtensions
{
    public static bool AddApplicationApi(this WebApplicationBuilder builder)
    {
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        bool isOpenApiGeneration = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
        if (isOpenApiGeneration)
        {
            builder.Configuration["ConnectionStrings:trykatchdb"] =
                "Host=localhost;Database=openapi;Username=openapi;Password=openapi";
        }
        else if (!builder.Environment.IsDevelopment())
        {
            RuntimeDatabaseConnectionContract.Validate(builder.Configuration);
        }

        builder.AddServiceDefaults();
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(
            builder.Configuration,
            builder.Environment.IsDevelopment(),
            isOpenApiGeneration);
        ModuleCatalog moduleCatalog = builder.Services.AddModules(builder.Configuration, EnabledModules.All);
        builder.Services.AddIdentity(
            builder.Configuration,
            builder.Environment.IsDevelopment(),
            isOpenApiGeneration);
        builder.Services.AddProblemDetails();
        builder.Services.AddOptions<AtomicMutationResponseOptions>()
            .BindConfiguration(AtomicMutationResponseOptions.SectionName)
            .Validate(
                options => options.MaximumBytes is >= 1_024 and <= 8_388_608,
                "Atomic mutation response limit must be between 1 KiB and 8 MiB.")
            .ValidateOnStart();
        builder.Services.AddExceptionHandler<AntiforgeryExceptionHandler>();
        builder.Services.AddExceptionHandler<RequestBindingExceptionHandler>();
        builder.Services.AddSingleton<IWorkspaceContextCookie, WorkspaceContextCookie>();
        builder.Services.AddSingleton<IApplicationUrlResolver, ApplicationUrlResolver>();
        builder.Services.AddScoped<ModuleTransactionCompensation>();
        builder.Services.AddScoped<IModuleTransactionCompensation>(services =>
            services.GetRequiredService<ModuleTransactionCompensation>());
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
            options.AddPolicy(
                "PlatformAdministrator",
                policy => policy.RequireClaim("platform_admin", "true"));
        });
        builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ApiAuthorizationMiddlewareResultHandler>();
        builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        builder.Services.AddScoped<IAuthorizationHandler, PlatformPermissionAuthorizationHandler>();
        builder.Services.AddOptions<AssistantOptions>().BindConfiguration("Assistant")
            .Validate(AssistantProviders.IsValid,
                "Enabled assistant requires a supported provider, explicit model and valid provider-specific server settings.")
            .ValidateOnStart();
        builder.Services.AddScoped<AssistantRuntime>();
        builder.Services.AddScoped<Trykatch.Application.Organizations.IOrganizationAssistantProviderPolicy, OrganizationAssistantProviderPolicy>();
        builder.Services.AddScoped<IAssistantTenantProvider, OrganizationAssistantProvider>();
        builder.Services.AddScoped<Trykatch.Application.Organizations.IOrganizationAssistantConnectionProbe, OrganizationAssistantConnectionProbe>();
        builder.Services.AddSingleton<AssistantKnowledge>();
        builder.Services.AddScoped<AssistantConversationTokens>();
        builder.Services.TryAddSingleton(TimeProvider.System);
        // Explicit opt-in to the .NET resilience-removal API: a paid provider POST must not
        // inherit ServiceDefaults' retries/hedging. Test the protocol through a fake provider.
#pragma warning disable EXTEXP0001
        builder.Services.AddHttpClient("assistant-provider")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        builder.Services.AddScoped<Microsoft.Extensions.AI.IChatClient>(services => AssistantProviders.Create(
            services.GetRequiredService<IHttpClientFactory>().CreateClient("assistant-provider"),
            services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AssistantOptions>>()));
        builder.Services.AddSingleton<AssistantRequestLimits>();
        builder.Services.AddOptions<RateLimiterOptions>().Configure<AssistantRequestLimits>((options, limits) => options.GlobalLimiter = limits.Limiter);
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("assistant", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("account-security", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                    ?? context.Connection.RemoteIpAddress?.ToString()
                    ?? "anonymous",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(5),
                        QueueLimit = 0
                    }));
            options.AddPolicy("account-recovery", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(15),
                        QueueLimit = 0
                    }));
            options.AddPolicy("outbox-replay", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(5),
                        QueueLimit = 0
                    }));
        });
        builder.Services.AddTrustedForwardedHeaders(builder.Configuration);
        builder.Services.AddHealthChecks()
            .AddDbContextCheck<PlatformDbContext>("postgres-platform", tags: ["ready"])
            .AddDbContextCheck<ApplicationDbContext>("postgres-application", tags: ["ready"])
            .AddDbContextCheck<IdentityDbContext>("postgres-identity", tags: ["ready"]);

        return isOpenApiGeneration;
    }

    public static async Task InitializeApplicationApiAsync(
        this WebApplication app,
        bool isOpenApiGeneration)
    {
        _ = app.Services.GetRequiredService<AssistantKnowledge>();
        if (isOpenApiGeneration)
        {
            return;
        }

        if (!app.Environment.IsDevelopment())
        {
            await app.Services.ValidateIdentityKeyRingAsync();
        }

        await IdentitySeeder.SeedPlatformAdministratorAsync(app.Services, app.Configuration);
        if (app.Environment.IsDevelopment())
        {
            await DevelopmentDemoSeeder.SeedAsync(app.Services, app.Configuration);
        }

        await IdentitySeeder.SeedOpenIddictClientsAsync(app.Services, app.Configuration);
    }

    public static WebApplication UseApplicationApi(this WebApplication app)
    {
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
        return app;
    }

    private static void ConfigureStrictJson(JsonSerializerOptions options)
    {
        options.RespectNullableAnnotations = true;
        options.RespectRequiredConstructorParameters = true;
    }
}
