namespace Trykatch.ModuleTool;

internal static class ApplicationCreationOutput
{
    internal static string Format(ApplicationTemplateResult result, bool verbose)
    {
        ArgumentNullException.ThrowIfNull(result);
        // Exit 73 is the .NET template engine's existing-file conflict. Its full
        // output can list hundreds of files; retain that detail behind --verbose.
        return result.ExitCode == 73 && !verbose
            ? "error: The template engine refused to overwrite existing files."
                + Environment.NewLine + "Choose a different --output directory, or review the existing files before retrying."
                + Environment.NewLine + "Use --verbose to see the full template engine diagnostics."
            : result.Output.Trim();
    }
}
