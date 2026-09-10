using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trykatch.Modules;
using Trykatch.Modules.AspNetCore;
using Trykatch.Modules.Documents.Application;
using Trykatch.Modules.Documents.Domain;
using Trykatch.Modules.Documents.Presentation;

namespace Trykatch.Modules.Documents.Infrastructure;

public sealed class DocumentsModule : IModule, IModuleMigrationContributor
{
    public string ModuleId => Descriptor.Id;

    public ModuleDescriptor Descriptor { get; } = new(
        "documents", "Documents", "1.0.0",
        "Organization-owned document records supplied by an independently packaged full-stack module.",
        ["projects"], [],
        ModuleCapabilities.Api | ModuleCapabilities.Web | ModuleCapabilities.Data
            | ModuleCapabilities.BackgroundWork | ModuleCapabilities.Assistant,
        [new("documents.list.after-table", "Renders module contributions after the documents table.", ExtensionPointKind.UiSlot, ModuleCapabilities.Web)])
    {
        DefaultDataOwnership = ModuleDataOwnership.Organization,
        DataResources =
        [
            new("documents", "app", "documents", ModuleDataOwnership.Organization,
                typeof(DocumentRecord).FullName, "documents_organization_isolation")
        ],
        Permissions =
        [
            new("documents.read", "View documents", "View documents in the current workspace.", DefaultRoles: ["admin", "member", "viewer"]),
            new("documents.manage", "Manage documents", "Create, edit, archive, restore, and request deletion of documents in the current workspace.", true, 20, ["admin", "member"])
        ],
        AssistantTools =
        [
            new("try" + "katch_list_documents", "Documents_List", "List documents in the current workspace.", AssistantToolRisk.ReadOnly, false),
            new("try" + "katch_create_document", "Documents_Create", "Create a document record in the current workspace.", AssistantToolRisk.Mutating, true),
            new("try" + "katch_update_document", "Documents_Update", "Update document content in the current workspace.", AssistantToolRisk.Mutating, true)
        ]
    };

    public IReadOnlyList<ModuleMigration> Migrations { get; } =
    [
        new("202609101300_initial", """
            CREATE TABLE app.documents
            (
                "Id" uuid PRIMARY KEY,
                "OrganizationId" uuid NOT NULL,
                "Title" character varying(200) NOT NULL,
                "Content" text NOT NULL,
                "CreatedBy" uuid NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "ArchivedAt" timestamp with time zone NULL
            );
            CREATE INDEX "IX_documents_OrganizationId_CreatedAt"
                ON app.documents ("OrganizationId", "CreatedAt" DESC);
            ALTER TABLE app.documents ENABLE ROW LEVEL SECURITY;
            ALTER TABLE app.documents FORCE ROW LEVEL SECURITY;
            CREATE POLICY documents_organization_isolation ON app.documents
              USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid)
              WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);
            """),
        new("202609102100_recoverable_lifecycle", """
            ALTER TABLE app.documents
              ADD COLUMN "UpdatedAt" timestamp with time zone NULL,
              ADD COLUMN "ArchivedBy" uuid NULL,
              ADD COLUMN "DeletedAt" timestamp with time zone NULL,
              ADD COLUMN "DeletedBy" uuid NULL,
              ADD COLUMN "DeletionReason" character varying(500) NULL;
            """)
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IApplicationModelContributor, DocumentsModelContributor>();
        services.AddSingleton<IOrganizationEndpointContributor, DocumentsEndpoints>();
        services.AddScoped<DocumentsUseCases>();
    }
}
