using ClickTracker.Links;

namespace ClickTracker.Admin;

/// <summary>Rejects admin requests that do not carry a valid API key.</summary>
public sealed class ApiKeyFilter : IEndpointFilter
{
    private readonly ApiKeyProvider _provider;

    public ApiKeyFilter(ApiKeyProvider provider) => _provider = provider;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var presented = context.HttpContext.Request.Headers["X-Api-Key"].ToString();
        if (!await _provider.ValidateAsync(presented, context.HttpContext.RequestAborted))
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }
}

/// <summary>Maps the admin API for link definitions.</summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var apiKeyProvider = endpoints.ServiceProvider.GetRequiredService<ApiKeyProvider>();
        var group = endpoints.MapGroup("/admin/links").AddEndpointFilter(new ApiKeyFilter(apiKeyProvider));

        group.MapGet("", (LinkStore links) =>
        {
            var all = links.List().Select(link => new LinkDto(link.Id, link.Url)).ToArray();
            return Results.Ok(all);
        });

        group.MapPut("/{id}", (string id, UpdateLinkRequest request, LinkStore links) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Url))
            {
                return Results.BadRequest("A url is required.");
            }

            try
            {
                links.Upsert(id, request.Url);
            }
            catch (InvalidLinkException exception)
            {
                return Results.BadRequest(exception.Message);
            }

            return Results.Ok(new LinkDto(id, request.Url));
        });

        group.MapDelete("/{id}", (string id, LinkStore links) =>
            links.Delete(id) ? Results.NoContent() : Results.NotFound());

        return endpoints;
    }
}
