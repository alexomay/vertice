using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Vertice.Core;

namespace Vertice.Api;

/// <summary>Una operación (Engine) por conexión; se libera al desconectarse.</summary>
public sealed class Sessions
{
    public const int Max = 200;
    readonly ConcurrentDictionary<string, Engine> _map = new();
    public int Count => _map.Count;

    public Engine Get(string id) => _map.TryGetValue(id, out var e) ? e : throw new HubException("Primero crea el escenario.");

    public void Set(string id, Engine e)
    {
        if (_map.Count >= Max && !_map.ContainsKey(id)) throw new HubException("Demasiadas sesiones abiertas; intenta en un momento.");
        _map[id] = e;
    }

    public void Drop(string id) => _map.TryRemove(id, out _);
}

/// <summary>
/// Mismas operaciones que el motor en WebAssembly (Interop en Vertice.Wasm), por SignalR.
/// SignalR atiende una llamada a la vez por conexión, así que cada Engine se usa en serie.
/// </summary>
public sealed class OpsHub(Sessions sessions, ILogger<OpsHub> log) : Hub
{
    Engine E => sessions.Get(Context.ConnectionId);

    public string Create(int seed, int orders, int vehicles)
    {
        if (orders is < 5 or > 400 || vehicles is < 1 or > 40) throw new HubException("Tamaño de escenario fuera de rango.");
        var e = Engine.Create(seed, orders, vehicles);
        sessions.Set(Context.ConnectionId, e);
        log.LogInformation("Escenario {Seed}: {Orders} entregas, {Vehicles} unidades", seed, orders, vehicles);
        return Engine.Json(e.Compare());
    }

    /// <summary>Optimiza en otro hilo y va mandando el avance (para la gráfica en vivo) mientras termina.</summary>
    public async Task<string> Optimize(double ms)
    {
        var engine = E;
        var caller = Clients.Caller;
        var channel = Channel.CreateUnbounded<SolverProgress>();
        var work = Task.Run(() =>
        {
            try { return engine.Optimize(Math.Clamp(ms, 100, 15000), p => { if (!p.Done) channel.Writer.TryWrite(p); }); }
            finally { channel.Writer.Complete(); }
        });
        await foreach (var p in channel.Reader.ReadAllAsync(Context.ConnectionAborted))
            await caller.SendAsync("progress", p.Iteration, p.ElapsedMs, p.BestCost);
        return Engine.Json(await work);
    }

    public void Start(bool guided) => E.Start(guided);

    public string Advance(double minutes)
    {
        E.Sim!.Advance(Math.Clamp(minutes, 0, 30));
        return E.SnapshotJson();
    }

    public string Snapshot() => E.SnapshotJson();

    public string Breakdown(int vehicleId)
    {
        var sim = E.Sim!;
        int id = vehicleId >= 0 ? vehicleId : sim.Units.Where(u => u.Status is UnitStatus.EnRuta or UnitStatus.Entregando && u.Plan.Count > 1)
            .OrderByDescending(u => u.Plan.Count).Select(u => u.Vehicle.Id).DefaultIfEmpty(-1).First();
        if (id >= 0 && id < sim.Units.Count) sim.BreakDown(id);
        return E.SnapshotJson();
    }

    public string Urgent(double? lat, double? lon)
    {
        E.Sim!.AddUrgentOrder(lat is double a && lon is double b ? new GeoPoint(a, b) : null);
        return E.SnapshotJson();
    }

    public string Traffic(double? lat, double? lon)
    {
        E.Sim!.AddIncident(lat is double a && lon is double b ? new GeoPoint(a, b) : null);
        return E.SnapshotJson();
    }

    public string Replan()
    {
        E.Sim!.Replan("solicitud del despachador");
        return E.SnapshotJson();
    }

    public void SetAutoReplan(bool on) => E.Sim!.AutoReplan = on;

    public string Ask(string question) =>
        Engine.Json(new AnswerDto(question, E.Ask(question[..Math.Min(question.Length, 300)]), E.Sim?.Now ?? 0));

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        sessions.Drop(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
