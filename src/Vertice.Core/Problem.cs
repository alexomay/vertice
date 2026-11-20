namespace Vertice.Core;

/// <summary>Pesos del costo: lo que le cuesta a la empresa cada cosa (en pesos).</summary>
public sealed record CostWeights
{
    public double LatePerMin { get; init; } = 14;      // cliente molesto, penalización contractual
    public double OvertimePerMin { get; init; } = 6;   // horas extra del chofer
    public double Unassigned { get; init; } = 6000;    // una entrega que nadie lleva
    public double OverCapPerKg { get; init; } = 80;    // sobrecupo (casi prohibido)
    public double WaitPerMin { get; init; } = 1.5;     // chofer parado esperando a que abra el cliente
    public double Reassign { get; init; } = 140;       // cambiarle la unidad a una entrega ya avisada (estabilidad del plan)
}

/// <summary>Problema de ruteo con ventanas de horario y capacidad (VRPTW) dependiente de la hora.</summary>
public sealed class Problem
{
    public Problem(Depot depot, IEnumerable<Vehicle> vehicles, TravelModel? travel = null)
    {
        Depot = depot;
        Net = new Network(depot);
        Travel = travel ?? new TravelModel();
        Vehicles = vehicles.ToList();
        for (int i = 0; i < Vehicles.Count; i++)
            if (Vehicles[i].Id != i) throw new ArgumentException("Los ids de unidad deben ser 0..n-1 en orden.");
    }

    public Depot Depot { get; }
    public Network Net { get; }
    public TravelModel Travel { get; }
    public List<Vehicle> Vehicles { get; }
    public List<Order> Orders { get; } = [];
    public CostWeights Weights { get; init; } = new();

    /// <summary>Nodo de la red de una entrega (el 0 es el CEDIS).</summary>
    public static int NodeOf(int orderId) => orderId + 1;

    /// <summary>Unidad que ya tenía asignada cada entrega antes de re-optimizar (-1 = ninguna). Mover una entrega cuesta.</summary>
    public int[] Home { get; private set; } = [];

    public void SetHome(IReadOnlyDictionary<int, int> assignment)
    {
        Home = Enumerable.Repeat(-1, Orders.Count).ToArray();
        foreach (var (order, vehicle) in assignment) Home[order] = vehicle;
    }

    public void ClearHome() => Home = [];

    public Order Add(Order o)
    {
        if (o.Id != Orders.Count) throw new ArgumentException("Los ids de entrega deben ser consecutivos.");
        Orders.Add(o);
        int node = Net.Add(o.Location);
        if (node != NodeOf(o.Id)) throw new InvalidOperationException("Red desalineada.");
        return o;
    }

    public Vehicle Vehicle(int id) => Vehicles[id];
}

/// <summary>
/// Ruta de una unidad. Normalmente sale del CEDIS al inicio del turno; al re-optimizar en vivo
/// sale de la parada que está atendiendo (StartNode) a la hora en que la termina (StartTime).
/// </summary>
public sealed class Route
{
    public required int VehicleId { get; init; }
    public int StartNode { get; init; }
    public double StartTime { get; init; }
    public double CapacityKg { get; init; }
    public bool Fixed { get; init; }   // unidad descompuesta o que ya terminó: no se le asigna nada
    public List<int> Stops { get; init; } = [];   // ids de entrega en orden

    public Route Clone() => new() { VehicleId = VehicleId, StartNode = StartNode, StartTime = StartTime, CapacityKg = CapacityKg, Fixed = Fixed, Stops = [.. Stops] };
}

public readonly record struct RouteEval(double Km, double LateMin, double OvertimeMin, double WaitMin, double End, double Kg, int LateStops, double Cost);

/// <summary>Una parada ya calendarizada: cuándo llega, cuándo se va y con cuánto retraso.</summary>
public readonly record struct StopTime(int OrderId, double Arrive, double Start, double Depart, double LateMin, double LegKm);

public static class Evaluator
{
    /// <summary>
    /// Evalúa la ruta simulando el día parada por parada (tiempos dependientes de la hora).
    /// Con <paramref name="bound"/> deja de calcular en cuanto el costo parcial lo rebasa (devuelve costo infinito).
    /// </summary>
    public static RouteEval Evaluate(Problem p, Route r, ReadOnlySpan<int> stops, List<StopTime>? schedule = null, double bound = double.PositiveInfinity)
    {
        var v = p.Vehicles[r.VehicleId];
        var w = p.Weights;
        var orders = p.Orders;
        double t = DepartTime(p, r, stops), km = 0, late = 0, wait = 0, kg = 0;
        int at = r.StartNode, lateStops = 0;
        double fixedCost = stops.Length > 0 && r.StartNode == 0 ? v.FixedCost : 0;
        var home = p.Home;
        double moved = 0;
        for (int i = 0; i < stops.Length; i++)
        {
            var o = orders[stops[i]];
            int node = o.Id + 1;
            double leg = p.Net.Km(at, node);
            t += p.Travel.Minutes(leg, p.Net[at], p.Net[node], t);
            km += leg;
            double arrive = t;
            if (t < o.Open) { wait += o.Open - t; t = o.Open; }
            double l = t - o.Close;
            if (l > 0) { late += l; if (l > 0.5) lateStops++; } else l = 0;
            double start = t;
            t += o.ServiceMin;
            kg += o.Kg;
            if (o.Id < home.Length && home[o.Id] >= 0 && home[o.Id] != r.VehicleId) moved += w.Reassign;
            schedule?.Add(new StopTime(o.Id, arrive, start, t, l, leg));
            at = node;
            if (km * v.CostPerKm + late * w.LatePerMin + wait * w.WaitPerMin + moved + fixedCost > bound)
                return new RouteEval(km, late, 0, wait, t, kg, lateStops, double.PositiveInfinity);
        }
        if (stops.Length > 0 || r.StartNode != 0)
        {
            double back = p.Net.Km(at, 0);
            t += p.Travel.Minutes(back, p.Net[at], p.Net[0], t);
            km += back;
        }
        double overtime = Math.Max(0, t - v.ShiftEnd);
        double over = Math.Max(0, kg - r.CapacityKg);
        double cost = km * v.CostPerKm + late * w.LatePerMin + wait * w.WaitPerMin + overtime * w.OvertimePerMin + over * w.OverCapPerKg + moved + fixedCost;
        return new RouteEval(km, late, overtime, wait, t, kg, lateStops, cost);
    }

    /// <summary>
    /// Si sale del CEDIS, la unidad no sale antes de lo necesario: llega justo cuando abre su primer cliente
    /// (nadie paga a un chofer por esperar afuera de un negocio cerrado).
    /// </summary>
    public static double DepartTime(Problem p, Route r, ReadOnlySpan<int> stops)
    {
        if (r.StartNode != 0 || stops.Length == 0) return r.StartTime;
        var first = p.Orders[stops[0]];
        int node = first.Id + 1;
        double leg = p.Net.Km(0, node);
        double travel = p.Travel.Minutes(leg, p.Net[0], p.Net[node], r.StartTime);
        return Math.Max(r.StartTime, first.Open - travel - 5);
    }

    public static RouteEval Evaluate(Problem p, Route r, List<StopTime>? schedule = null) =>
        Evaluate(p, r, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(r.Stops), schedule);
}
