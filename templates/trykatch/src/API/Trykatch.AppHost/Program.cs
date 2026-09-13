using Trykatch.AppHost;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);
builder.AddApplicationResources();
builder.Build().Run();
