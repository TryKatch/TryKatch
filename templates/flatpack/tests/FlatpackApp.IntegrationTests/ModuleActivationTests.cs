using FlatpackApp.Api.Controllers;
using FlatpackApp.Api.Modules;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Shouldly;

namespace FlatpackApp.IntegrationTests;

[TestClass]
public sealed class ModuleActivationTests
{
    [TestMethod]
    public void DisabledModuleControllerIsRemovedFromMvcFeature()
    {
        ApplicationPartManager manager = new();
        manager.ApplicationParts.Add(new AssemblyPart(typeof(ProjectsController).Assembly));
        manager.FeatureProviders.Add(new ControllerFeatureProvider());
        manager.FeatureProviders.Add(new FlatpackModuleControllerFeatureProvider(new HashSet<string>(StringComparer.Ordinal)));
        ControllerFeature feature = new();

        manager.PopulateFeature(feature);

        feature.Controllers.ShouldNotContain(controller => controller.AsType() == typeof(ProjectsController));
        feature.Controllers.ShouldContain(controller => controller.AsType() == typeof(ModulesController));
    }
}
