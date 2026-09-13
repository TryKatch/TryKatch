using Trykatch.Api;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
bool isOpenApiGeneration = builder.AddApplicationApi();

WebApplication app = builder.Build();
await app.InitializeApplicationApiAsync(isOpenApiGeneration);
app.UseApplicationApi();
app.Run();

public partial class Program;
