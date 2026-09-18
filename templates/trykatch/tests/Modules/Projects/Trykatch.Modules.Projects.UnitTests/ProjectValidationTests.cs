using Shouldly;
using Trykatch.Modules.Projects.Application;

namespace Trykatch.Modules.Projects.UnitTests;

[TestClass]
public sealed class ProjectValidationTests
{
    [TestMethod]
    public async Task InvalidProjectUpdatesAreRejected()
    {
        UpdateProjectValidator validator = new();
        (await validator.ValidateAsync(new UpdateProjectCommand(Guid.NewGuid(), "   ", null))).IsValid.ShouldBeFalse();
        (await validator.ValidateAsync(new UpdateProjectCommand(Guid.NewGuid(), new string('a', 121), null))).IsValid.ShouldBeFalse();
        (await validator.ValidateAsync(new UpdateProjectCommand(Guid.NewGuid(), "Delivery", new string('a', 2001)))).IsValid.ShouldBeFalse();
        (await validator.ValidateAsync(new UpdateProjectCommand(Guid.NewGuid(), "Delivery", null))).IsValid.ShouldBeTrue();
    }
}
