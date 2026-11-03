using System.Net;
using ClickTracker;
using ClickTracker.Admin;
using ClickTracker.Clicks;
using ClickTracker.Links;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

builder.Services.AddOpenApi();

builder.Services.Configure<LinksOptions>(builder.Configuration.GetSection("Links"));
builder.Services.Configure<ClicksOptions>(builder.Configuration.GetSection("Clicks"));
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection("Admin"));

var proxySettings = builder.Configuration.GetSection("ForwardedHeaders").Get<ProxyOptions>() ?? new ProxyOptions();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;

    if (proxySettings.KnownNetworks.Length == 0 && proxySettings.KnownProxies.Length == 0)
    {
        // Keep the framework defaults, which trust a proxy on the loopback address.
        return;
    }

    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();

    foreach (var network in proxySettings.KnownNetworks)
    {
        if (System.Net.IPNetwork.TryParse(network, out var parsed))
        {
            options.KnownIPNetworks.Add(parsed);
        }
    }

    foreach (var proxy in proxySettings.KnownProxies)
    {
        if (IPAddress.TryParse(proxy, out var parsed))
        {
            options.KnownProxies.Add(parsed);
        }
    }
});

builder.Services.AddSingleton<LinkStore>();

builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
builder.Services.AddSingleton<OpenBaoSecretReader>();
builder.Services.AddSingleton<ApiKeyProvider>();

builder.Services.AddSingleton<ClickQueue>();
builder.Services.AddSingleton<SqliteClickStore>();
builder.Services.AddSingleton<IClickStore>(sp => sp.GetRequiredService<SqliteClickStore>());
builder.Services.AddSingleton<ClickWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ClickWriter>());

var app = builder.Build();

app.UseForwardedHeaders();

var linkStore = app.Services.GetRequiredService<LinkStore>();
linkStore.Load();
linkStore.StartWatching();

var clickStore = app.Services.GetRequiredService<SqliteClickStore>();
await clickStore.InitializeAsync(CancellationToken.None);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapAdminEndpoints();

app.MapMethods("/{id}", new[] { HttpMethods.Get, HttpMethods.Head }, (
    string id,
    HttpContext context,
    LinkStore links,
    ClickQueue clicks) =>
{
    if (!links.TryGetTarget(id, out var target))
    {
        return Results.NotFound();
    }

    if (HttpMethods.IsGet(context.Request.Method))
    {
        clicks.Enqueue(ClickCapture.Capture(context, id, target));
    }

    return Results.Redirect(target, permanent: false);
});

app.Run();

/// <summary>Exposed so the integration tests can host the application.</summary>
public partial class Program
{
}
