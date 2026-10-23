using Microsoft.Extensions.Configuration;
using Shouldly;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class RuntimeDatabaseConnectionContractTests
{
    [TestMethod]
    public void ProductionValidationRequiresCoordinatorConnection()
    {
        IConfiguration configuration = Configuration(includeCoordinator: false);

        Should.Throw<InvalidOperationException>(() => RuntimeDatabaseConnectionContract.Validate(configuration));
    }

    [TestMethod]
    public void ProductionValidationRejectsDuplicateRuntimeUsers()
    {
        IConfiguration configuration = Configuration(coordinatorUser: "trykatch_outbox_worker");

        Should.Throw<InvalidOperationException>(() => RuntimeDatabaseConnectionContract.Validate(configuration));
    }

    [TestMethod]
    public void ProductionValidationRejectsUnexpectedCoordinatorUser()
    {
        IConfiguration configuration = Configuration(coordinatorUser: "unexpected_runtime_role");

        Should.Throw<InvalidOperationException>(() => RuntimeDatabaseConnectionContract.Validate(configuration));
    }

    [TestMethod]
    public void ProductionValidationAcceptsFiveExpectedDistinctRuntimeUsers()
    {
        IConfiguration configuration = Configuration();

        RuntimeDatabaseConnectionContract.Validate(configuration);
    }

    [TestMethod]
    public void CoordinatorConnectionNeverFallsBackToMigratorConnection()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:trykatchdb"] =
                    "Host=localhost;Database=trykatch;Username=trykatch_migrator;Password=secret"
            })
            .Build();

        Should.Throw<InvalidOperationException>(() =>
            RuntimeDatabaseConnectionContract.Get(configuration, RuntimeDatabaseConnectionContract.WorkCoordinator));
    }

    private static IConfiguration Configuration(
        bool includeCoordinator = true,
        string coordinatorUser = "trykatch_work_coordinator")
    {
        Dictionary<string, string?> settings = new()
        {
            ["ConnectionStrings:trykatch-organization"] = Connection("trykatch_org_runtime"),
            ["ConnectionStrings:trykatch-platform"] = Connection("trykatch_platform_runtime"),
            ["ConnectionStrings:trykatch-identity"] = Connection("trykatch_identity_runtime"),
            ["ConnectionStrings:trykatch-outbox"] = Connection("trykatch_outbox_worker")
        };
        if (includeCoordinator)
        {
            settings["ConnectionStrings:trykatch-work"] = Connection(coordinatorUser);
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static string Connection(string username) =>
        $"Host=localhost;Database=trykatch;Username={username};Password=secret";
}
