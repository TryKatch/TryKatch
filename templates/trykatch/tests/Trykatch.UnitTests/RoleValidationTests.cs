using Shouldly;
using Trykatch.Application.Organizations;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class RoleValidationTests
{
    [TestMethod]
    public async Task RoleCommandValidationRejectsInvalidFieldsAndDuplicateGrants()
    {
        SaveRoleValidator validator = new();
        (await validator.ValidateAsync(new SaveRoleCommand(null, "   ", "", []))).IsValid.ShouldBeFalse();
        (await validator.ValidateAsync(new SaveRoleCommand(null, new string('a', 81), "", []))).IsValid.ShouldBeFalse();
        (await validator.ValidateAsync(new SaveRoleCommand(null, "Operator", new string('a', 241), []))).IsValid.ShouldBeFalse();
        (await validator.ValidateAsync(new SaveRoleCommand(null, "Operator", "", ["projects.read", "projects.read"]))).IsValid.ShouldBeFalse();
        (await validator.ValidateAsync(new SaveRoleCommand(null, "Operator", "", [" "]))).IsValid.ShouldBeFalse();
        (await validator.ValidateAsync(new SaveRoleCommand(null, "Operator", "", ["projects.read"]))).IsValid.ShouldBeTrue();
    }
}
