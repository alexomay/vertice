using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using Vertice.Core;

// El worker de la interfaz llama a estas funciones; todo viaja como JSON.
Console.WriteLine("Vértice: motor listo");

[System.Runtime.Versioning.SupportedOSPlatform("browser")]
public static partial class Interop
{
    static Engine? _engine;
    static Engine E => _engine ?? throw new InvalidOperationException("Primero crea el escenario.");

    [JSExport]
    public static string Create(int seed, int orders, int vehicles)
    {
        _engine = Engine.Create(seed, orders, vehicles);
        return Engine.Json(_engine.Compare());
    }

    [JSExport]
    public static string Optimize(double ms)
    {
        var result = E.Optimize(ms, p => { if (!p.Done) Progress(p.Iteration, p.ElapsedMs, p.BestCost); });
        return Engine.Json(result);
    }

    [JSExport]
    public static void Start(bool guided) => E.Start(guided);

    [JSExport]
    public static string Advance(double minutes)
    {
        E.Sim!.Advance(minutes);
        return E.SnapshotJson();
    }

    [JSExport]
    public static string Snapshot() => E.SnapshotJson();

    [JSExport]
    public static string Breakdown(int vehicleId)
    {
        var sim = E.Sim!;
        int id = vehicleId >= 0 ? vehicleId : sim.Units.Where(u => u.Status is UnitStatus.EnRuta or UnitStatus.Entregando && u.Plan.Count > 1)
            .OrderByDescending(u => u.Plan.Count).Select(u => u.Vehicle.Id).DefaultIfEmpty(-1).First();
        if (id >= 0) sim.BreakDown(id);
        return E.SnapshotJson();
    }

    [JSExport]
    public static string Urgent(double lat, double lon)
    {
        E.Sim!.AddUrgentOrder(double.IsNaN(lat) ? null : new GeoPoint(lat, lon));
        return E.SnapshotJson();
    }

    [JSExport]
    public static string Traffic(double lat, double lon)
    {
        E.Sim!.AddIncident(double.IsNaN(lat) ? null : new GeoPoint(lat, lon));
        return E.SnapshotJson();
    }

    [JSExport]
    public static string Replan()
    {
        E.Sim!.Replan("solicitud del despachador");
        return E.SnapshotJson();
    }

    [JSExport]
    public static void SetAutoReplan(bool on) => E.Sim!.AutoReplan = on;

    [JSExport]
    public static string Ask(string question) =>
        Engine.Json(new AnswerDto(question, E.Ask(question), E.Sim?.Now ?? 0));

    [JSImport("progress", "vertice")]
    internal static partial void Progress(int iteration, double ms, double best);
}
