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

    [TestMethod]
    public void AvailabilityDeclarationsPreserveDefaultsAndRemainDistinctFromPermissions()
    {
        ModuleDescriptor descriptor = new ProjectsModule().Descriptor;

        descriptor.Features.Select(feature => feature.Key).ShouldBe(["projects.reads", "projects.writes"]);
        descriptor.Features.ShouldAllBe(feature => feature.DefaultEnabled);
        descriptor.Features.Select(feature => feature.Key).Intersect(descriptor.Permissions.Select(permission => permission.Key))
            .ShouldBeEmpty();
    }
}
