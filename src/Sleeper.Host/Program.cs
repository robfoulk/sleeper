using Microsoft.AspNetCore.Routing;
using Sleeper.Api.Extensions;
using Sleeper.Host.Endpoints;
using Sleeper.McpServer.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddSleeperApi()
    .AddNflData();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly(typeof(KeeperTools).Assembly);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Sleeper Analytics API", Version = "v1" });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Sleeper Analytics API v1");
    c.RoutePrefix = "swagger";
});

app.MapMcp("/mcp");
app.MapSleeperApi();
app.MapGet("/health", () => Results.Ok("ok")).ExcludeFromDescription();
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

// Print a banner of all live endpoints once the host is listening.
var lifetime = app.Lifetime;
lifetime.ApplicationStarted.Register(() =>
{
    var server = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
    var addrFeature = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
    var baseUrl = addrFeature?.Addresses.FirstOrDefault() ?? "http://localhost:5757";

    var dataSource = app.Services.GetRequiredService<EndpointDataSource>();
    var routes = dataSource.Endpoints
        .OfType<RouteEndpoint>()
        .Select(e =>
        {
            var verbs = e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods;
            var verb = verbs is { Count: > 0 } ? string.Join(",", verbs) : "ANY";
            return (verb, pattern: "/" + e.RoutePattern.RawText?.TrimStart('/'));
        })
        .Where(r => !r.pattern.StartsWith("/swagger") && !r.pattern.StartsWith("/_") && r.pattern != "/")
        .OrderBy(r => r.pattern)
        .ToList();

    var line = new string('=', 70);
    Console.WriteLine();
    Console.WriteLine(line);
    Console.WriteLine($" Sleeper Host listening on {baseUrl}");
    Console.WriteLine($"   MCP (streamable HTTP):  {baseUrl}/mcp");
    Console.WriteLine($"   Swagger / OpenAPI:      {baseUrl}/swagger");
    Console.WriteLine($"   Health:                 {baseUrl}/health");
    Console.WriteLine($" REST endpoints:");
    foreach (var (verb, pattern) in routes)
    {
        Console.WriteLine($"   {verb,-6} {baseUrl}{pattern}");
    }
    Console.WriteLine(line);
    Console.WriteLine();

    app.Logger.LogInformation("Sleeper Host ready at {BaseUrl} (MCP: {Mcp}, Swagger: {Swagger})",
        baseUrl, $"{baseUrl}/mcp", $"{baseUrl}/swagger");
});

app.Run();
