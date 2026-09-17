using System.Text.Json;

namespace Trykatch.ModuleTool;

internal static partial class BlueprintRenderer
{
    private static readonly string[] CrudWebTestOperations = ["List", "Create", "Archive", "Restore", "RequestDeletion"];

    private static Dictionary<string, string> WebTokens(ModuleBlueprint blueprint)
    {
        string moduleCamel = char.ToLowerInvariant(blueprint.Module[0]) + blueprint.Module[1..];
        Dictionary<string, BlueprintText> labels = blueprint.Workflow.States.ToDictionary(s => s.Name, s => s.Label, StringComparer.Ordinal);
        foreach (BlueprintField item in blueprint.Fields) labels[item.Name] = item.Label;
        foreach (BlueprintAction action in blueprint.Workflow.Actions)
        {
            foreach (BlueprintGuard guard in action.Guards) AddGuardLabels(guard, labels);
            foreach (BlueprintField input in action.Inputs) labels[input.Name] = input.Label;
        }
        object[] actions = blueprint.Workflow.Actions.Select(a => (object)new
        {
            id = ScaffoldingNameRules.ToKebabCase(a.Name), label = a.Label,
            inputs = a.Inputs.Select(i => new { i.Name, i.Required, i.MinimumLength, i.MaximumLength, i.Label }).ToArray()
        }).ToArray();
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__WEB_TEST_WORKFLOW_FIELDS__"] = $"workflowState: {Quote(blueprint.Workflow.InitialState)}, decisionReason: null, canEdit: true, availableActions: [],",
            ["__WEB_TEST_API_MOCKS__"] = $"{moduleCamel}Update: (id: string, body: unknown) => transport('/api/v1/{blueprint.Resource}/' + id, {{ method: 'PUT', body: JSON.stringify(body) }}), " +
                string.Join(", ", CrudWebTestOperations
                    .Concat(blueprint.Workflow.Actions.Select(action => action.Name))
                    .Select(operation => moduleCamel + operation + ": vi.fn()")),
            ["__LABEL_PLURAL_EN__"] = Quote(blueprint.Labels.Plural.En),
            ["__LABEL_SINGULAR_EN__"] = Quote(blueprint.Labels.Singular.En),
            ["__LABEL_PLURAL__"] = JsonSerializer.Serialize(blueprint.Labels.Plural, ModuleBlueprint.JsonOptions),
            ["__BLUEPRINT_MESSAGES_EN__"] = WebMessages(blueprint, false),
            ["__BLUEPRINT_MESSAGES_FR__"] = WebMessages(blueprint, true),
            ["__WEB_ACTION_IMPORTS__"] = string.Join(", ", blueprint.Workflow.Actions.Select(a => moduleCamel + a.Name)),
            ["__WEB_ACTION_MOCKS__"] = string.Join(", ", blueprint.Workflow.Actions.Select(a => moduleCamel + a.Name + ": vi.fn()")),
            ["__WEB_ACTION_METADATA__"] = JsonSerializer.Serialize(actions, ModuleBlueprint.JsonOptions),
            ["__WEB_WORKFLOW_LABELS__"] = JsonSerializer.Serialize(labels, ModuleBlueprint.JsonOptions),
            ["__WEB_ACTION_DISPATCH__"] = string.Join("\n    ", blueprint.Workflow.Actions.Select(a =>
                $"case {Quote(ScaffoldingNameRules.ToKebabCase(a.Name))}: return {moduleCamel}{a.Name}(id, {{ expectedVersion{string.Concat(a.Inputs.Select(i => ", " + i.Name + ": values[" + Quote(i.Name) + "] ?? null"))} }});"))
        };
    }

    private static void AddGuardLabels(BlueprintGuard guard, Dictionary<string, BlueprintText> labels)
    {
        if (labels.TryGetValue(guard.Code, out BlueprintText? previous) && previous != guard.Message)
            throw new ArgumentException($"Guard code '{guard.Code}' has conflicting translations.");
        labels[guard.Code] = guard.Message;
        if (guard.Rules is not null) foreach (BlueprintGuard child in guard.Rules) AddGuardLabels(child, labels);
    }

    private static string WebMessages(ModuleBlueprint blueprint, bool french)
    {
        string singular = french ? blueprint.Labels.Singular.Fr : blueprint.Labels.Singular.En;
        string plural = french ? blueprint.Labels.Plural.Fr : blueprint.Labels.Plural.En;
        Dictionary<string, string> messages = new(StringComparer.Ordinal)
        {
            ["moduleTitle"] = plural,
            ["createRecord"] = (french ? "Créer : " : "Create ") + singular,
            ["newRecord"] = (french ? "Créer : " : "New ") + singular,
            ["editRecord"] = (french ? "Modifier : " : "Edit ") + singular,
            ["emptyTitle"] = (french ? "Aucun résultat : " : "No records: ") + plural,
            ["search"] = (french ? "Rechercher : " : "Search ") + plural,
            ["loadFailed"] = (french ? "Chargement impossible : " : "Unable to load ") + plural,
            ["loading"] = (french ? "Chargement : " : "Loading ") + plural,
            ["pageDescription"] = french ? "Gérez les enregistrements et leurs décisions de vérification." : "Manage records and their review decisions.",
            ["recordDetails"] = singular,
            ["workflowState"] = french ? "État du processus" : "Workflow state",
            ["decisionReason"] = french ? "Motif de la décision" : "Decision reason",
            ["confirmAction"] = french ? "Confirmer" : "Confirm",
            ["refreshRecord"] = french ? "Charger la version récente" : "Load latest version"
        };
        foreach (BlueprintField field in blueprint.Fields) messages["field" + Pascal(field.Name)] = french ? field.Label.Fr : field.Label.En;
        // Enum option labels retain their declared identifiers; authors may use field labels to explain their meaning.
        foreach (ModuleFieldDefinition definition in blueprint.Definitions)
            foreach (string value in definition.EnumValues) messages["field" + definition.PropertyName + value] = value;
        return string.Join(",\n    ", messages.Select(pair => Quote(pair.Key) + ": " + Quote(pair.Value)));
    }
}
