using Trykatch.Api.Controllers;
using Trykatch.Api.Modules;
using Trykatch.Modules;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Shouldly;

namespace Trykatch.IntegrationTests;

[TestClass]
public sealed class ModuleActivationTests
{
    [TestMethod]
    public void DisabledModuleControllerIsRemovedFromMvcFeature()
    {
        ApplicationPartManager manager = new();
        manager.ApplicationParts.Add(new AssemblyPart(typeof(DisabledModuleController).Assembly));
        manager.ApplicationParts.Add(new AssemblyPart(typeof(ModulesController).Assembly));
        manager.FeatureProviders.Add(new ControllerFeatureProvider());
        manager.FeatureProviders.Add(new ModuleControllerFeatureProvider(new HashSet<string>(StringComparer.Ordinal)));
        ControllerFeature feature = new();

        manager.PopulateFeature(feature);

        feature.Controllers.ShouldNotContain(controller => controller.AsType() == typeof(DisabledModuleController));
        feature.Controllers.ShouldContain(controller => controller.AsType() == typeof(ModulesController));
    }

    [ApiController]
    [Module("disabled-test-module")]
    public sealed class DisabledModuleController : ControllerBase;
}
