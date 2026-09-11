using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using Trykatch.Application.Identity;

namespace Trykatch.Identity;

public static class DependencyInjection
{
    public static IServiceCollection AddIdentity(this IServiceCollection services, IConfiguration configuration, bool isDevelopment, bool isOpenApiGeneration = false)
    {
        string connectionString = configuration.GetConnectionString("trykatch-identity")
            ?? configuration.GetConnectionString("trykatchdb")
            ?? throw new InvalidOperationException("Connection string 'trykatch-identity' is required.");

        services.AddDbContext<IdentityDbContext>(options =>
        {
            options.UseNpgsql(connectionString, postgres => postgres.EnableRetryOnFailure());
            options.UseOpenIddict();
        });

        IDataProtectionBuilder dataProtection = services.AddDataProtection().SetApplicationName("Trykatch");
        IdentityCertificates? certificates = null;
        if (!isOpenApiGeneration)
        {
            dataProtection.PersistKeysToDbContext<IdentityDbContext>();
        }
        if (!isDevelopment && !isOpenApiGeneration)
        {
            services.AddOptions<IdentityDataProtectionOptions>()
                .Bind(configuration.GetSection("DataProtection"))
                .Validate(options => !string.IsNullOrWhiteSpace(options.Certificate.Path), "DataProtection:Certificate:Path is required outside Development.")
                .ValidateOnStart();
            services.AddOptions<OpenIddictCertificateOptions>().Bind(configuration.GetSection("OpenIddict"));
            // Validate and load before any seeding or network-facing host startup.
            certificates = IdentityCertificates.Load(configuration);
            services.AddSingleton(_ => certificates);
            services.AddSingleton<IdentityKeyRingValidation>();
            services.AddHostedService(provider => provider.GetRequiredService<IdentityKeyRingValidation>());
            services.AddSingleton<ProductionDataProtectionXmlRepository>();
            services.AddSingleton(provider => new CertificateXmlEncryptor(certificates.Active, provider.GetRequiredService<ILoggerFactory>()));
            services.AddOptions<KeyManagementOptions>()
                .Configure<ProductionDataProtectionXmlRepository, CertificateXmlEncryptor>((options, repository, encryptor) =>
                {
                    options.XmlRepository = repository;
                    options.XmlEncryptor = encryptor;
                }).ValidateOnStart();
            // Validation runs after every Configure/PostConfigure registration, including
            // registrations added later by a module. Ordering cannot silently replace this boundary.
            services.AddSingleton<IValidateOptions<KeyManagementOptions>, ProductionKeyManagementPolicy>();
            dataProtection.UnprotectKeysWithAnyCertificate([certificates.Active, .. certificates.Retired]);
        }

        services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
        {
            bool useLocalDemoPolicy = isDevelopment
                && configuration.GetValue<bool>("DevelopmentDemo:Enabled");
            options.SignIn.RequireConfirmedEmail = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.Password.RequiredLength = useLocalDemoPolicy ? 8 : 12;
            options.Password.RequireNonAlphanumeric = true;
            options.User.RequireUniqueEmail = true;
        })
        .AddEntityFrameworkStores<IdentityDbContext>()
        .AddClaimsPrincipalFactory<PlatformClaimsPrincipalFactory>()
        .AddDefaultTokenProviders();
        services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromHours(24));

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "__Host-trykatch";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.SlidingExpiration = true;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            // AddIdentity wires its security-stamp validator into this event instance.
            // Mutate only the redirect handlers so role, password, and MFA changes
            // continue to invalidate existing application cookies.
            options.Events.OnRedirectToLogin = context => RejectRedirect(context, StatusCodes.Status401Unauthorized);
            options.Events.OnRedirectToAccessDenied = context => RejectRedirect(context, StatusCodes.Status403Forbidden);
            options.Events.OnSigningIn = context =>
            {
                // Ticket properties survive sliding renewal; an independent sign-in
                // receives a fresh ID. Legacy cookies must sign in again for step-up.
                context.Properties.Items.TryAdd(AccountSecuritySession.PropertyName, Guid.NewGuid().ToString("N"));
                return Task.CompletedTask;
            };
        });
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(5));

        OpenIddictBuilder openIddict = services.AddOpenIddict()
            .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<IdentityDbContext>())
            .AddServer(options =>
            {
                options.SetAuthorizationEndpointUris("/connect/authorize")
                    .SetTokenEndpointUris("/connect/token")
                    .SetEndSessionEndpointUris("/connect/logout");
                options.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange();
                options.AllowClientCredentialsFlow();
                options.RegisterScopes(OpenIddictConstants.Scopes.OpenId, OpenIddictConstants.Scopes.Profile, "trykatch-api");
                options.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough();

                if (isDevelopment || isOpenApiGeneration)
                {
                    options.AddDevelopmentEncryptionCertificate().AddDevelopmentSigningCertificate();
                }
                else
                {
                    options.AddSigningCertificate(certificates!.Signing);
                    options.AddEncryptionCertificate(certificates.Encryption);
                }
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
            });

        _ = openIddict;
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<IPlatformAccessDirectory, PlatformAccessDirectory>();
        services.AddScoped<IPlatformAuthorityReader, PlatformAuthorityReader>();
        services.AddScoped<PlatformManagementAuthorization>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IAccountSecurity, AccountSecurityService>();
        return services;
    }

    private static Task RejectRedirect(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        return Task.CompletedTask;
    }

    public static Task ValidateIdentityKeyRingAsync(this IServiceProvider services, CancellationToken cancellationToken = default) =>
        services.GetRequiredService<IdentityKeyRingValidation>().StartAsync(cancellationToken);
}
