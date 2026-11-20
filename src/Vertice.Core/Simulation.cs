using System.Diagnostics;

namespace Vertice.Core;

public enum UnitStatus { EnCedis, EnRuta, Esperando, Entregando, Regresando, Terminado, Averiada }

public enum EventKind { Salida, Entrega, EntregaTarde, Averia, Urgente, Trafico, Reoptimizacion, Regreso, Info }

public sealed record LogEntry(double At, EventKind Kind, string Text, int? VehicleId = null, int? OrderId = null);

/// <summary>Un tramo recorrido: lo que se esperaba contra lo que tardó de verdad (para explicar retrasos).</summary>
public sealed record Leg(int VehicleId, int? ToOrder, double Start, double PlannedMin, double ActualMin, double IncidentFactor, double Km);

public sealed record Delivery(int OrderId, int VehicleId, double Arrive, double Start, double LateMin);

/// <summary>Estado en vivo de una unidad.</summary>
public sealed class Unit
{
    public required Vehicle Vehicle { get; init; }
    public UnitStatus Status { get; set; } = UnitStatus.EnCedis;
    /// <summary>Entregas pendientes en orden; la primera es a la que va (o la que atiende).</summary>
    public List<int> Plan { get; } = [];
    public List<int> Done { get; } = [];
    public GeoPoint From { get; set; }
    public GeoPoint To { get; set; }
    public int? Target { get; set; }            // entrega destino; null = CEDIS
    public double LegStart { get; set; }
    public double LegEnd { get; set; }
    public double BusyUntil { get; set; }        // fin de espera o de entrega
    public double Km { get; set; }
    public double KgDelivered { get; set; }
    public int Legs { get; set; }
    public double? BrokenAt { get; set; }
    public GeoPoint Position { get; set; }
    /// <summary>Entregas que le llegaron por re-optimización (de otra unidad o pedidos urgentes).</summary>
    public List<(double At, int OrderId, string Why)> Received { get; } = [];
}

/// <summary>Predicción de una parada pendiente: a qué hora llega y si va a tiempo.</summary>
public readonly record struct Eta(int OrderId, int VehicleId, double Arrive, double LateMin, Risk Risk);

public enum Risk { ATiempo, EnRiesgo, Tarde }

/// <summary>Evento programado del "día guiado" de la demo (lo que suele pasar en una operación real).</summary>
public sealed record ScriptedEvent(double At, string Kind);

/// <summary>
/// La operación del día: mueve las unidades minuto a minuto (con tráfico real variable), registra entregas,
/// predice retrasos y re-optimiza en vivo cuando algo cambia (unidad descompuesta, pedido urgente, choque).
/// </summary>
public sealed class Simulation
{
    public Problem Problem { get; }
    public double Now { get; private set; }
    public List<Unit> Units { get; }
    public Dictionary<int, Delivery> Delivered { get; } = [];
    public List<int> Unassigned { get; } = [];
    public List<LogEntry> Log { get; } = [];
    public List<Leg> LegsLog { get; } = [];
    /// <summary>Hora de llegada que tenía cada entrega en el plan de la mañana (para comparar).</summary>
    public Dictionary<int, double> PlannedArrive { get; } = [];
    public int PlanVersion { get; private set; }
    public List<ScriptedEvent> Script { get; } = [];
    public bool AutoReplan { get; set; } = true;
    public double ReplanBudgetMs { get; set; } = 900;
    /// <summary>Tope de iteraciones al re-optimizar (en pruebas hace el resultado reproducible sin depender del CPU).</summary>
    public int ReplanMaxIterations { get; set; } = int.MaxValue;
    public ReplanReport? LastReplan { get; private set; }

    readonly Random _rng;
    int _incidentSeq;

    public Simulation(Problem problem, Solution plan, int seed = 1)
    {
        Problem = problem;
        _rng = new Random(seed);
        Units = problem.Vehicles.Select(v => new Unit { Vehicle = v, Position = problem.Depot.Location, From = problem.Depot.Location, To = problem.Depot.Location }).ToList();
        foreach (var r in plan.Routes)
        {
            Units[r.VehicleId].Plan.AddRange(r.Stops);
            var sched = new List<StopTime>();
            Evaluator.Evaluate(problem, r, sched);
            foreach (var st in sched) PlannedArrive[st.OrderId] = st.Arrive;
        }
        Unassigned.AddRange(plan.Unassigned);
        Now = problem.Vehicles.Min(v => v.ShiftStart) - 10;
        PlanVersion = 1;
        Add(EventKind.Info, $"Plan del día listo: {plan.Routes.Count(r => r.Stops.Count > 0)} unidades, {plan.Routes.Sum(r => r.Stops.Count)} entregas.");
    }

    public bool Finished => Now > Problem.Vehicles.Min(v => v.ShiftStart) && Unassigned.Count == 0 &&
        Units.All(u => u.Status is UnitStatus.Terminado or UnitStatus.Averiada || (u.Status == UnitStatus.EnCedis && u.Plan.Count == 0));

    /// <summary>Un día con lo que normalmente pasa: choque en hora pico, pedidos urgentes y una unidad que se descompone.</summary>
    public void UseGuidedScript()
    {
        Script.Clear();
        Script.AddRange([
            new(9 * 60 + 40, "trafico"), new(10 * 60 + 50, "urgente"), new(11 * 60 + 35, "averia"),
            new(12 * 60 + 40, "urgente"), new(14 * 60 + 10, "trafico"), new(15 * 60 + 5, "urgente"),
        ]);
    }

    // ── avance del reloj ────────────────────────────────────────────────

    public void Advance(double minutes)
    {
        double target = Now + minutes;
        while (Now < target - 1e-9)
        {
            double step = Math.Min(1, target - Now);   // de minuto en minuto: los eventos quedan en su hora exacta
            double next = Now + step;
            foreach (var ev in Script.Where(e => e.At > Now && e.At <= next).ToList())
            {
                Script.Remove(ev);
                Now = ev.At;
                RunScripted(ev.Kind);
            }
            foreach (var u in Units) Tick(u, next);
            Now = next;
        }
        foreach (var u in Units) u.Position = PositionOf(u, Now);
    }

    void RunScripted(string kind)
    {
        switch (kind)
        {
            case "trafico": AddIncident(); break;
            case "urgente": AddUrgentOrder(); break;
            case "averia":
                var victim = Units.Where(u => u.Status is UnitStatus.EnRuta or UnitStatus.Entregando && u.Plan.Count >= 3)
                    .OrderByDescending(u => u.Plan.Count).FirstOrDefault();
                if (victim != null) BreakDown(victim.Vehicle.Id);
                break;
        }
    }

    void Tick(Unit u, double until)
    {
        // procesa todos los cambios de estado que caen antes de "until"
        for (int guard = 0; guard < 50; guard++)
        {
            switch (u.Status)
            {
                case UnitStatus.EnCedis:
                case UnitStatus.Terminado:
                {
                    double earliest = Math.Max(u.Vehicle.ShiftStart, u.Status == UnitStatus.Terminado ? Math.Max(u.LegEnd, Now) : 0);
                    // sale justo a tiempo para su primer cliente (igual que en el plan)
                    double depart = u.Plan.Count == 0 ? earliest
                        : Math.Max(Now, Evaluator.DepartTime(Problem, new Route { VehicleId = u.Vehicle.Id, StartTime = earliest, CapacityKg = u.Vehicle.CapacityKg }, [u.Plan[0]]));
                    if (u.Plan.Count == 0 || depart > until) return;
                    StartLeg(u, Problem.Depot.Location, u.Plan[0], depart);
                    Add(EventKind.Salida, $"{u.Vehicle.Code} sale del CEDIS con {u.Plan.Count} entregas.", u.Vehicle.Id, at: depart);
                    continue;
                }
                case UnitStatus.EnRuta:
                {
                    if (u.LegEnd > until) return;
                    var o = Problem.Orders[u.Target!.Value];
                    if (u.LegEnd < o.Open) { u.Status = UnitStatus.Esperando; u.BusyUntil = o.Open; }
                    else { u.Status = UnitStatus.Entregando; u.BusyUntil = u.LegEnd + o.ServiceMin; }
                    _arrive[u.Vehicle.Id] = u.LegEnd;
                    continue;
                }
                case UnitStatus.Esperando:
                {
                    if (u.BusyUntil > until) return;
                    var o = Problem.Orders[u.Target!.Value];
                    u.Status = UnitStatus.Entregando;
                    u.BusyUntil += o.ServiceMin;
                    continue;
                }
                case UnitStatus.Entregando:
                {
                    if (u.BusyUntil > until) return;
                    var o = Problem.Orders[u.Target!.Value];
                    double start = u.BusyUntil - o.ServiceMin;
                    double late = Math.Max(0, start - o.Close);
                    Delivered[o.Id] = new Delivery(o.Id, u.Vehicle.Id, _arrive.GetValueOrDefault(u.Vehicle.Id, start), start, late);
                    u.Done.Add(o.Id);
                    u.Plan.Remove(o.Id);
                    u.KgDelivered += o.Kg;
                    if (late > 0.5) Add(EventKind.EntregaTarde, $"{u.Vehicle.Code} entregó {o.Code} ({o.Customer}) {Math.Round(late)} min tarde.", u.Vehicle.Id, o.Id, u.BusyUntil);
                    else Add(EventKind.Entrega, $"{u.Vehicle.Code} entregó {o.Code} · {o.Customer}.", u.Vehicle.Id, o.Id, u.BusyUntil);
                    double t = u.BusyUntil;
                    if (u.Plan.Count > 0) StartLeg(u, o.Location, u.Plan[0], t);
                    else StartLeg(u, o.Location, null, t);
                    continue;
                }
                case UnitStatus.Regresando:
                {
                    if (u.LegEnd > until) return;
                    u.Position = Problem.Depot.Location;
                    if (u.Plan.Count > 0)
                    {
                        // le asignaron más entregas mientras regresaba: recarga y vuelve a salir
                        Add(EventKind.Salida, $"{u.Vehicle.Code} recarga en el CEDIS y sale con {u.Plan.Count} entregas más.", u.Vehicle.Id, at: u.LegEnd);
                        StartLeg(u, Problem.Depot.Location, u.Plan[0], u.LegEnd + 10);
                        continue;
                    }
                    u.Status = UnitStatus.Terminado;
                    Add(EventKind.Regreso, $"{u.Vehicle.Code} terminó su ruta: {u.Done.Count} entregas, {u.Km:F0} km.", u.Vehicle.Id, at: u.LegEnd);
                    return;
                }
                default:
                    return;
            }
        }
    }

    readonly Dictionary<int, double> _arrive = [];

    /// <summary>Arranca un tramo. El tiempo real varía contra lo esperado (semáforos, clima, suerte) y por incidentes.</summary>
    void StartLeg(Unit u, GeoPoint from, int? orderId, double t)
    {
        var to = orderId is int id ? Problem.Orders[id].Location : Problem.Depot.Location;
        double km = TravelModel.RoadKm(from, to);
        double baseMin = km * 60 / TravelModel.SpeedKmh(t);
        double factor = Problem.Travel.IncidentFactor(from, to, t);
        double planned = baseMin;   // lo que estimaba el plan con tráfico normal de esa hora
        double noise = Math.Clamp(Math.Exp(Gauss() * 0.13), 0.82, 1.5);
        double actual = baseMin * factor * noise;
        u.From = from;
        u.To = to;
        u.Target = orderId;
        u.LegStart = t;
        u.LegEnd = t + Math.Max(1, actual);
        u.Km += km;
        u.Legs++;
        u.Status = orderId is null ? UnitStatus.Regresando : UnitStatus.EnRuta;
        LegsLog.Add(new Leg(u.Vehicle.Id, orderId, t, planned, actual, factor, km));
    }

    double Gauss()
    {
        double u1 = 1 - _rng.NextDouble(), u2 = _rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    public GeoPoint PositionOf(Unit u, double t)
    {
        if (u.Status is UnitStatus.EnRuta or UnitStatus.Regresando)
        {
            double f = Math.Clamp((t - u.LegStart) / Math.Max(1e-6, u.LegEnd - u.LegStart), 0, 1);
            return u.From.Lerp(u.To, f);
        }
        if (u.Status is UnitStatus.Esperando or UnitStatus.Entregando && u.Target is int id) return Problem.Orders[id].Location;
        if (u.Status == UnitStatus.Averiada) return u.Position;
        if (u.Status == UnitStatus.Terminado) return Problem.Depot.Location;
        return Problem.Depot.Location;
    }

    // ── predicción ──────────────────────────────────────────────────────

    /// <summary>
    /// Hora estimada de cada entrega pendiente con el tráfico de ahora (incluye incidentes activos).
    /// El tramo en curso se estima con el avance de la unidad (como lo vería el GPS).
    /// </summary>
    public List<Eta> Predict()
    {
        var list = new List<Eta>();
        foreach (var u in Units)
        {
            if (u.Plan.Count == 0 || u.Status == UnitStatus.Averiada) continue;
            var (startNode, startTime, rest) = Commit(u);
            if (startNode is int sn && u.Target is int cur)
            {
                var o = Problem.Orders[cur];
                double arrive = u.Status == UnitStatus.EnRuta ? u.LegEnd : _arrive.GetValueOrDefault(u.Vehicle.Id, Now);
                double late = Math.Max(0, Math.Max(arrive, o.Open) - o.Close);
                list.Add(new Eta(cur, u.Vehicle.Id, arrive, late, Classify(arrive, o)));
            }
            var r = new Route { VehicleId = u.Vehicle.Id, StartNode = startNode ?? 0, StartTime = startTime, CapacityKg = double.MaxValue };
            var sched = new List<StopTime>();
            Evaluator.Evaluate(Problem, r, rest.ToArray(), sched);
            foreach (var s in sched)
                list.Add(new Eta(s.OrderId, u.Vehicle.Id, s.Arrive, s.LateMin, Classify(s.Arrive, Problem.Orders[s.OrderId])));
        }
        return list;
    }

    static Risk Classify(double arrive, Order o) =>
        Math.Max(arrive, o.Open) > o.Close + 0.5 ? Risk.Tarde : arrive > o.Close - 15 ? Risk.EnRiesgo : Risk.ATiempo;

    /// <summary>
    /// Desde dónde y a qué hora se puede re-planear una unidad: si va a una entrega (o la atiende), esa ya
    /// queda comprometida y se parte de ahí; si está en el CEDIS o regresando, parte del CEDIS.
    /// </summary>
    (int? StartNode, double StartTime, List<int> Pending) Commit(Unit u)
    {
        switch (u.Status)
        {
            case UnitStatus.EnRuta when u.Target is int t:
            {
                var o = Problem.Orders[t];
                return (Problem.NodeOf(t), Math.Max(u.LegEnd, o.Open) + o.ServiceMin, u.Plan.Skip(1).ToList());
            }
            case UnitStatus.Esperando when u.Target is int t:
                return (Problem.NodeOf(t), u.BusyUntil + Problem.Orders[t].ServiceMin, u.Plan.Skip(1).ToList());
            case UnitStatus.Entregando when u.Target is int t:
                return (Problem.NodeOf(t), u.BusyUntil, u.Plan.Skip(1).ToList());
            case UnitStatus.Regresando:
                return (null, u.LegEnd + 10, [.. u.Plan]);
            case UnitStatus.Terminado:
                return (null, Math.Max(Now, u.LegEnd) + 10, [.. u.Plan]);
            default:
                return (null, Math.Max(Now, u.Vehicle.ShiftStart), [.. u.Plan]);
        }
    }

    // ── lo que pasa en el día ───────────────────────────────────────────

    public void BreakDown(int vehicleId)
    {
        var u = Units[vehicleId];
        if (u.Status is UnitStatus.Averiada or UnitStatus.Terminado) return;
        u.Position = PositionOf(u, Now);
        u.Status = UnitStatus.Averiada;
        u.BrokenAt = Now;
        var orphans = u.Plan.ToList();
        u.Plan.Clear();
        u.Target = null;
        Add(EventKind.Averia, $"{u.Vehicle.Code} se descompuso cerca de {NearestArea(u.Position)}: {orphans.Count} entregas sin unidad.", vehicleId);
        Unassigned.AddRange(orphans);
        _orphanFrom = u.Vehicle.Code;
        if (AutoReplan && orphans.Count > 0) Replan($"{u.Vehicle.Code} descompuesta");
    }

    string? _orphanFrom;

    public Order AddUrgentOrder(GeoPoint? at = null)
    {
        var o = Scenario.RandomOrder(_rng, Problem.Orders.Count, Now, urgent: true, now: Now);
        if (at is GeoPoint g) o = new Order { Id = o.Id, Code = o.Code, Customer = o.Customer, Area = NearestArea(g), Location = g, Kg = o.Kg, ServiceMin = o.ServiceMin, Open = o.Open, Close = o.Close, Urgent = true, CreatedAt = Now };
        Problem.Add(o);
        Unassigned.Add(o.Id);
        Add(EventKind.Urgente, $"Pedido urgente {o.Code}: {o.Customer} en {o.Area}, entregar antes de las {Clock.Hm(o.Close)}.", orderId: o.Id);
        if (AutoReplan) Replan($"pedido urgente {o.Code}");
        return o;
    }

    static readonly (string Label, GeoPoint At)[] Hotspots =
    [
        ("Choque en Circuito Interior a la altura de Insurgentes", new(19.4080, -99.1720)),
        ("Marcha en Paseo de la Reforma", new(19.4300, -99.1550)),
        ("Obra en Calzada de Tlalpan", new(19.3700, -99.1420)),
        ("Encharcamiento en Viaducto", new(19.4000, -99.1350)),
        ("Choque en Periférico Norte", new(19.5000, -99.2100)),
        ("Bloqueo en Eje Central", new(19.4450, -99.1400)),
    ];

    public Incident AddIncident(GeoPoint? at = null, double factor = 2.4, double durationMin = 75, string? label = null)
    {
        var spot = Hotspots[_incidentSeq % Hotspots.Length];
        var center = at ?? spot.At;
        var i = new Incident(++_incidentSeq, label ?? (at is null ? spot.Label : $"Tráfico pesado en {NearestArea(center)}"), center, 2.2, factor, Now, Now + durationMin);
        Problem.Travel.AddIncident(i);
        // las unidades que ya van por esa zona se alentan desde ahora
        int hit = 0;
        foreach (var u in Units.Where(u => u.Status is UnitStatus.EnRuta or UnitStatus.Regresando && u.LegEnd > Now))
        {
            if (Problem.Travel.IncidentFactor(u.From, u.To, Now) > 1)
            {
                u.LegEnd = Now + (u.LegEnd - Now) * factor;
                hit++;
            }
        }
        Add(EventKind.Trafico, $"{i.Label}: tráfico {factor:0.#}× más lento hasta las {Clock.Hm(i.Until)}{(hit > 0 ? $" ({hit} unidades atrapadas)" : "")}.");
        return i;
    }

    // ── re-optimización en vivo ─────────────────────────────────────────

    public ReplanReport Replan(string reason, Action<SolverProgress>? progress = null)
    {
        var sw = Stopwatch.StartNew();
        var before = Units.SelectMany(u => u.Plan.Select(o => (o, u.Vehicle.Id))).ToDictionary(x => x.o, x => x.Id);
        int riskBefore = Predict().Count(e => e.Risk != Risk.ATiempo) + Unassigned.Count;

        var commits = new Dictionary<int, int?>();
        var routes = new List<Route>();
        foreach (var u in Units)
        {
            if (u.Status == UnitStatus.Averiada)
            {
                routes.Add(new Route { VehicleId = u.Vehicle.Id, Fixed = true });
                continue;
            }
            var (startNode, startTime, rest) = Commit(u);
            bool fromDepot = startNode is null;
            double onboard = fromDepot ? 0 : rest.Sum(id => Problem.Orders[id].Kg);
            commits[u.Vehicle.Id] = fromDepot ? null : u.Target;
            // desde el CEDIS sale con carga completa; en ruta, cabe lo que traía (se asume transbordo/cross-dock para lo nuevo)
            double cap = fromDepot ? u.Vehicle.CapacityKg : Math.Max(onboard, u.Vehicle.CapacityKg - u.KgDelivered - (u.Target is int t ? Problem.Orders[t].Kg : 0));
            routes.Add(new Route { VehicleId = u.Vehicle.Id, StartNode = startNode ?? 0, StartTime = startTime, CapacityKg = cap, Stops = rest });
        }
        var start = new Solution { Routes = routes, Unassigned = [.. Unassigned] };
        var solver = new AlnsSolver(Problem, new SolverOptions { Seed = PlanVersion * 31, TimeLimitMs = ReplanBudgetMs, MaxIterations = ReplanMaxIterations, ReportEvery = 10 });
        Problem.SetHome(before);   // mover una entrega ya avisada al cliente cuesta: solo se hace si vale la pena
        Solution best;
        try { best = solver.Solve(start, progress); }
        finally { Problem.ClearHome(); }

        Unassigned.Clear();
        Unassigned.AddRange(best.Unassigned);
        var moved = new List<(int Order, int? From, int To)>();
        foreach (var r in best.Routes)
        {
            var u = Units[r.VehicleId];
            if (u.Status == UnitStatus.Averiada) continue;
            var plan = new List<int>();
            if (commits[r.VehicleId] is int c) plan.Add(c);
            plan.AddRange(r.Stops);
            u.Plan.Clear();
            u.Plan.AddRange(plan);
            foreach (var o in r.Stops)
            {
                int? prev = before.TryGetValue(o, out var pv) ? pv : null;
                if (prev != r.VehicleId)
                {
                    moved.Add((o, prev, r.VehicleId));
                    string why = Problem.Orders[o].Urgent && prev is null ? "pedido urgente" : prev is null ? $"de {_orphanFrom ?? "una unidad descompuesta"}" : $"de {Units[prev.Value].Vehicle.Code}";
                    u.Received.Add((Now, o, why));
                }
            }
            if (u.Status == UnitStatus.Terminado && u.Plan.Count > 0) u.LegEnd = Math.Max(u.LegEnd, Now);
        }
        PlanVersion++;
        int riskAfter = Predict().Count(e => e.Risk != Risk.ATiempo) + Unassigned.Count;
        sw.Stop();
        var units = moved.Select(m => Units[m.To].Vehicle.Code).Distinct().OrderBy(x => x).ToList();
        LastReplan = new ReplanReport(Now, reason, moved.Count, units, riskBefore, riskAfter, sw.Elapsed.TotalMilliseconds, solver.Iterations);
        Add(EventKind.Reoptimizacion, $"Re-optimización por {reason}: {moved.Count} entregas reasignadas{(units.Count > 0 ? $" a {string.Join(", ", units)}" : "")} en {sw.Elapsed.TotalSeconds:0.0} s. Entregas en riesgo: {riskBefore} → {riskAfter}.");
        return LastReplan;
    }

    // ── utilidades ──────────────────────────────────────────────────────

    void Add(EventKind kind, string text, int? vehicleId = null, int? orderId = null, double? at = null)
    {
        Log.Add(new LogEntry(at ?? Now, kind, text, vehicleId, orderId));
        if (Log.Count > 400) Log.RemoveRange(0, Log.Count - 400);
    }

    public string NearestArea(GeoPoint g)
    {
        var o = Problem.Orders.MinBy(x => x.Location.HaversineKm(g));
        return o is null ? "la zona" : o.Area.Split(',')[0];
    }
}

public sealed record ReplanReport(double At, string Reason, int Moved, List<string> Units, int RiskBefore, int RiskAfter, double Ms, int Iterations);
