using Trykatch.Infrastructure.Persistence;
using Trykatch.Infrastructure.Modules;
using Trykatch.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ApplicationModelContributionTests
{
    [TestMethod]
    public void DisabledModuleDoesNotContributeItsEntityModel()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=module_test;Username=test;Password=test")
            .Options;
        using ApplicationDbContext context = new(options, []);

        context.Model.FindEntityType(typeof(Project)).ShouldBeNull();
    }

    [TestMethod]
    public void EnabledModuleContributesItsEntityModel()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=module_test;Username=test;Password=test")
            .Options;
        ModuleCatalog catalog = new([new ProjectsModule()]);
        using ApplicationDbContext context = new(options, [new ProjectsModelContributor()], moduleCatalog: catalog);

        context.Model.FindEntityType(typeof(Project)).ShouldNotBeNull();
    }

    [TestMethod]
    public void DescriptorChangesDoNotReuseAPreviouslyValidatedModel()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=module_test;Username=test;Password=test")
            .Options;
        ProjectsModule projects = new();
        using (ApplicationDbContext valid = new(options, [new ProjectsModelContributor()], moduleCatalog: new([projects])))
            valid.Model.FindEntityType(typeof(Project)).ShouldNotBeNull();

        ModuleDescriptor invalidDescriptor = projects.Descriptor with
        {
            DataResources =
            [
                projects.Descriptor.DataResources.Single() with
                {
                    Table = "renamed_projects"
                }
            ]
        };
        using ApplicationDbContext invalid = new(
            options,
            [new ProjectsModelContributor()],
            moduleCatalog: new([new DescriptorOnlyModule(invalidDescriptor)]));

        Should.Throw<InvalidOperationException>(() => _ = invalid.Model);
    }

    private sealed class DescriptorOnlyModule(ModuleDescriptor descriptor) : IModule
    {
        public ModuleDescriptor Descriptor { get; } = descriptor;

        public void Register(IServiceCollection services, IConfiguration configuration)
        {
        }
    }
}
