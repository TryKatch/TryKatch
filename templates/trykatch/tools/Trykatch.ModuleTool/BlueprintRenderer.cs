using System.Globalization;
using System.Text.Json;

namespace Trykatch.ModuleTool;

internal static partial class BlueprintRenderer
{
    public static bool Replaces(string template) => template is "Entity.cs" or "UseCases.cs" or "Endpoints.cs"
        or "Store.cs" or "ModelContributor.cs" or "Module.cs" or "IntegrationEvents.cs" or "WebIndex.tsx" or "WebMessages.ts";

    internal static string Quote(string value) => JsonSerializer.Serialize(value);
    internal static string Pascal(string value) => char.ToUpperInvariant(value[0]) + value[1..];
    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture) + "m";

    public static IReadOnlyDictionary<string, string> Render(ModuleBlueprint blueprint, string rootNamespace, string moduleId)
    {
        string entity = blueprint.Entity;
        string states = string.Join(", ", blueprint.Workflow.States.Select((s, i) => s.Name + " = " + (i + 1)));
        string editable = string.Join(" or ", blueprint.Workflow.EditableStates.Select(s => entity + "WorkflowState." + s));
        Dictionary<string, string> values = new(StringComparer.Ordinal)
        {
            ["__WORKFLOW_STATES__"] = states,
            ["__INITIAL_STATE__"] = blueprint.Workflow.InitialState,
            ["__EDITABLE_STATES__"] = editable,
            ["__DOMAIN_CONSTRAINTS__"] = string.Join("\n        ", blueprint.Fields.SelectMany(f => Constraints(f, f.Name, f.Name, entity))),
            ["__DOMAIN_ACTIONS__"] = string.Join("\n\n", blueprint.Workflow.Actions.Select(a => DomainAction(blueprint, a))),
            ["__APPLICATION_ACTIONS__"] = string.Join("\n\n", blueprint.Workflow.Actions.Select(a => ApplicationAction(blueprint, a, moduleId))),
            ["__ACTION_REGISTRATIONS__"] = string.Join("\n        ", blueprint.Workflow.Actions.Select(a => $"services.AddScoped<{a.Name}{entity}CommandHandler>();")),
            ["__AVAILABLE_ACTIONS__"] = string.Join("\n        ", blueprint.Workflow.Actions.Select(a =>
                $"if (record.Can{a.Name}() && await authorizer.HasPermissionAsync({Quote(moduleId + "." + a.Permission)}, cancellationToken)) actions.Add({Quote(ScaffoldingNameRules.ToKebabCase(a.Name))});")),
            ["__ACTION_REQUESTS__"] = string.Join("\n", blueprint.Workflow.Actions.Select(a =>
                $"[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]\npublic sealed record {entity}{a.Name}Request(Guid ExpectedVersion{string.Concat(a.Inputs.Select(i => ", string? " + Pascal(i.Name)))});")),
            ["__ACTION_MAPPINGS__"] = string.Join("\n", blueprint.Workflow.Actions.Select(a => EndpointMapping(blueprint, a, moduleId))),
            ["__ACTION_EVENTS__"] = string.Join("\n", blueprint.Workflow.Actions.Select(a =>
                $"public sealed record {entity}{a.Name}Completed(Guid Id, Guid OrganizationId, Guid ActorId, DateTimeOffset OccurredAt);")),
            ["__BLUEPRINT_PERMISSIONS__"] = string.Join("\n            ", blueprint.Workflow.Actions.Select(a => a.Permission).Distinct().Select(permission =>
                $"new({Quote(moduleId + "." + permission)}, {Quote(permission == "review" ? "Review workflow decisions" : "Submit for review")}, {Quote("Execute " + permission + " actions within the current organization.")}, true, 30, {(permission == "review" ? "[\"admin\"]" : "[\"admin\", \"member\"]")}),"))
        };
        foreach (var pair in WebTokens(blueprint)) values.Add(pair.Key, pair.Value);
        foreach (var pair in TestTokens(blueprint)) values.Add(pair.Key, pair.Value);
        return values;
    }

    private static string DomainAction(ModuleBlueprint blueprint, BlueprintAction action)
    {
        string entity = blueprint.Entity;
        string inputParameters = string.Concat(action.Inputs.Select(i => ", string? " + i.Name));
        string validations = string.Join("\n        ", action.Inputs.SelectMany(i => Constraints(i, i.Name, i.Name, entity)));
        string guards = string.Join("\n        ", action.Guards.Select(g =>
            $"if (!({Expression(blueprint, action, g)})) throw new BlueprintRuleException({Quote(g.Code)}, {Quote(g.Field ?? g.Input ?? "workflow")}, {Quote(g.Message.En)});"));
        string assignments = string.Join("\n        ", action.Assignments.Select(pair => $"{Pascal(pair.Key)} = {pair.Value}?.Trim();"));
        string available = string.Join(" && ", action.Guards.Select(g => AvailabilityExpression(blueprint, action, g))
            .Where(expression => expression is not null).Select(expression => "(" + expression + ")"));
        return $$"""
            public bool Can{{action.Name}}() => LifecycleState == {{entity}}LifecycleState.Active
                && WorkflowState == {{entity}}WorkflowState.{{action.From}}{{(available.Length > 0 ? " && " + available : "")}};

            public void {{action.Name}}(Guid expectedVersion{{inputParameters}}, DateTimeOffset now)
            {
                EnsureVersion(expectedVersion);
                if (LifecycleState != {{entity}}LifecycleState.Active || WorkflowState != {{entity}}WorkflowState.{{action.From}})
                    throw new BlueprintConflictException("invalid_transition", "This action is not allowed in the current state.");
                {{validations}}
                {{guards}}
                {{assignments}}
                WorkflowState = {{entity}}WorkflowState.{{action.To}};
                UpdatedAt = now;
                Version = Guid.NewGuid();
            }
            """;
    }

    // Unknown user input is potentially satisfiable. Keep known conjunctions, but do
    // not block a disjunction that could still be satisfied by an input branch.
    private static string? AvailabilityExpression(ModuleBlueprint blueprint, BlueprintAction action, BlueprintGuard guard)
    {
        if (guard.Op is not "all" and not "any")
            return guard.Input is null ? Expression(blueprint, action, guard) : null;
        string?[] children = guard.Rules!.Select(child => AvailabilityExpression(blueprint, action, child)).ToArray();
        if (guard.Op == "any" && children.Any(child => child is null)) return null;
        string[] known = children.OfType<string>().ToArray();
        return known.Length == 0 ? null : "(" + string.Join(guard.Op == "all" ? " && " : " || ", known) + ")";
    }

    private static string Expression(ModuleBlueprint blueprint, BlueprintAction action, BlueprintGuard guard)
    {
        if (guard.Op is "all" or "any")
            return "(" + string.Join(guard.Op == "all" ? " && " : " || ", guard.Rules!.Select(g => Expression(blueprint, action, g))) + ")";
        BlueprintField field = guard.Field is not null ? blueprint.Fields.Single(f => f.Name == guard.Field) : action.Inputs.Single(f => f.Name == guard.Input);
        ModuleFieldDefinition definition = ModuleFieldContract.Parse(field.Specification).Single();
        string operand = guard.Field is not null ? Pascal(guard.Field) : guard.Input!;
        if (guard.Op == "notEmpty") return $"!string.IsNullOrWhiteSpace({operand})";
        string literal = guard.CompareToField is not null ? Pascal(guard.CompareToField) : definition.Kind switch
        {
            ModuleFieldKind.Decimal => Number(guard.Value.GetDecimal()),
            ModuleFieldKind.Integer => guard.Value.GetInt32().ToString(CultureInfo.InvariantCulture),
            ModuleFieldKind.Long => guard.Value.GetInt64().ToString(CultureInfo.InvariantCulture) + "L",
            ModuleFieldKind.Boolean => guard.Value.GetBoolean() ? "true" : "false",
            ModuleFieldKind.Enum => blueprint.Entity + definition.PropertyName + "." + guard.Value.GetString(),
            _ => Quote(guard.Value.GetString()!)
        };
        string op = guard.Op switch { "eq" => "==", "ne" => "!=", "gt" => ">", "gte" => ">=", "lt" => "<", "lte" => "<=", _ => throw new InvalidOperationException("Unvalidated guard operator.") };
        return $"{operand} {op} {literal}";
    }

    private static IEnumerable<string> Constraints(BlueprintField field, string value, string path, string entity)
    {
        ModuleFieldDefinition definition = ModuleFieldContract.Parse(field.Specification).Single();
        string Failure(string code, string message) => $"throw new BlueprintRuleException({Quote(code)}, {Quote(path)}, {Quote(message)});";
        if (definition.Kind == ModuleFieldKind.String)
        {
            if (field.Required) yield return $"if (string.IsNullOrWhiteSpace({value})) {Failure("required", field.Label.En + " is required.")}";
            yield return $"if ({value}?.Trim().Length > {field.MaximumLength}) {Failure("max_length", field.Label.En + " is too long.")}";
            if (field.MinimumLength is > 0)
                yield return $"if ({value} is not null && {value}.Trim().Length < {field.MinimumLength}) {Failure("min_length", field.Label.En + " is too short.")}";
        }
        if (definition.Kind == ModuleFieldKind.Enum)
        {
            string condition = definition.Required ? $"!Enum.IsDefined({value})" : $"{value}.HasValue && !Enum.IsDefined({value}.Value)";
            yield return $"if ({condition}) {Failure("invalid_enum", "Choose a declared value.")}";
        }
        if (definition.Kind == ModuleFieldKind.Decimal)
        {
            string actual = definition.Required ? value : value + ".Value";
            string condition = $"({actual} <= -10000000000000000m || {actual} >= 10000000000000000m || {actual} != decimal.Round({actual}, 2))";
            yield return $"if ({(definition.Required ? "" : value + ".HasValue && ")}{condition}) {Failure("decimal_precision", "Use at most 16 integer digits and 2 fractional digits.")}";
        }
        if (field.Minimum is not null)
            yield return $"if ({value} {(field.ExclusiveMinimum ? "<=" : "<")} {Number(field.Minimum.Value)}) {Failure("minimum", field.Label.En + " is below the allowed minimum.")}";
        if (field.Maximum is not null)
            yield return $"if ({value} > {Number(field.Maximum.Value)}) {Failure("maximum", field.Label.En + " exceeds the allowed maximum.")}";
    }

    private static string ApplicationAction(ModuleBlueprint blueprint, BlueprintAction action, string moduleId)
    {
        string entity = blueprint.Entity;
        string parameters = string.Concat(action.Inputs.Select(i => ", string? " + i.Name));
        string arguments = string.Concat(action.Inputs.Select(i => ", " + i.Name));
        return $$"""
            public sealed class {{action.Name}}{{entity}}CommandHandler(
                I{{entity}}Store store, IOrganizationModuleData context, IModulePermissionAuthorizer authorizer,
                TimeProvider timeProvider, {{entity}}ReadModel reader)
            {
              public async Task<{{entity}}OperationResult<{{entity}}Dto>> HandleAsync(Guid id, Guid expectedVersion{{parameters}}, CancellationToken cancellationToken)
              {
                if (!await authorizer.HasPermissionAsync({{Quote(moduleId + "." + action.Permission)}}, cancellationToken)) return Forbidden<{{entity}}Dto>();
                {{entity}}Record? record = await store.FindAsync(id, false, cancellationToken);
                if (record is null) return NotFound<{{entity}}Dto>();
                DateTimeOffset occurredAt = timeProvider.GetUtcNow();
                record.{{action.Name}}(expectedVersion{{arguments}}, occurredAt);
                RecordChange(context, record, {{Quote(ScaffoldingNameRules.ToKebabCase(action.Name))}}, new {{entity}}{{action.Name}}Completed(record.Id, record.OrganizationId, context.ActorId, occurredAt));
                await store.SaveChangesAsync(cancellationToken);
                return {{entity}}Operation.Success(await reader.MapAsync(record, cancellationToken));
              }
            }
            """;
    }

    private static string EndpointMapping(ModuleBlueprint blueprint, BlueprintAction action, string moduleId)
    {
        string entity = blueprint.Entity;
        string inputs = string.Concat(action.Inputs.Select(i => ", request." + Pascal(i.Name)));
        return $$"""
                group.MapPost("/{id:guid}/actions/{{ScaffoldingNameRules.ToKebabCase(action.Name)}}", async Task<Results<Ok<{{entity}}Dto>, ForbidHttpResult, NotFound>> (
                    Guid id, {{entity}}{{action.Name}}Request request, {{action.Name}}{{entity}}CommandHandler handler, CancellationToken cancellationToken) =>
                {
                    {{entity}}OperationResult<{{entity}}Dto> result = await handler.HandleAsync(id, request.ExpectedVersion{{inputs}}, cancellationToken);
                    if (result.IsSuccess) return TypedResults.Ok(result.Value!);
                    return result.Code == "forbidden" ? TypedResults.Forbid() : TypedResults.NotFound();
                }).RequireAuthorization({{Quote("permission:" + moduleId + "." + action.Permission)}})
                  .WithMetadata(new RequireAntiforgeryTokenAttribute(true))
                  .WithName({{Quote(blueprint.Module + "_" + action.Name)}}).WithTags({{Quote(blueprint.Module)}})
                  .ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
            """;
    }
}
