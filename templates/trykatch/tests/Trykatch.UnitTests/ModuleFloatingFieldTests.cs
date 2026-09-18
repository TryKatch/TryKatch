using Shouldly;
using Trykatch.ModuleTool;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ModuleFloatingFieldTests
{
    [TestMethod]
    public void TextLikeFieldsUseSharedFloatingControlsWithoutNestedLabels()
    {
        string form = ModuleFieldRenderer.Render("Invoice", ModuleFieldContract.Parse(
            "title:string:required:max(200),notes:string:optional:max(2000),amount:decimal:required,count:int:required,sequence:long:required,dueDate:date:required,issuedAt:datetime:required,reference:guid:required"))["__WEB_FORM_FIELDS__"];

        form.Split("<FloatingInput").Length.ShouldBe(8);
        form.ShouldContain("<FloatingTextarea label={t('fieldNotes')}");
        form.ShouldNotContain("<label>");
        form.ShouldContain("type=\"date\"");
        form.ShouldContain("type=\"datetime-local\"");
        form.ShouldContain("maxLength={2000}");
    }

    [TestMethod]
    public void BooleanAndEnumFieldsRetainVisibleNativeLabels()
    {
        string form = ModuleFieldRenderer.Render("Invoice", ModuleFieldContract.Parse(
            "enabled:bool:required,approved:bool:optional,stage:enum(Draft,Sent):required"))["__WEB_FORM_FIELDS__"];

        form.ShouldContain("<input type=\"checkbox\"");
        form.ShouldContain("<select");
        form.ShouldContain("<label>{t('fieldStage')}");
        form.ShouldNotContain("FloatingInput");
    }
}
