using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Modules;
using Trykatch.Modules.Projects.Domain;
using Trykatch.Modules.Projects.Infrastructure;

namespace Trykatch.Modules.Projects.UnitTests;

[TestClass]
public sealed class ProjectAssistantFeatureTests
{
    [TestMethod]
    public async Task ReadOnlyAssistantAdaptersCheckPermissionThenReadsBeforeParsingOrQuerying()
    {
        TestData disabledData = new();
        TestPermission enabledPermission = new(true);
        TestFeatures disabledFeatures = new(false);
        using ServiceProvider disabled = Tools(enabledPermission, disabledFeatures, disabledData);
        foreach (IReadOnlyAssistantTool tool in disabled.GetServices<IReadOnlyAssistantTool>())
        {
            await Should.ThrowAsync<OrganizationFeatureUnavailableException>(() =>
                tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { unexpected = true }), CancellationToken.None));
        }
        enabledPermission.Calls.ShouldBe(2);
        disabledFeatures.Calls.ShouldBe(2);
        disabledData.Queries.ShouldBe(0);

        TestData deniedData = new();
        TestPermission deniedPermission = new(false);
        TestFeatures deniedFeatures = new(true);
        using ServiceProvider denied = Tools(deniedPermission, deniedFeatures, deniedData);
        foreach (IReadOnlyAssistantTool tool in denied.GetServices<IReadOnlyAssistantTool>())
        {
            await Should.ThrowAsync<UnauthorizedAccessException>(() =>
                tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { unexpected = true }), CancellationToken.None));
        }
        deniedPermission.Calls.ShouldBe(2);
        deniedFeatures.Calls.ShouldBe(0);
        deniedData.Queries.ShouldBe(0);
    }

    private static ServiceProvider Tools(TestPermission permission, TestFeatures features, TestData data)
    {
        ServiceCollection services = new();
        new ProjectsModule().Register(services, new ConfigurationBuilder().Build());
        services.AddSingleton<IOrganizationModuleData>(data);
        services.AddSingleton<IModulePermissionAuthorizer>(permission);
        services.AddSingleton<IOrganizationFeatureEvaluator>(features);
        return services.BuildServiceProvider();
    }

    private sealed class TestFeatures(bool enabled) : IOrganizationFeatureEvaluator
    {
        public int Calls { get; private set; }
        public Task<bool> IsEnabledAsync(string featureKey, CancellationToken ct)
        { Calls++; return Task.FromResult(enabled); }
    }

    private sealed class TestPermission(bool granted) : IModulePermissionAuthorizer
    {
        public int Calls { get; private set; }
        public Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(granted); }
    }

    private sealed class TestData : IOrganizationModuleData
    {
        public Guid OrganizationId => Guid.NewGuid();
        public Guid ActorId => Guid.NewGuid();
        public int Queries { get; private set; }
        public IQueryable<TEntity> Query<TEntity>() where TEntity : class
        { Queries++; throw new InvalidOperationException("Query should not run when policy denies access."); }
        public void Add<TEntity>(TEntity entity) where TEntity : class => throw new NotSupportedException();
        public void Remove<TEntity>(TEntity entity) where TEntity : class => throw new NotSupportedException();
        public void RecordAudit(string action, string subjectType, string subjectId, string displayName,
            IReadOnlyDictionary<string, string?>? details = null) => throw new NotSupportedException();
        public void Enqueue<TMessage>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
        public Task SaveChangesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
