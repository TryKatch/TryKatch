using Microsoft.Extensions.Hosting;
using Trykatch.Migrator;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
return await builder.RunDatabaseMigrationsAsync();
