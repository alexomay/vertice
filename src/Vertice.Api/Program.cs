using Vertice.Api;
using Vertice.Core;

// Vértice en modo servidor: el mismo motor que corre en el navegador, expuesto por un hub de SignalR
// (una operación por conexión) y una API REST para planear sin interfaz.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 64 * 1024);
builder.Services.AddSingleton<Sessions>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, VerticeJson.Default));
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<OpsHub>("/hub/ops");

var api = app.MapGroup("/api");
api.MapGet("/health", (Sessions s) => Results.Ok(new { status = "ok", sessions = s.Count, version = typeof(Engine).Assembly.GetName().Version?.ToString() }));

// Planea un día sin interfaz: devuelve el plan manual, el optimizado y la curva de mejora
api.MapPost("/plan", (PlanRequest r) =>
{
    if (r.Orders is < 5 or > 400 || r.Vehicles is < 1 or > 40 || r.TimeMs is < 100 or > 15000)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["orders 5–400, vehicles 1–40, timeMs 100–15000"] });
    var engine = Engine.Create(r.Seed, r.Orders, r.Vehicles);
    return Results.Ok(engine.Optimize(r.TimeMs));
});

app.MapFallbackToFile("index.html");
app.Run();

public sealed record PlanRequest(int Seed = 2026, int Orders = 150, int Vehicles = 12, double TimeMs = 3000);

public partial class Program;
