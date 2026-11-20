using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vertice.Core;

/// <summary>Indicadores del día (en vivo y contra el plan manual).</summary>
public sealed record Kpis
{
    public int Total { get; init; }
    public int Delivered { get; init; }
    public int OnTime { get; init; }
    public int Late { get; init; }
    public double OnTimePct { get; init; }
    public int AtRisk { get; init; }
    public int PredictedLate { get; init; }
    public int Unassigned { get; init; }
    public double KmDriven { get; init; }
    public double KmPlanned { get; init; }
    public double KmManual { get; init; }
    public int VansPlanned { get; init; }
    public int VansManual { get; init; }
    public int LatePlanned { get; init; }
    public int LateManual { get; init; }
    public double CostPlanned { get; init; }
    public double CostManual { get; init; }
    public double FuelSavedL { get; init; }
    public double FuelSavedMxn { get; init; }
    public double Co2SavedKg { get; init; }
    public double SavingsPct { get; init; }
}

public sealed record PlanSummary(double Km, int Vans, int LateStops, double LateMin, double Cost, double EndsAt, List<RouteDto> Routes);
public sealed record RouteDto(int VehicleId, string Code, int Stops, double Km, double Kg, double EndsAt, List<double[]> Path);
public sealed record PlanCompareDto(PlanSummary Manual, PlanSummary? Optimized, int Iterations, double Ms, List<ProgressPoint> Progress);
public sealed record ProgressPoint(int It, double Ms, double Best);

public sealed record UnitDto(int Id, string Code, string Driver, string Status, double Lat, double Lon, int Done, int Pending, double Km,
    double LoadPct, string? Next, string? NextCustomer, double? NextEta, string Risk, int Received, List<double[]> Path);
public sealed record OrderDto(int Id, string Code, string Customer, string Area, double Lat, double Lon, double Open, double Close, double Kg,
    bool Urgent, string State, int? VehicleId, double? Eta, double? DeliveredAt, double? LateMin);
public sealed record IncidentDto(int Id, string Label, double Lat, double Lon, double RadiusKm, double Until, bool Active);
public sealed record LogDto(double At, string Kind, string Text, int? VehicleId);
public sealed record ReplanDto(double At, string Reason, int Moved, List<string> Units, int RiskBefore, int RiskAfter, double Ms, int Iterations);
public sealed record DepotDto(string Name, double Lat, double Lon);

public sealed record SnapshotDto(string Phase, double Now, int PlanVersion, bool Finished, DepotDto Depot, List<UnitDto> Units, List<OrderDto> Orders,
    List<IncidentDto> Incidents, Kpis Kpis, List<LogDto> Log, ReplanDto? LastReplan);

public sealed record AnswerDto(string Question, string Answer, double At);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(SnapshotDto))]
[JsonSerializable(typeof(PlanCompareDto))]
[JsonSerializable(typeof(AnswerDto))]
[JsonSerializable(typeof(ReplanDto))]
[JsonSerializable(typeof(string[]))]
public sealed partial class VerticeJson : JsonSerializerContext;

/// <summary>
/// Fachada del motor para la interfaz (WebAssembly en el navegador o la API con SignalR):
/// arma el día, compara plan manual vs optimizado, corre la operación y responde preguntas.
/// </summary>
public sealed class Engine
{
    // camioneta de reparto: ~7.5 km/L; gasolina ~$24.5/L; 2.31 kg CO₂ por litro
    const double KmPerLiter = 7.5, PesosPerLiter = 24.5, Co2PerLiter = 2.31;

    public Problem Problem { get; private set; } = null!;
    public Solution Manual { get; private set; } = null!;
    public Solution? Optimized { get; private set; }
    public Simulation? Sim { get; private set; }
    public string Phase => Sim != null ? "live" : Optimized != null ? "ready" : "plan";
    public int Seed { get; private set; }

    PlanSummary _manual = null!;
    PlanSummary? _optimized;
    Assistant? _assistant;

    public static Engine Create(int seed = 2026, int orders = 150, int vehicles = 12)
    {
        var e = new Engine { Seed = seed };
        e.Problem = Scenario.Build(seed, orders, vehicles);
        e.Manual = ManualPlanner.Plan(e.Problem, e.Problem.Orders.Select(o => o.Id));
        e._manual = Summarize(e.Problem, e.Manual);
        return e;
    }

    public PlanCompareDto Optimize(double timeMs, Action<SolverProgress>? progress = null, int maxIterations = int.MaxValue)
    {
        var points = new List<ProgressPoint>();
        var solver = new AlnsSolver(Problem, new SolverOptions { Seed = Seed, TimeLimitMs = timeMs, MaxIterations = maxIterations, ReportEvery = 5 });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Optimized = solver.Solve(Solution.Empty(Problem, Problem.Orders.Select(o => o.Id)), p =>
        {
            points.Add(new ProgressPoint(p.Iteration, Math.Round(p.ElapsedMs), Math.Round(p.BestCost)));
            progress?.Invoke(p);
        });
        _optimized = Summarize(Problem, Optimized);
        return new PlanCompareDto(_manual, _optimized, solver.Iterations, Math.Round(sw.Elapsed.TotalMilliseconds), points);
    }

    public PlanCompareDto Compare() => new(_manual, _optimized, 0, 0, []);

    public void Start(bool guided = true, int seed = 1)
    {
        Sim = new Simulation(Problem, Optimized ?? throw new InvalidOperationException("Primero optimiza el plan."), seed);
        if (guided) Sim.UseGuidedScript();
        _assistant = new Assistant(Sim, Kpi);
    }

    public string Ask(string q) => _assistant?.Ask(q) ?? "Inicia el día para que pueda responder con datos en vivo.";

    // ── resúmenes ───────────────────────────────────────────────────────

    static PlanSummary Summarize(Problem p, Solution s)
    {
        var routes = new List<RouteDto>();
        double km = 0, lateMin = 0, cost = 0, end = 0;
        int late = 0, vans = 0;
        foreach (var r in s.Routes)
        {
            var e = Evaluator.Evaluate(p, r);
            cost += e.Cost;
            if (r.Stops.Count == 0) continue;
            vans++; km += e.Km; late += e.LateStops; lateMin += e.LateMin; end = Math.Max(end, e.End);
            var path = new List<double[]> { Ll(p.Depot.Location) };
            path.AddRange(r.Stops.Select(id => Ll(p.Orders[id].Location)));
            path.Add(Ll(p.Depot.Location));
            routes.Add(new RouteDto(r.VehicleId, p.Vehicles[r.VehicleId].Code, r.Stops.Count, Math.Round(e.Km, 1), Math.Round(e.Kg), Math.Round(e.End), path));
        }
        cost += s.Unassigned.Count * p.Weights.Unassigned;
        return new PlanSummary(Math.Round(km, 1), vans, late, Math.Round(lateMin), Math.Round(cost), Math.Round(end), routes);
    }

    public Kpis Kpi()
    {
        var m = _manual;
        var o = _optimized ?? m;
        double savedL = (m.Km - o.Km) / KmPerLiter;
        var k = new Kpis
        {
            Total = Problem.Orders.Count,
            KmPlanned = o.Km, KmManual = m.Km, VansPlanned = o.Vans, VansManual = m.Vans,
            LatePlanned = o.LateStops, LateManual = m.LateStops, CostPlanned = o.Cost, CostManual = m.Cost,
            FuelSavedL = Math.Round(savedL, 1), FuelSavedMxn = Math.Round(savedL * PesosPerLiter), Co2SavedKg = Math.Round(savedL * Co2PerLiter),
            SavingsPct = m.Cost > 0 ? Math.Round((m.Cost - o.Cost) / m.Cost * 100, 1) : 0,
        };
        if (Sim == null) return k;
        var etas = Sim.Predict();
        int lateDone = Sim.Delivered.Values.Count(d => d.LateMin > 0.5);
        return k with
        {
            Delivered = Sim.Delivered.Count,
            Late = lateDone,
            OnTime = Sim.Delivered.Count - lateDone,
            OnTimePct = Sim.Delivered.Count == 0 ? 100 : Math.Round((Sim.Delivered.Count - lateDone) * 100.0 / Sim.Delivered.Count, 1),
            AtRisk = etas.Count(e => e.Risk == Risk.EnRiesgo),
            PredictedLate = etas.Count(e => e.Risk == Risk.Tarde),
            Unassigned = Sim.Unassigned.Count,
            KmDriven = Math.Round(Sim.Units.Sum(u => u.Km - (u.Status is UnitStatus.EnRuta or UnitStatus.Regresando ? RemainingKm(u) : 0)), 1),
        };
    }

    double RemainingKm(Unit u)
    {
        double f = Math.Clamp((Sim!.Now - u.LegStart) / Math.Max(1e-6, u.LegEnd - u.LegStart), 0, 1);
        return TravelModel.RoadKm(u.From, u.To) * (1 - f);
    }

    public SnapshotDto Snapshot()
    {
        var p = Problem;
        var depot = new DepotDto(p.Depot.Name, p.Depot.Location.Lat, p.Depot.Location.Lon);
        if (Sim == null)
        {
            var plan = Optimized ?? Manual;
            var assign = plan.Routes.SelectMany(r => r.Stops.Select(o => (o, r.VehicleId))).ToDictionary(x => x.o, x => x.VehicleId);
            var ordersPlan = p.Orders.Select(o => ToDto(o, "pendiente", assign.TryGetValue(o.Id, out var v) ? v : null, null, null, null)).ToList();
            return new SnapshotDto(Phase, p.Vehicles.Min(v => v.ShiftStart) - 10, 0, false, depot, [], ordersPlan, [], Kpi(), [], null);
        }

        var s = Sim;
        var etas = s.Predict();
        var etaBy = etas.ToDictionary(e => e.OrderId);
        var units = s.Units.Select(u =>
        {
            var mine = etas.Where(e => e.VehicleId == u.Vehicle.Id).ToList();
            string risk = mine.Any(e => e.Risk == Risk.Tarde) ? "tarde" : mine.Any(e => e.Risk == Risk.EnRiesgo) ? "riesgo" : "ok";
            int? next = u.Status is UnitStatus.Averiada ? null : u.Plan.Count > 0 ? u.Plan[0] : null;
            var path = new List<double[]>();
            if (u.Status is not (UnitStatus.Averiada or UnitStatus.Terminado))
            {
                path.Add(Ll(u.Position));
                if (u.Status == UnitStatus.Regresando && u.Plan.Count > 0) path.Add(Ll(p.Depot.Location));
                path.AddRange(u.Plan.Select(id => Ll(p.Orders[id].Location)));
                path.Add(Ll(p.Depot.Location));
            }
            double cap = u.Vehicle.CapacityKg;
            double onboard = u.Plan.Sum(id => p.Orders[id].Kg);
            return new UnitDto(u.Vehicle.Id, u.Vehicle.Code, u.Vehicle.Driver, StatusText(u.Status), u.Position.Lat, u.Position.Lon, u.Done.Count, u.Plan.Count,
                Math.Round(u.Km, 1), Math.Round(Math.Min(100, onboard / cap * 100)), next is int n ? p.Orders[n].Code : null, next is int n2 ? p.Orders[n2].Customer : null,
                next is int n3 && etaBy.TryGetValue(n3, out var ne) ? Math.Round(Math.Max(ne.Arrive, p.Orders[n3].Open), 1) : null, risk, u.Received.Count, path);
        }).ToList();

        var assigned = s.Units.SelectMany(u => u.Plan.Select(o => (o, u.Vehicle.Id))).ToDictionary(x => x.o, x => x.Id);
        var orders = p.Orders.Select(o =>
        {
            if (s.Delivered.TryGetValue(o.Id, out var d))
                return ToDto(o, d.LateMin > 0.5 ? "tarde" : "entregado", d.VehicleId, null, d.Start, d.LateMin);
            if (s.Unassigned.Contains(o.Id)) return ToDto(o, "sinunidad", null, null, null, null);
            etaBy.TryGetValue(o.Id, out var e);
            string state = e.Risk == Risk.Tarde ? "atrasado" : e.Risk == Risk.EnRiesgo ? "riesgo" : "pendiente";
            return ToDto(o, state, assigned.TryGetValue(o.Id, out var v) ? v : null, Math.Round(e.Arrive, 1), null, Math.Round(e.LateMin, 1));
        }).ToList();

        var incidents = p.Travel.Incidents.Select(i => new IncidentDto(i.Id, i.Label, i.Center.Lat, i.Center.Lon, i.RadiusKm, i.Until, i.From <= s.Now && i.Until >= s.Now)).ToList();
        var log = s.Log.TakeLast(80).Select(l => new LogDto(Math.Round(l.At, 1), l.Kind.ToString(), l.Text, l.VehicleId)).ToList();
        var rp = s.LastReplan is { } r ? new ReplanDto(r.At, r.Reason, r.Moved, r.Units, r.RiskBefore, r.RiskAfter, Math.Round(r.Ms), r.Iterations) : null;
        return new SnapshotDto(Phase, Math.Round(s.Now, 2), s.PlanVersion, s.Finished, depot, units, orders, incidents, Kpi(), log, rp);
    }

    static OrderDto ToDto(Order o, string state, int? vehicle, double? eta, double? deliveredAt, double? late) =>
        new(o.Id, o.Code, o.Customer, o.Area, o.Location.Lat, o.Location.Lon, o.Open, o.Close, o.Kg, o.Urgent, state, vehicle, eta, deliveredAt, late);

    static string StatusText(UnitStatus s) => s switch
    {
        UnitStatus.EnCedis => "en CEDIS",
        UnitStatus.EnRuta => "en ruta",
        UnitStatus.Esperando => "esperando",
        UnitStatus.Entregando => "entregando",
        UnitStatus.Regresando => "regresando",
        UnitStatus.Terminado => "terminó",
        _ => "descompuesta",
    };

    static double[] Ll(GeoPoint g) => [Math.Round(g.Lon, 5), Math.Round(g.Lat, 5)];

    // ── JSON para la interfaz ───────────────────────────────────────────

    public string SnapshotJson() => JsonSerializer.Serialize(Snapshot(), VerticeJson.Default.SnapshotDto);
    public static string Json(PlanCompareDto d) => JsonSerializer.Serialize(d, VerticeJson.Default.PlanCompareDto);
    public static string Json(AnswerDto d) => JsonSerializer.Serialize(d, VerticeJson.Default.AnswerDto);
}
