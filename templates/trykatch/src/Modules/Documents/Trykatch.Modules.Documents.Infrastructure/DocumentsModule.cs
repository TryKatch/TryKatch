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
        "documents", "Documents", "1.1.0",
        "Organization-isolated file uploads backed by private S3-compatible object storage.",
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
            new("documents.manage", "Manage documents", "Upload, edit metadata, archive, restore, and request deletion of documents in the current workspace.", true, 20, ["admin", "member"])
        ],
        AssistantTools =
        [
            new("try" + "katch_list_documents", "Documents_List", "List documents in the current workspace.", AssistantToolRisk.ReadOnly, false),
            new("try" + "katch_update_document", "Documents_Update", "Update document metadata in the current workspace.", AssistantToolRisk.Mutating, true)
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
            """),
        new("202609141200_object_storage", """
            ALTER TABLE app.documents
              ADD COLUMN "FileName" character varying(255) NULL,
              ADD COLUMN "MediaType" character varying(127) NULL,
              ADD COLUMN "SizeBytes" bigint NULL,
              ADD COLUMN "Sha256" character(64) NULL,
              ADD COLUMN "ObjectKey" character varying(500) NULL;
            CREATE UNIQUE INDEX "IX_documents_OrganizationId_ObjectKey"
              ON app.documents ("OrganizationId", "ObjectKey")
              WHERE "ObjectKey" IS NOT NULL;
            """),
        new("202609151400_document_type", """
            ALTER TABLE app.documents
              ADD COLUMN "DocumentType" character varying(32) NOT NULL DEFAULT 'other',
              ADD CONSTRAINT "CK_documents_DocumentType"
                CHECK ("DocumentType" IN ('invoice', 'contract', 'certificate', 'report', 'other'));
            """)
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IReadOnlyAssistantTool, ListDocumentsAssistantTool>();
        services.AddSingleton<IApplicationModelContributor, DocumentsModelContributor>();
        services.AddSingleton<IOrganizationEndpointContributor, DocumentsEndpoints>();
        services.AddScoped<IDocumentStore, DocumentStore>();
        services.AddScoped<DocumentsUseCases>();
    }
}
