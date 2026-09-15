using System.Globalization;

namespace Trykatch.ModuleTool;

internal static partial class BlueprintRenderer
{
    private static Dictionary<string, string> TestTokens(ModuleBlueprint blueprint)
    {
        string entity = blueprint.Entity;
        return new(StringComparer.Ordinal)
        {
            ["__FIXTURE_ARGUMENTS__"] = string.Join(", ", blueprint.Fields.Select(f => SampleValue(f, entity))),
            ["__ARCHIVED_ACTION_TESTS__"] = string.Join("\n\n", blueprint.Workflow.Actions.Select(action => $$"""
                [TestMethod]
                public void {{action.Name}}RejectsArchivedRecordsWithoutChangingState()
                {
                    {{entity}}Record record = CreateRecord();
                    record.Archive(Guid.NewGuid(), DateTimeOffset.UtcNow);
                    Guid version = record.Version;
                    {{entity}}WorkflowState state = record.WorkflowState;
                    Should.Throw<BlueprintConflictException>(() => record.{{action.Name}}(version{{string.Concat(action.Inputs.Select(i => ", " + SampleValue(i, entity)))}}, DateTimeOffset.UtcNow));
                    record.Version.ShouldBe(version);
                    record.WorkflowState.ShouldBe(state);
                }

                [TestMethod]
                public void {{action.Name}}RejectsStaleVersionsBeforeAnyMutation()
                {
                    {{entity}}Record record = CreateRecord();
                    Guid version = record.Version;
                    Should.Throw<BlueprintConflictException>(() => record.{{action.Name}}(Guid.NewGuid(){{string.Concat(action.Inputs.Select(i => ", " + SampleValue(i, entity)))}}, DateTimeOffset.UtcNow)).Code.ShouldBe("stale_version");
                    record.Version.ShouldBe(version);
                }
                """))
        };
    }

    private static string SampleValue(BlueprintField field, string entity)
    {
        ModuleFieldDefinition definition = ModuleFieldContract.Parse(field.Specification).Single();
        if (!definition.Required) return "null";
        if (definition.Kind == ModuleFieldKind.String)
        {
            int length = Math.Min(field.MaximumLength, Math.Max(12, field.MinimumLength ?? 0));
            return Quote("Sample value".PadRight(length, 'x')[..length]);
        }
        decimal value = Math.Max(field.Minimum ?? 1, 1);
        if (field.ExclusiveMinimum && field.Minimum >= value) value += definition.Kind == ModuleFieldKind.Decimal ? 0.01m : 1;
        if (field.Maximum is not null) value = Math.Min(value, field.Maximum.Value);
        return definition.Kind switch
        {
            ModuleFieldKind.Decimal => Number(value),
            ModuleFieldKind.Integer => decimal.Truncate(value).ToString(CultureInfo.InvariantCulture),
            ModuleFieldKind.Long => decimal.Truncate(value).ToString(CultureInfo.InvariantCulture) + "L",
            ModuleFieldKind.Boolean => "true",
            ModuleFieldKind.Guid => "Guid.NewGuid()",
            ModuleFieldKind.Date => "new DateOnly(2026, 1, 1)",
            ModuleFieldKind.DateTime => "DateTimeOffset.UnixEpoch",
            ModuleFieldKind.Enum => entity + definition.PropertyName + "." + definition.EnumValues[0],
            _ => throw new InvalidOperationException("Unknown fixture kind.")
        };
    }
}
