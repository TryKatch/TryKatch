using FlatpackApp.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace FlatpackApp.UnitTests;

[TestClass]
public sealed class FlatpackModuleMigrationPlanTests
{
    [TestMethod]
    public void BuildReturnsOnlyPendingMigrationsInModuleAndIdOrder()
    {
        DataModule projects = new("projects",
        [
            new("202609080900_initial", "CREATE TABLE app.projects_test(id uuid PRIMARY KEY);"),
            new("202609081000_index", "CREATE INDEX ix_projects_test ON app.projects_test(id);")
        ]);
        DataModule reporting = new("reporting",
        [
            new("202609081100_initial", "CREATE TABLE app.reporting_test(id uuid PRIMARY KEY);")
        ], ["projects"]);
        string appliedChecksum = FlatpackModuleMigrationPlan.ComputeChecksum(
            "CREATE TABLE app.projects_test(id uuid PRIMARY KEY);\n");

        IReadOnlyList<PendingFlatpackModuleMigration> plan = FlatpackModuleMigrationPlan.Build(
            new FlatpackModuleCatalog([reporting, projects]).Modules,
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
            new("202609080900_initial", "CREATE TABLE app.projects_test(id uuid PRIMARY KEY);")
        ]);

        Should.Throw<InvalidOperationException>(() => FlatpackModuleMigrationPlan.Build(
                [module],
                [new("projects", "202609080900_initial", new string('0', 64))]))
            .Message.ShouldContain("modified after deployment");
    }

    [TestMethod]
    public void BuildRejectsModuleOwnedTransactionControl()
    {
        DataModule module = new("projects",
        [
            new("202609080900_initial", "BEGIN; CREATE TABLE app.projects_test(id uuid PRIMARY KEY); COMMIT;")
        ]);

        Should.Throw<InvalidOperationException>(() => FlatpackModuleMigrationPlan.Build([module], []))
            .Message.ShouldContain("migrator owns the transaction");
    }

    private sealed class DataModule(
        string id,
        IReadOnlyList<FlatpackModuleMigration> migrations,
        IReadOnlyList<string>? requires = null) : IFlatpackModule, IFlatpackModuleMigrationContributor
    {
        public FlatpackModuleDescriptor Descriptor { get; } = new(
            id,
            id,
            "1.0.0",
            "Test data module.",
            requires ?? [],
            [],
            FlatpackModuleCapabilities.Data,
            []);

        public IReadOnlyList<FlatpackModuleMigration> Migrations { get; } = migrations;

        public void Register(IServiceCollection services, IConfiguration configuration)
        {
        }
    }
}
