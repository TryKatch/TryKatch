using Trykatch.Modules;

namespace Trykatch.Infrastructure.Features;

internal static class FeatureAvailabilityPolicy
{
    internal static bool IsEnabled(
        ModuleFeatureDescriptor? declaration,
        bool deploymentDisabled,
        bool forceDisabled,
        bool? organizationOverride,
        bool? environmentDefault,
        bool? configuredDefault)
    {
        if (declaration is null || deploymentDisabled || forceDisabled)
        {
            return false;
        }

        return organizationOverride
            ?? environmentDefault
            ?? configuredDefault
            ?? declaration.DefaultEnabled;
    }
}
