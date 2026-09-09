using TrykatchApp.Application.Identity;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace TrykatchApp.Identity;

public static class DependencyInjection
{
    public static IServiceCollection AddTrykatchIdentity(this IServiceCollection services, IConfiguration configuration, bool isDevelopment, bool isOpenApiGeneration = false)
    {
        string connectionString = configuration.GetConnectionString("trykatchdb")
            ?? throw new InvalidOperationException("Connection string 'trykatchdb' is required.");

        services.AddDbContext<IdentityDbContext>(options =>
        {
            options.UseNpgsql(connectionString, postgres => postgres.EnableRetryOnFailure());
            options.UseOpenIddict();
        });

        IDataProtectionBuilder dataProtection = services.AddDataProtection().SetApplicationName("TrykatchApp");
        if (!isOpenApiGeneration)
        {
            dataProtection.PersistKeysToDbContext<IdentityDbContext>();
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
                    string signingPath = configuration["OpenIddict:SigningCertificate:Path"]
                        ?? throw new InvalidOperationException("OpenIddict signing certificate path is required in production.");
                    string encryptionPath = configuration["OpenIddict:EncryptionCertificate:Path"]
                        ?? throw new InvalidOperationException("OpenIddict encryption certificate path is required in production.");
                    options.AddSigningCertificate(X509CertificateLoader.LoadPkcs12FromFile(
                        signingPath,
                        configuration["OpenIddict:SigningCertificate:Password"]));
                    options.AddEncryptionCertificate(X509CertificateLoader.LoadPkcs12FromFile(
                        encryptionPath,
                        configuration["OpenIddict:EncryptionCertificate:Password"]));
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
        return services;
    }

    private static Task RejectRedirect(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        return Task.CompletedTask;
    }
}
