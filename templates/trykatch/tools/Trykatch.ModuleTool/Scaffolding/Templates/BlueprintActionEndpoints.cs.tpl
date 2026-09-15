using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using __ROOT_NAMESPACE__.Modules.__MODULE__.Application;
using __ROOT_NAMESPACE__.Modules.__MODULE__.Domain;

namespace __ROOT_NAMESPACE__.Modules.__MODULE__.Presentation;

__ACTION_REQUESTS__

public sealed partial class __MODULE__Endpoints
{
    private static void MapActions(RouteGroupBuilder group)
    {
        __ACTION_MAPPINGS__
    }
}

internal sealed class BlueprintFailureFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (BlueprintRuleException exception)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [exception.Field] = [exception.Message] },
                extensions: new Dictionary<string, object?> { ["code"] = exception.Code, ["field"] = exception.Field });
        }
        catch (BlueprintConflictException exception)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Record conflict", detail: exception.Message,
                extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
        }
    }
}
