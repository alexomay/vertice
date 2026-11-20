using Vertice.Core;
using Xunit;
using Xunit.Abstractions;

namespace Vertice.Core.Tests;

public class SolverTests(ITestOutputHelper log)
{
    static (Problem P, List<int> Ids) Day(int orders = 150, int vehicles = 12, int seed = 2026)
    {
        var p = Scenario.Build(seed, orders, vehicles);
        return (p, p.Orders.Select(o => o.Id).ToList());
    }

    static Kpi Measure(Problem p, Solution s)
    {
        double km = 0, late = 0; int lateStops = 0, used = 0;
        foreach (var r in s.Routes)
        {
            var e = Evaluator.Evaluate(p, r);
            km += e.Km; late += e.LateMin; lateStops += e.LateStops;
            if (r.Stops.Count > 0) used++;
        }
        return new Kpi(km, late, lateStops, used, s.Cost(p));
    }

    record Kpi(double Km, double LateMin, int LateStops, int Vehicles, double Cost);

    [Fact]
    public void Optimized_plan_beats_the_manual_plan()
    {
        var (p, ids) = Day();
        var manual = Measure(p, ManualPlanner.Plan(p, ids));
        var solver = new AlnsSolver(p, new SolverOptions { TimeLimitMs = 4000 });
        var best = solver.Solve(Solution.Empty(p, ids));
        var opt = Measure(p, best);
        log.WriteLine($"manual:     {manual}");
        log.WriteLine($"optimizado: {opt}  ({solver.Iterations} iteraciones)");

        Assert.Empty(best.Unassigned);
        Assert.True(opt.Cost < manual.Cost * 0.85, $"costo {opt.Cost:F0} vs manual {manual.Cost:F0}");
        Assert.True(opt.Km < manual.Km, "debe recorrer menos kilómetros");
        Assert.True(opt.LateStops <= manual.LateStops);
    }

    [Fact]
    public void Every_order_is_delivered_exactly_once_and_capacity_holds()
    {
        var (p, ids) = Day(80, 7, 11);
        var best = new AlnsSolver(p, new SolverOptions { TimeLimitMs = 800 }).Solve(Solution.Empty(p, ids));
        var seen = best.Routes.SelectMany(r => r.Stops).Concat(best.Unassigned).ToList();
        Assert.Equal(ids.Count, seen.Count);
        Assert.Equal(ids.Count, seen.Distinct().Count());
        foreach (var r in best.Routes)
            Assert.True(r.Stops.Sum(id => p.Orders[id].Kg) <= r.CapacityKg + 1e-6);
    }

    [Fact]
    public void Same_seed_gives_the_same_plan()
    {
        var (p, ids) = Day(60, 5, 3);
        var a = new AlnsSolver(p, new SolverOptions { MaxIterations = 150, TimeLimitMs = 60_000 }).Solve(Solution.Empty(p, ids));
        var b = new AlnsSolver(p, new SolverOptions { MaxIterations = 150, TimeLimitMs = 60_000 }).Solve(Solution.Empty(p, ids));
        Assert.Equal(a.Routes.Select(r => string.Join(',', r.Stops)), b.Routes.Select(r => string.Join(',', r.Stops)));
    }

    [Fact]
    public void Rush_hour_is_slower_than_midday()
    {
        Assert.True(TravelModel.SpeedKmh(8 * 60) < TravelModel.SpeedKmh(11 * 60));
        Assert.True(TravelModel.SpeedKmh(18 * 60) < TravelModel.SpeedKmh(11 * 60));
    }

    [Fact]
    public void Incident_slows_only_legs_that_touch_it()
    {
        var t = new TravelModel();
        var a = new GeoPoint(19.43, -99.13);
        var b = new GeoPoint(19.44, -99.14);
        var far = new GeoPoint(19.30, -99.25);
        double before = t.Minutes(2, a, b, 600);
        t.AddIncident(new Incident(1, "Choque", a, 1.5, 2.5, 590, 700));
        Assert.Equal(before * 2.5, t.Minutes(2, a, b, 600), 6);
        Assert.Equal(t.Minutes(2, far, far, 600), new TravelModel().Minutes(2, far, far, 600), 6);
        Assert.Equal(before, t.Minutes(2, a, b, 720), 6);   // ya pasó
    }
}
