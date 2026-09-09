using TrykatchApp.Domain.Projects;
using TrykatchApp.Infrastructure.Persistence;
using TrykatchApp.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace TrykatchApp.UnitTests;

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
        using ApplicationDbContext context = new(options, [new ProjectsModelContributor()]);

        context.Model.FindEntityType(typeof(Project)).ShouldNotBeNull();
    }
}
