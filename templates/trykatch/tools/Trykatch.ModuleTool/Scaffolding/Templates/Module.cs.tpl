using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using __ROOT_NAMESPACE__.Modules;
using __ROOT_NAMESPACE__.Modules.AspNetCore;
using __ROOT_NAMESPACE__.Modules.__MODULE__.Application;
using __ROOT_NAMESPACE__.Modules.__MODULE__.Domain;
using __ROOT_NAMESPACE__.Modules.__MODULE__.Presentation;

namespace __ROOT_NAMESPACE__.Modules.__MODULE__.Infrastructure;

public sealed class __MODULE__Module : IModule, IModuleMigrationContributor
{
    public string ModuleId => Descriptor.Id;

    public ModuleDescriptor Descriptor { get; } = new(
        "__MODULE_ID__", "__MODULE__", "1.0.0", "__DESCRIPTION__", [], [],
        __MODULE_CAPABILITIES__, [])
    {
        Publisher = "__PUBLISHER__",
        DefaultDataOwnership = ModuleDataOwnership.Organization,
        DataResources =
        [
            new("__RESOURCE__", "app", "__RESOURCE__", ModuleDataOwnership.Organization,
                typeof(__ENTITY__Record).FullName, "__RESOURCE___organization_isolation")
        ],
        Permissions =
        [
            new("__MODULE_ID__.read", "View __MODULE__", "View __MODULE__ records in the current workspace.", DefaultRoles: ["admin", "member", "viewer"]),
            new("__MODULE_ID__.manage", "Manage __MODULE__", "Create, edit, archive, restore, and request deletion of __MODULE__ records.", true, 20, ["admin", "member"])
        ]
    };

    public IReadOnlyList<ModuleMigration> Migrations { get; } =
    [
        new("202601010000_initial", """
            CREATE TABLE app.__RESOURCE__
            (
                "Id" uuid PRIMARY KEY,
                "OrganizationId" uuid NOT NULL,
                "Name" character varying(200) NOT NULL,
                "Description" character varying(2000) NULL,
                "CreatedBy" uuid NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NULL,
                "ArchivedAt" timestamp with time zone NULL,
                "ArchivedBy" uuid NULL,
                "DeletedAt" timestamp with time zone NULL,
                "DeletedBy" uuid NULL,
                "DeletionReason" character varying(500) NULL
            );
            CREATE INDEX "IX___RESOURCE___OrganizationId_CreatedAt"
                ON app.__RESOURCE__ ("OrganizationId", "CreatedAt" DESC);
            ALTER TABLE app.__RESOURCE__ ENABLE ROW LEVEL SECURITY;
            ALTER TABLE app.__RESOURCE__ FORCE ROW LEVEL SECURITY;
            CREATE POLICY __RESOURCE___organization_isolation ON app.__RESOURCE__
              USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid)
              WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);
            """)
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IApplicationModelContributor, __MODULE__ModelContributor>();
        services.AddSingleton<IOrganizationEndpointContributor, __MODULE__Endpoints>();
        services.AddScoped<I__ENTITY__Store, __ENTITY__Store>();
        services.AddScoped<__MODULE__UseCases>();
    }
}
