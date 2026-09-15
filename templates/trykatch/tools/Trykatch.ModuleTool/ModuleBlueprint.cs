using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trykatch.ModuleTool;

/// <summary>A validated authoring contract. No blueprint content is executed.</summary>
internal sealed record ModuleBlueprint
{
    public required int SchemaVersion { get; init; }
    public required string Module { get; init; }
    public required string Entity { get; init; }
    public required string Resource { get; init; }
    public required string Ownership { get; init; }
    public required BlueprintLabels Labels { get; init; }
    public required BlueprintField[] Fields { get; init; }
    public required BlueprintWorkflow Workflow { get; init; }

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        MaxDepth = 24
    };

    public static ModuleBlueprint Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length > 262144) throw new ArgumentException("Blueprint must not exceed 256 KiB.");
        using JsonDocument document = JsonDocument.Parse(stream, new() { MaxDepth = 24 });
        RejectDuplicateProperties(document.RootElement, "$");
        ModuleBlueprint blueprint = document.Deserialize<ModuleBlueprint>(JsonOptions)
            ?? throw new ArgumentException("$: blueprint is empty.");
        blueprint.Validate();
        return blueprint;
    }

    [JsonIgnore]
    public string FieldSpecification => string.Join(',', Fields.Select(item => item.Specification));
    [JsonIgnore]
    public IReadOnlyList<ModuleFieldDefinition> Definitions => ModuleFieldContract.Parse(FieldSpecification);

    public void Validate()
    {
        Require(SchemaVersion == 1, "schemaVersion", "supported version is 1.");
        _ = ModuleName.Parse(Module);
        _ = EntityName.Parse(Entity);
        _ = ResourceName.Parse(Resource);
        Require(Ownership == "organization", "ownership", "must be organization.");
        Require(Labels is not null, "labels", "is required.");
        Labels!.Validate("labels");
        Require(Fields is { Length: > 0 and <= 24 }, "fields", "declare 1–24 fields.");
        foreach (BlueprintField field in Fields!)
        {
            Require(field is not null, "fields", "null fields are not supported.");
            field!.Validate("fields." + field.Name);
        }
        IReadOnlyList<ModuleFieldDefinition> definitions = Definitions;
        string[] reserved = ["version", "expectedVersion", "workflowState", "availableActions", "workflow", "validateBusinessFields", "touch", "ensureEditable", "ensureVersion", "decisionReason", "canEdit", "actionRecord", "selectedAction", "actionValues", "actionDefinition", "transition", "closeAction", "openAction", "refreshEditor", "refreshAction", "locale", "workflowActions", "workflowText", "workflowError", "isStaleConflict"];
        foreach (ModuleFieldDefinition field in definitions)
            Require(!reserved.Contains(field.Name, StringComparer.OrdinalIgnoreCase), "fields." + field.Name, "is reserved by the workflow contract.");
        Require(Workflow is not null, "workflow", "is required.");
        BlueprintWorkflow workflow = Workflow!;
        Require(workflow.States is { Length: > 0 and <= 16 }, "workflow.states", "declare 1–16 states.");
        HashSet<string> states = new(StringComparer.OrdinalIgnoreCase);
        foreach (BlueprintState state in workflow.States!)
        {
            Require(state is not null, "workflow.states", "null states are not supported.");
            Require(EntityName.Parse(state!.Name).Value == state.Name, "workflow.states", "state names cannot contain surrounding whitespace.");
            Require(states.Add(state.Name), "workflow.states", "state names must be unique.");
            Require(state.Label is not null, "workflow.states." + state.Name, "label is required.");
            state.Label!.Validate("workflow.states." + state.Name);
        }
        Require(workflow.States.Any(s => s.Name == workflow.InitialState), "workflow.initialState", "must reference a declared state with exact casing.");
        Require(workflow.EditableStates is { Length: > 0 }, "workflow.editableStates", "declare editable states.");
        Require(workflow.EditableStates!.Distinct(StringComparer.Ordinal).Count() == workflow.EditableStates.Length,
            "workflow.editableStates", "must not contain duplicates.");
        foreach (string state in workflow.EditableStates)
            Require(workflow.States.Any(s => s.Name == state), "workflow.editableStates", "must reference declared states.");
        Require(workflow.Actions is { Length: > 0 and <= 24 }, "workflow.actions", "declare 1–24 actions.");
        HashSet<string> actionNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (BlueprintAction action in workflow.Actions!)
        {
            Require(action is not null, "workflow.actions", "null actions are not supported.");
            string at = "workflow.actions." + action!.Name;
            Require(EntityName.Parse(action.Name).Value == action.Name, at, "action names cannot contain surrounding whitespace.");
            Require(!new[] { "Create", "Update", "Archive", "Restore", "RequestDeletion", "Touch", "EnsureVersion", "EnsureEditable", "ValidateBusinessFields", "ToString", "Equals", "GetHashCode", "GetType", "CanEdit" }.Contains(action.Name, StringComparer.OrdinalIgnoreCase)
                && !new[] { "Id", "OrganizationId", "CreatedBy", "CreatedAt", "UpdatedAt", "ArchivedAt", "ArchivedBy", "DeletedAt", "DeletedBy", "DeletionReason", "LifecycleState", "WorkflowState", "Version", "DecisionReason", "MemberwiseClone", "Finalize", Entity + "Record" }.Contains(action.Name, StringComparer.OrdinalIgnoreCase)
                && !definitions.Any(f => f.PropertyName.Equals(action.Name, StringComparison.OrdinalIgnoreCase)), at, "action name collides with a generated member.");
            Require(actionNames.Add(action.Name), at, "action names must be unique.");
            Require(workflow.States.Any(s => s.Name == action.From) && workflow.States.Any(s => s.Name == action.To) && action.From != action.To,
                at, "from/to must name different declared states.");
            Require(action.Permission is "submit" or "review", at + ".permission", "must be submit or review in schema v1.");
            Require(action.Label is not null, at + ".label", "is required.");
            action.Label!.Validate(at + ".label");
            Require(action.Inputs is not null && action.Assignments is not null && action.Guards is not null, at, "inputs, assignments and guards cannot be null.");
            Require(action.Inputs!.Length <= 8 && action.Guards!.Length <= 16, at, "at most 8 inputs and 16 guards are supported.");
            foreach (BlueprintField input in action.Inputs)
            {
                Require(input is not null, at + ".inputs", "cannot contain null.");
                input!.Validate(at + ".inputs." + input.Name);
                Require(input.Kind == "string", at + ".inputs." + input.Name, "schema v1 action inputs support strings.");
                Require(input.Name is not "expectedVersion" and not "now" and not "actorId" and not "organizationId" and not "id" and not "cancellationToken" and not "occurredAt" and not "store" and not "authorizer" and not "context" and not "reader" and not "timeProvider", at + ".inputs", "reserved input name.");
            }
            Require(action.Inputs.Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == action.Inputs.Length, at + ".inputs", "names must be unique.");
            foreach ((string target, string input) in action.Assignments!)
            {
                Require(target == "decisionReason", at + ".assignments", "schema v1 assigns action input only to decisionReason.");
                Require(action.Inputs.Any(i => i.Name == input && i.Kind == "string" && i.MaximumLength <= 500), at + ".assignments", "reference a declared string input bounded to 500 characters.");
            }
            foreach (BlueprintGuard guard in action.Guards!)
                ValidateGuard(guard, action, at + ".guards", 0);
        }
        HashSet<string> reachable = [workflow.InitialState];
        Dictionary<string, BlueprintText> translations = workflow.States.ToDictionary(s => s.Name, s => s.Label, StringComparer.Ordinal);
        void AddTranslation(string key, BlueprintText text)
        {
            Require(!translations.TryGetValue(key, out BlueprintText? previous) || previous == text,
                "labels." + key, "the same translation key must have identical English and French text.");
            translations[key] = text;
        }
        void AddGuardTranslations(BlueprintGuard guard)
        {
            AddTranslation(guard.Code, guard.Message);
            if (guard.Rules is not null) foreach (BlueprintGuard child in guard.Rules) AddGuardTranslations(child);
        }
        foreach (BlueprintField field in Fields) AddTranslation(field.Name, field.Label);
        foreach (BlueprintAction action in workflow.Actions)
        {
            foreach (BlueprintField input in action.Inputs) AddTranslation(input.Name, input.Label);
            foreach (BlueprintGuard guard in action.Guards) AddGuardTranslations(guard);
            Require(!actionNames.Contains("Can" + action.Name), "workflow.actions", "action names cannot collide with generated availability methods.");
            Require(!definitions.Any(f => f.PropertyName == "Can" + action.Name), "fields", "field names cannot collide with generated availability methods.");
        }
        while (true)
        {
            int before = reachable.Count;
            foreach (BlueprintAction action in workflow.Actions)
                if (reachable.Contains(action.From)) reachable.Add(action.To);
            if (before == reachable.Count) break;
        }
        Require(reachable.Count == workflow.States.Length, "workflow.states", "every state must be reachable from initialState.");
    }

    private void ValidateGuard(BlueprintGuard guard, BlueprintAction action, string path, int depth)
    {
        Require(guard is not null && depth < 8, path, "guard nesting must be less than 8.");
        Require(guard!.Code is { Length: > 0 and <= 64 } && guard.Code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'), path + ".code", "use a stable alphanumeric/underscore error code.");
        Require(guard.Message is not null, path + ".message", "is required.");
        guard.Message!.Validate(path + ".message");
        if (guard.Op is "all" or "any")
        {
            Require(guard.Rules is { Length: > 0 and <= 16 } && guard.Field is null && guard.Input is null && guard.CompareToField is null && guard.Value.ValueKind == JsonValueKind.Undefined,
                path, "all/any require only a nonempty rules array.");
            foreach (BlueprintGuard child in guard.Rules!) ValidateGuard(child, action, path + ".rules", depth + 1);
            return;
        }
        Require(guard.Rules is null && (guard.Field is null) != (guard.Input is null), path, "specify exactly one field or input.");
        BlueprintField? field = guard.Field is not null ? Fields.SingleOrDefault(f => f.Name == guard.Field) : action.Inputs.SingleOrDefault(f => f.Name == guard.Input);
        Require(field is not null, path, "unknown field/input reference.");
        ModuleFieldDefinition definition = ModuleFieldContract.Parse(field!.Specification).Single();
        Require(guard.Op is "eq" or "ne" or "gt" or "gte" or "lt" or "lte" or "notEmpty", path + ".op", "unsupported operator.");
        if (guard.Op == "notEmpty")
        {
            Require(definition.Kind == ModuleFieldKind.String && guard.CompareToField is null && guard.Value.ValueKind == JsonValueKind.Undefined, path, "notEmpty requires a string and no value.");
            return;
        }
        bool number = definition.Kind is ModuleFieldKind.Integer or ModuleFieldKind.Long or ModuleFieldKind.Decimal;
        Require(guard.Op is "eq" or "ne" || number, path, "ordered comparisons require a numeric field.");
        if (guard.CompareToField is not null)
        {
            BlueprintField? other = Fields.SingleOrDefault(f => f.Name == guard.CompareToField);
            Require(other is not null && other.Kind == field.Kind && guard.Value.ValueKind == JsonValueKind.Undefined,
                path + ".compareToField", "reference a field with the same kind and omit value.");
            Require(number || definition.Kind is ModuleFieldKind.String or ModuleFieldKind.Boolean,
                path, "field comparisons support numeric, string and boolean fields.");
            return;
        }
        Require(number ? guard.Value.ValueKind == JsonValueKind.Number && guard.Value.TryGetDecimal(out _)
            : definition.Kind == ModuleFieldKind.Boolean ? guard.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
            : definition.Kind is ModuleFieldKind.String or ModuleFieldKind.Enum && guard.Value.ValueKind == JsonValueKind.String,
            path + ".value", "literal type does not match the referenced field; supported comparisons use numeric, boolean, string or enum fields.");
        if (definition.Kind == ModuleFieldKind.Enum)
            Require(definition.EnumValues.Contains(guard.Value.GetString()!, StringComparer.Ordinal), path + ".value", "unknown enum value.");
        if (definition.Kind == ModuleFieldKind.Integer)
            Require(guard.Value.TryGetInt32(out _), path + ".value", "must be an int32 literal.");
        if (definition.Kind == ModuleFieldKind.Long)
            Require(guard.Value.TryGetInt64(out _), path + ".value", "must be an int64 literal.");
    }

    private static void RejectDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                Require(names.Add(property.Name), path + "." + property.Name, "duplicate property.");
                RejectDuplicateProperties(property.Value, path + "." + property.Name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in element.EnumerateArray()) RejectDuplicateProperties(child, path + "[]");
    }

    internal static void Require(bool condition, string path, string message)
    {
        if (!condition) throw new ArgumentException($"$.{path}: {message}");
    }
}

internal sealed record BlueprintText(string En, string Fr)
{
    public void Validate(string path)
    {
        foreach (string? text in new[] { En, Fr })
            ModuleBlueprint.Require(!string.IsNullOrWhiteSpace(text) && text.Length <= 500 && !text.Any(char.IsControl), path, "English and French single-line text (1–500 characters) is required.");
    }
}

internal sealed record BlueprintLabels(BlueprintText Singular, BlueprintText Plural)
{
    public void Validate(string path)
    {
        ModuleBlueprint.Require(Singular is not null && Plural is not null, path, "singular and plural labels are required.");
        Singular!.Validate(path + ".singular");
        Plural!.Validate(path + ".plural");
    }
}

internal sealed record BlueprintField
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public bool Required { get; init; } = true;
    public int MaximumLength { get; init; } = 200;
    public int? MinimumLength { get; init; }
    public decimal? Minimum { get; init; }
    public bool ExclusiveMinimum { get; init; }
    public decimal? Maximum { get; init; }
    public required BlueprintText Label { get; init; }
    internal string Specification => $"{Name}:{Kind}:{(Required ? "required" : "optional")}" + (Kind == "string" ? $":max({MaximumLength})" : "");

    public void Validate(string path)
    {
        ModuleFieldDefinition definition = ModuleFieldContract.Parse(Specification).Single();
        ModuleBlueprint.Require(Name == definition.Name && Kind == (definition.Kind == ModuleFieldKind.Enum
            ? "enum(" + string.Join(',', definition.EnumValues) + ")"
            : definition.Kind switch { ModuleFieldKind.Integer => "int", ModuleFieldKind.Boolean => "bool", ModuleFieldKind.DateTime => "datetime", _ => definition.Kind.ToString().ToLowerInvariant() }),
            path, "use canonical field names and kinds without surrounding whitespace or extra modifiers.");
        ModuleBlueprint.Require(Label is not null, path + ".label", "is required.");
        Label!.Validate(path + ".label");
        ModuleBlueprint.Require(MinimumLength is null || Kind == "string" && MinimumLength >= 0 && MinimumLength <= MaximumLength, path, "minimumLength must fit the string maximumLength.");
        ModuleBlueprint.Require((Minimum is null && Maximum is null) || Kind is "decimal" or "int" or "long", path, "numeric bounds require a numeric field.");
        ModuleBlueprint.Require(Minimum is null || Maximum is null || Minimum < Maximum, path, "minimum must be less than maximum.");
        ModuleBlueprint.Require(!ExclusiveMinimum || Minimum is not null, path, "exclusiveMinimum requires minimum.");
        decimal lower = Kind switch { "int" => int.MinValue, "long" => long.MinValue, _ => -9999999999999999.99m };
        decimal upper = Kind switch { "int" => int.MaxValue, "long" => long.MaxValue, _ => 9999999999999999.99m };
        foreach (decimal bound in new[] { Minimum, Maximum }.OfType<decimal>())
            ModuleBlueprint.Require(bound >= lower && bound <= upper && bound == decimal.Round(bound, Kind == "decimal" ? 2 : 0),
                path, "numeric bounds must fit the field type and its storage precision.");
        ModuleBlueprint.Require(!ExclusiveMinimum || Minimum < upper, path, "exclusive minimum leaves no representable value.");
    }
}

internal sealed record BlueprintState(string Name, BlueprintText Label);
internal sealed record BlueprintWorkflow
{
    public required string InitialState { get; init; }
    public required BlueprintState[] States { get; init; }
    public required string[] EditableStates { get; init; }
    public required BlueprintAction[] Actions { get; init; }
}
internal sealed record BlueprintAction
{
    public required string Name { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public required string Permission { get; init; }
    public required BlueprintText Label { get; init; }
    public BlueprintField[] Inputs { get; init; } = [];
    public Dictionary<string, string> Assignments { get; init; } = [];
    public BlueprintGuard[] Guards { get; init; } = [];
}
internal sealed record BlueprintGuard
{
    public required string Op { get; init; }
    public string? Field { get; init; }
    public string? Input { get; init; }
    public string? CompareToField { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Value { get; init; }
    public BlueprintGuard[]? Rules { get; init; }
    public required string Code { get; init; }
    public required BlueprintText Message { get; init; }
}
