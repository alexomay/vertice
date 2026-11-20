using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Vertice.Core;

/// <summary>Asignación de entregas a unidades: una ruta por unidad + lo que quedó sin asignar.</summary>
public sealed class Solution
{
    public List<Route> Routes { get; init; } = [];
    public List<int> Unassigned { get; init; } = [];

    public Solution Clone() => new() { Routes = Routes.Select(r => r.Clone()).ToList(), Unassigned = [.. Unassigned] };

    public double Cost(Problem p) => Routes.Sum(r => Evaluator.Evaluate(p, r).Cost) + Unassigned.Count * p.Weights.Unassigned;

    public int RouteOf(int orderId)
    {
        for (int i = 0; i < Routes.Count; i++) if (Routes[i].Stops.Contains(orderId)) return i;
        return -1;
    }

    /// <summary>Rutas vacías desde el CEDIS para todas las unidades (punto de partida del día).</summary>
    public static Solution Empty(Problem p, IEnumerable<int> orderIds) => new()
    {
        Routes = p.Vehicles.Select(v => new Route { VehicleId = v.Id, StartNode = 0, StartTime = v.ShiftStart, CapacityKg = v.CapacityKg }).ToList(),
        Unassigned = orderIds.ToList(),
    };
}

public sealed record SolverOptions
{
    public int Seed { get; init; } = 7;
    public double TimeLimitMs { get; init; } = 2500;
    public int MaxIterations { get; init; } = int.MaxValue;
    public double MinRemoveShare { get; init; } = 0.06;
    public double MaxRemoveShare { get; init; } = 0.18;
    public int MaxRemove { get; init; } = 24;
    /// <summary>Cada cuántas iteraciones se reporta el avance (para la gráfica en vivo).</summary>
    public int ReportEvery { get; init; } = 25;
}

public readonly record struct SolverProgress(int Iteration, double BestCost, double CurrentCost, double ElapsedMs, bool Done);

/// <summary>
/// ALNS (Adaptive Large Neighborhood Search, Ropke &amp; Pisinger 2006): destruye una parte de la solución
/// (al azar, lo más caro, entregas parecidas o una ruta completa), la reconstruye (barata o con "regret")
/// y acepta con recocido simulado. Los operadores que más mejoran se usan más.
/// </summary>
public sealed class AlnsSolver
{
    readonly Problem _p;
    readonly SolverOptions _o;
    readonly Random _rng;
    int[] _buf = new int[64];

    public AlnsSolver(Problem p, SolverOptions? options = null)
    {
        _p = p;
        _o = options ?? new SolverOptions();
        _rng = new Random(_o.Seed);
    }

    enum Destroy { Random, Worst, Related, Route }
    enum Repair { Greedy, Regret, GreedyNoise }

    public Solution Solve(Solution start, Action<SolverProgress>? progress = null, CancellationToken cancel = default)
    {
        var sw = Stopwatch.StartNew();
        var cur = start.Clone();
        Insert(cur, Repair.Regret);
        double curCost = cur.Cost(_p);
        var best = cur.Clone();
        double bestCost = curCost;

        double[] dw = [1, 1, 1, 1], rw = [1, 1, 1];
        double[] ds = new double[4], rs = new double[3];
        int[] dn = new int[4], rn = new int[3];
        int assigned = cur.Routes.Sum(r => r.Stops.Count) + cur.Unassigned.Count;
        // temperatura: al inicio se acepta un 3 % peor con probabilidad 1/2; se enfría hasta casi cero
        double t0 = Math.Max(1, 0.03 * curCost / Math.Log(2));
        int it = 0;
        progress?.Invoke(new SolverProgress(0, bestCost, curCost, sw.Elapsed.TotalMilliseconds, false));

        while (it < _o.MaxIterations && sw.Elapsed.TotalMilliseconds < _o.TimeLimitMs && !cancel.IsCancellationRequested && assigned > 0)
        {
            it++;
            int d = Roulette(dw), r = Roulette(rw);
            var cand = cur.Clone();
            int q = Math.Clamp((int)Math.Round(assigned * (_o.MinRemoveShare + _rng.NextDouble() * (_o.MaxRemoveShare - _o.MinRemoveShare))), 2, _o.MaxRemove);
            Remove(cand, (Destroy)d, q);
            Insert(cand, (Repair)r);
            double c = cand.Cost(_p);

            double frac = sw.Elapsed.TotalMilliseconds / _o.TimeLimitMs;
            double temp = t0 * Math.Pow(0.002, Math.Min(1, frac));
            double score = 0;
            if (c < bestCost - 1e-6) { best = cand.Clone(); bestCost = c; cur = cand; curCost = c; score = 33; }
            else if (c < curCost - 1e-6) { cur = cand; curCost = c; score = 9; }
            else if (_rng.NextDouble() < Math.Exp(-(c - curCost) / temp)) { cur = cand; curCost = c; score = 13; }
            ds[d] += score; dn[d]++; rs[r] += score; rn[r]++;

            if (it % 100 == 0)
            {
                Adapt(dw, ds, dn);
                Adapt(rw, rs, rn);
            }
            if (progress != null && it % _o.ReportEvery == 0)
                progress(new SolverProgress(it, bestCost, curCost, sw.Elapsed.TotalMilliseconds, false));
        }
        Polish(best);
        bestCost = best.Cost(_p);
        progress?.Invoke(new SolverProgress(it, bestCost, curCost, sw.Elapsed.TotalMilliseconds, true));
        Iterations = it;
        return best;
    }

    public int Iterations { get; private set; }

    static void Adapt(double[] w, double[] s, int[] n)
    {
        for (int i = 0; i < w.Length; i++)
        {
            if (n[i] > 0) w[i] = 0.8 * w[i] + 0.2 * (s[i] / n[i]);
            w[i] = Math.Max(w[i], 0.5);
            s[i] = 0; n[i] = 0;
        }
    }

    int Roulette(double[] w)
    {
        double x = _rng.NextDouble() * w.Sum();
        for (int i = 0; i < w.Length; i++) { x -= w[i]; if (x <= 0) return i; }
        return w.Length - 1;
    }

    // ── destrucción ─────────────────────────────────────────────────────

    void Remove(Solution s, Destroy op, int q)
    {
        var pool = new List<(int Route, int Order)>();
        for (int ri = 0; ri < s.Routes.Count; ri++)
            if (!s.Routes[ri].Fixed) foreach (var o in s.Routes[ri].Stops) pool.Add((ri, o));
        if (pool.Count == 0) return;
        q = Math.Min(q, pool.Count);
        var take = new HashSet<int>();

        switch (op)
        {
            case Destroy.Random:
                while (take.Count < q) take.Add(pool[_rng.Next(pool.Count)].Order);
                break;

            case Destroy.Worst:
            {
                // lo que más cuesta tener en su ruta (con algo de azar para no repetir siempre lo mismo)
                var gains = new List<(double Gain, int Order)>();
                foreach (var r in s.Routes.Where(r => !r.Fixed && r.Stops.Count > 0))
                {
                    double full = Evaluator.Evaluate(_p, r).Cost;
                    for (int i = 0; i < r.Stops.Count; i++)
                    {
                        var span = Without(r.Stops, i);
                        gains.Add((full - Evaluator.Evaluate(_p, r, span).Cost, r.Stops[i]));
                    }
                }
                gains.Sort((a, b) => b.Gain.CompareTo(a.Gain));
                while (take.Count < q && gains.Count > 0)
                {
                    int k = (int)(Math.Pow(_rng.NextDouble(), 4) * gains.Count);
                    take.Add(gains[k].Order);
                    gains.RemoveAt(k);
                }
                break;
            }

            case Destroy.Related:
            {
                // entregas cercanas y con horario parecido (Shaw): se reacomodan juntas
                int seed = pool[_rng.Next(pool.Count)].Order;
                take.Add(seed);
                var so = _p.Orders[seed];
                var rel = pool.Where(x => x.Order != seed)
                    .Select(x => (x.Order, R: _p.Net.Km(Problem.NodeOf(seed), Problem.NodeOf(x.Order)) / 10.0 + Math.Abs(_p.Orders[x.Order].Open - so.Open) / 240.0))
                    .OrderBy(x => x.R).ToList();
                while (take.Count < q && rel.Count > 0)
                {
                    int k = (int)(Math.Pow(_rng.NextDouble(), 5) * rel.Count);
                    take.Add(rel[k].Order);
                    rel.RemoveAt(k);
                }
                break;
            }

            case Destroy.Route:
            {
                // vaciar una ruta corta: obliga a probar con una unidad menos
                var routes = s.Routes.Where(r => !r.Fixed && r.Stops.Count > 0).OrderBy(r => r.Stops.Count).ToList();
                var victim = routes[Math.Min(routes.Count - 1, (int)(Math.Pow(_rng.NextDouble(), 2) * routes.Count))];
                foreach (var o in victim.Stops) take.Add(o);
                while (take.Count < q) take.Add(pool[_rng.Next(pool.Count)].Order);
                break;
            }
        }

        foreach (var r in s.Routes) r.Stops.RemoveAll(take.Contains);
        s.Unassigned.AddRange(take);
    }

    ReadOnlySpan<int> Without(List<int> stops, int skip)
    {
        Ensure(stops.Count);
        int k = 0;
        for (int i = 0; i < stops.Count; i++) if (i != skip) _buf[k++] = stops[i];
        return _buf.AsSpan(0, k);
    }

    void Ensure(int n) { if (_buf.Length < n + 2) _buf = new int[(n + 2) * 2]; }

    // ── reconstrucción ──────────────────────────────────────────────────

    /// <summary>Mejor lugar para meter la entrega en la ruta: (aumento de costo, posición).</summary>
    (double Delta, int Pos) BestPosition(Route r, double routeCost, double routeKg, int orderId)
    {
        if (r.Fixed) return (double.PositiveInfinity, -1);
        var o = _p.Orders[orderId];
        if (routeKg + o.Kg > r.CapacityKg + 1e-9) return (double.PositiveInfinity, -1);
        var stops = CollectionsMarshal.AsSpan(r.Stops);
        Ensure(stops.Length + 1);
        double best = double.PositiveInfinity;
        int bestPos = -1;
        for (int pos = 0; pos <= stops.Length; pos++)
        {
            int k = 0;
            for (int i = 0; i < pos; i++) _buf[k++] = stops[i];
            _buf[k++] = orderId;
            for (int i = pos; i < stops.Length; i++) _buf[k++] = stops[i];
            var e = Evaluator.Evaluate(_p, r, _buf.AsSpan(0, k), null, routeCost + best);
            double delta = e.Cost - routeCost;
            if (delta < best) { best = delta; bestPos = pos; }
        }
        return (best, bestPos);
    }

    void Insert(Solution s, Repair op)
    {
        if (s.Unassigned.Count == 0) return;
        int nR = s.Routes.Count;
        var cost = new double[nR];
        var kg = new double[nR];
        for (int i = 0; i < nR; i++)
        {
            var e = Evaluator.Evaluate(_p, s.Routes[i]);
            cost[i] = e.Cost; kg[i] = e.Kg;
        }
        var pending = s.Unassigned.OrderBy(_ => _rng.Next()).ToList();
        s.Unassigned.Clear();
        double noise = op == Repair.GreedyNoise ? 0.15 : 0;
        // tabla (entrega × ruta) del mejor lugar; al insertar solo se recalcula la ruta que cambió
        var table = pending.ToDictionary(o => o, o => new (double, int)[nR]);
        foreach (var o in pending)
            for (int ri = 0; ri < nR; ri++) table[o][ri] = BestPosition(s.Routes[ri], cost[ri], kg[ri], o);

        while (pending.Count > 0)
        {
            int pickIdx = -1, pickRoute = -1, pickPos = -1;
            double pickScore = double.NegativeInfinity;
            for (int idx = 0; idx < pending.Count; idx++)
            {
                var row = table[pending[idx]];
                double b1 = double.PositiveInfinity, b2 = double.PositiveInfinity;
                int r1 = -1;
                for (int ri = 0; ri < nR; ri++)
                {
                    double d = row[ri].Item1;
                    if (noise > 0 && !double.IsInfinity(d)) d *= 1 + noise * (2 * _rng.NextDouble() - 1);
                    if (d < b1) { b2 = b1; b1 = d; r1 = ri; }
                    else if (d < b2) b2 = d;
                }
                if (r1 < 0) continue;
                double score = op == Repair.Regret
                    ? (double.IsInfinity(b2) ? 1e9 - b1 : b2 - b1)   // la que más pierde si no va en su mejor lugar
                    : -b1;                                           // la más barata
                if (score > pickScore) { pickScore = score; pickIdx = idx; pickRoute = r1; pickPos = row[r1].Item2; }
            }
            if (pickIdx < 0) { s.Unassigned.AddRange(pending); return; }   // ya no cabe en ninguna unidad

            int order = pending[pickIdx];
            pending.RemoveAt(pickIdx);
            var route = s.Routes[pickRoute];
            route.Stops.Insert(pickPos, order);
            var ev = Evaluator.Evaluate(_p, route);
            cost[pickRoute] = ev.Cost; kg[pickRoute] = ev.Kg;
            foreach (var o in pending) table[o][pickRoute] = BestPosition(route, cost[pickRoute], kg[pickRoute], o);
        }
    }

    // ── pulido final: mover una parada de lugar dentro de su ruta (or-opt) y 2-opt ──

    void Polish(Solution s)
    {
        foreach (var r in s.Routes.Where(r => !r.Fixed && r.Stops.Count > 2))
        {
            bool improved = true;
            int guard = 0;
            while (improved && guard++ < 20)
            {
                improved = false;
                double baseCost = Evaluator.Evaluate(_p, r).Cost;
                int n = r.Stops.Count;
                for (int i = 0; i < n - 1 && !improved; i++)
                    for (int j = i + 1; j < n && !improved; j++)
                    {
                        r.Stops.Reverse(i, j - i + 1);   // 2-opt: voltear un tramo
                        if (Evaluator.Evaluate(_p, r).Cost < baseCost - 1e-6) improved = true;
                        else r.Stops.Reverse(i, j - i + 1);
                    }
            }
        }
    }
}
