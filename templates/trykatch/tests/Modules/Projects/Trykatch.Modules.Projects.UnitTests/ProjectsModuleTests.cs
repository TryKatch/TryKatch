using Shouldly;
using Trykatch.Modules;
using Trykatch.Modules.Projects.Domain;
using Trykatch.Modules.Projects.Infrastructure;

namespace Trykatch.Modules.Projects.UnitTests;

[TestClass]
public sealed class ProjectsModuleTests
{
    [TestMethod]
    public void DescriptorAndMigrationBaselineRemainAligned()
    {
        ProjectsModule module = new();
        module.Descriptor.Id.ShouldBe("projects");
        module.Descriptor.DataResources.Single().EntityType.ShouldBe(typeof(Project).FullName);
        module.ShouldBeAssignableTo<IModuleMigrationContributor>();
        ((IModuleMigrationContributor)module).Migrations.ShouldContain(migration => migration.Id.EndsWith("_projects_baseline", StringComparison.Ordinal));
    }
}
