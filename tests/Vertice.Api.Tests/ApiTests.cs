using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace Vertice.Api.Tests;

public class ApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_responds()
    {
        var res = await factory.CreateClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("ok", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Plan_endpoint_returns_manual_and_optimized()
    {
        var res = await factory.CreateClient().PostAsJsonAsync("/api/plan", new { seed = 7, orders = 40, vehicles = 5, timeMs = 400 });
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var manual = doc.RootElement.GetProperty("manual");
        var opt = doc.RootElement.GetProperty("optimized");
        Assert.True(opt.GetProperty("cost").GetDouble() < manual.GetProperty("cost").GetDouble());
        Assert.Equal(40, opt.GetProperty("routes").EnumerateArray().Sum(r => r.GetProperty("stops").GetInt32()));
    }

    [Fact]
    public async Task Plan_endpoint_rejects_absurd_sizes()
    {
        var res = await factory.CreateClient().PostAsJsonAsync("/api/plan", new { orders = 5000 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Hub_runs_a_live_operation()
    {
        var server = factory.Server;
        var conn = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hub/ops"), o => o.HttpMessageHandlerFactory = _ => server.CreateHandler())
            .Build();
        int progress = 0;
        conn.On<int, double, double>("progress", (_, _, _) => Interlocked.Increment(ref progress));
        await conn.StartAsync();

        await conn.InvokeAsync<string>("Create", 3, 50, 6);
        await conn.InvokeAsync<string>("Optimize", 500.0);
        await conn.InvokeAsync("Start", false);
        string json = "";
        for (int i = 0; i < 6; i++) json = await conn.InvokeAsync<string>("Advance", 30.0);   // la API avanza máx. 30 min por llamada
        var snap = JsonDocument.Parse(json).RootElement;
        Assert.Equal("live", snap.GetProperty("phase").GetString());
        Assert.True(snap.GetProperty("kpis").GetProperty("delivered").GetInt32() > 0);

        snap = JsonDocument.Parse(await conn.InvokeAsync<string>("Breakdown", -1)).RootElement;
        Assert.Contains(snap.GetProperty("units").EnumerateArray(), u => u.GetProperty("status").GetString() == "descompuesta");
        Assert.Equal(0, snap.GetProperty("kpis").GetProperty("unassigned").GetInt32());

        var answer = JsonDocument.Parse(await conn.InvokeAsync<string>("Ask", "¿Cuánto llevamos ahorrado?")).RootElement;
        Assert.Contains("km menos", answer.GetProperty("answer").GetString());
        for (int i = 0; i < 20 && Volatile.Read(ref progress) == 0; i++) await Task.Delay(100);
        Assert.True(progress > 0, "el optimizador debe reportar avance por SignalR");
        await conn.DisposeAsync();
    }

    [Fact]
    public async Task Hub_requires_a_scenario_first()
    {
        var server = factory.Server;
        var conn = new HubConnectionBuilder().WithUrl(new Uri(server.BaseAddress, "hub/ops"), o => o.HttpMessageHandlerFactory = _ => server.CreateHandler()).Build();
        await conn.StartAsync();
        var ex = await Assert.ThrowsAsync<HubException>(() => conn.InvokeAsync<string>("Snapshot"));
        Assert.Contains("Primero crea el escenario", ex.Message);
        await conn.DisposeAsync();
    }
}
