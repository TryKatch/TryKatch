using Trykatch.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ModuleMigrationPlanTests
{
    [TestMethod]
    public void BuildReturnsOnlyPendingMigrationsInModuleAndIdOrder()
    {
        DataModule projects = new("projects",
        [
            new("202609080900_initial", "CREATE TABLE infrastructure.projects_test(id uuid PRIMARY KEY);"),
            new("202609081000_index", "CREATE INDEX ix_projects_test ON infrastructure.projects_test(id);")
        ]);
        DataModule reporting = new("reporting",
        [
            new("202609081100_initial", "CREATE TABLE infrastructure.reporting_test(id uuid PRIMARY KEY);")
        ], ["projects"]);
        string appliedChecksum = ModuleMigrationPlan.ComputeChecksum(
            "CREATE TABLE infrastructure.projects_test(id uuid PRIMARY KEY);\n");

        IReadOnlyList<PendingModuleMigration> plan = ModuleMigrationPlan.Build(
            new ModuleCatalog([reporting, projects]).Modules,
            [new("projects", "202609080900_initial", appliedChecksum)]);

        plan.Select(item => $"{item.ModuleId}/{item.MigrationId}").ShouldBe(
        [
            "projects/202609081000_index",
            "reporting/202609081100_initial"
        ]);
    }

    [TestMethod]
    public void BuildRejectsMutationOfAnAppliedMigration()
    {
        DataModule module = new("projects",
        [
            new("202609080900_initial", "CREATE TABLE infrastructure.projects_test(id uuid PRIMARY KEY);")
        ]);

        Should.Throw<InvalidOperationException>(() => ModuleMigrationPlan.Build(
                [module],
                [new("projects", "202609080900_initial", new string('0', 64))]))
            .Message.ShouldContain("modified after deployment");
    }

    [TestMethod]
    public void BuildRejectsModuleOwnedTransactionControl()
    {
        DataModule module = new("projects",
        [
            new("202609080900_initial", "BEGIN; CREATE TABLE infrastructure.projects_test(id uuid PRIMARY KEY); COMMIT;")
        ]);

        Should.Throw<InvalidOperationException>(() => ModuleMigrationPlan.Build([module], []))
            .Message.ShouldContain("migrator owns the transaction");
    }

    [TestMethod]
    public void BuildAllowsTransactionKeywordsInsideProceduralBodiesAndComments()
    {
        DataModule module = new("projects",
        [
            new("202609080900_initial", """
                -- BEGIN is documentation, not transaction control.
                CREATE TABLE infrastructure.projects_test(id uuid PRIMARY KEY);
                DO $policy$
                BEGIN
                  PERFORM 1;
                END
                $policy$;
                SELECT 'COMMIT is data';
                """)
        ]);

        ModuleMigrationPlan.Build([module], []).Count.ShouldBe(1);
    }

    [TestMethod]
    public void BuildRejectsUndeclaredQuotedRelations()
    {
        DataModule module = new("projects",
        [
            new("202609080900_initial", """
                CREATE TABLE infrastructure.projects_test(id uuid PRIMARY KEY);
                CREATE TABLE "identity"."UndeclaredQuoted"(id uuid PRIMARY KEY);
                """)
        ]);

        Should.Throw<InvalidOperationException>(() => ModuleMigrationPlan.Build([module], []))
            .Message.ShouldContain("identity.UndeclaredQuoted");
    }

    private sealed class DataModule(
        string id,
        IReadOnlyList<ModuleMigration> migrations,
        IReadOnlyList<string>? requires = null) : IModule, IModuleMigrationContributor
    {
        public ModuleDescriptor Descriptor { get; } = new(
            id,
            id,
            "1.0.0",
            "Test data module.",
            requires ?? [],
            [],
            ModuleCapabilities.Data,
            [])
        {
            DefaultDataOwnership = ModuleDataOwnership.Infrastructure,
            DataResources =
            [
                new($"{id}-test", "infrastructure", $"{id}_test", ModuleDataOwnership.Infrastructure,
                    AccessRule: ModuleDataAccessRule.HostOnly)
            ]
        };

        public IReadOnlyList<ModuleMigration> Migrations { get; } = migrations;

        public void Register(IServiceCollection services, IConfiguration configuration)
        {
        }
    }
}
