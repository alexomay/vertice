using Vertice.Core;
using Xunit;
using Xunit.Abstractions;

namespace Vertice.Core.Tests;

public class SimulationTests(ITestOutputHelper log)
{
    static Engine Ready(int orders = 120, int vehicles = 10, int seed = 2026)
    {
        var e = Engine.Create(seed, orders, vehicles);
        e.Optimize(60_000, maxIterations: 300);   // por iteraciones, no por tiempo: mismo plan en cualquier máquina
        return e;
    }

    [Fact]
    public void A_guided_day_ends_with_every_order_delivered()
    {
        var e = Ready();
        e.Start(guided: true);
        var sim = e.Sim!;
        sim.ReplanBudgetMs = 60_000;
        sim.ReplanMaxIterations = 120;
        int guard = 0;
        while (!sim.Finished && guard++ < 24 * 60) sim.Advance(1);

        var k = e.Kpi();
        log.WriteLine($"entregadas {k.Delivered}/{k.Total}, a tiempo {k.OnTimePct}%, km {k.KmDriven}, fin {Clock.Hm(sim.Now)}");
        foreach (var l in sim.Log.Where(l => l.Kind is EventKind.Averia or EventKind.Urgente or EventKind.Trafico or EventKind.Reoptimizacion))
            log.WriteLine($"{Clock.Hm(l.At)} {l.Kind}: {l.Text}");

        Assert.True(sim.Finished);
        Assert.Empty(sim.Unassigned);
        Assert.Equal(k.Total, k.Delivered);
        Assert.Contains(sim.Units, u => u.Status == UnitStatus.Averiada);
        Assert.True(sim.Log.Count(l => l.Kind == EventKind.Reoptimizacion) >= 4);   // avería + 3 urgentes
        Assert.True(k.OnTimePct >= 85, $"a tiempo {k.OnTimePct}%");
        // nadie entrega dos veces y nada se pierde
        Assert.Equal(sim.Delivered.Count, sim.Units.Sum(u => u.Done.Count));
    }

    [Fact]
    public void A_breakdown_hands_its_orders_to_other_units()
    {
        var e = Engine.Create(5, 90, 8);
        e.Optimize(60_000, maxIterations: 400);
        e.Start(guided: false);
        var sim = e.Sim!;
        sim.ReplanBudgetMs = 60_000;
        sim.ReplanMaxIterations = 150;   // reproducible: no depende de qué tan rápida sea la máquina
        sim.Advance(3 * 60);   // 10:50 aprox.
        var victim = sim.Units.Where(u => u.Status == UnitStatus.EnRuta && u.Plan.Count >= 3).OrderByDescending(u => u.Plan.Count).First();
        var orphans = victim.Plan.ToList();

        sim.BreakDown(victim.Vehicle.Id);

        Assert.Equal(UnitStatus.Averiada, victim.Status);
        Assert.Empty(victim.Plan);
        Assert.Empty(sim.Unassigned);
        foreach (var o in orphans) Assert.Contains(sim.Units, u => u != victim && u.Plan.Contains(o));
        Assert.NotNull(sim.LastReplan);
        Assert.InRange(sim.LastReplan!.Moved, orphans.Count, orphans.Count + 6);   // rescata las huérfanas y casi no revuelve lo demás
    }

    [Fact]
    public void Units_on_the_road_keep_the_stop_they_are_heading_to()
    {
        var e = Ready(90, 8, 9);
        e.Start(guided: false);
        var sim = e.Sim!;
        sim.ReplanBudgetMs = 300;
        sim.Advance(150);
        var heading = sim.Units.Where(u => u.Status == UnitStatus.EnRuta).ToDictionary(u => u.Vehicle.Id, u => u.Target);
        sim.AddUrgentOrder();
        foreach (var (vid, target) in heading)
            Assert.Equal(target, sim.Units[vid].Plan.FirstOrDefault());
    }

    [Fact]
    public void An_incident_slows_units_already_driving_through_it()
    {
        var e = Ready(90, 8, 9);
        e.Start(guided: false);
        var sim = e.Sim!;
        sim.Advance(120);
        Unit? u = null;
        for (int i = 0; i < 120 && u == null; i++)
        {
            u = sim.Units.FirstOrDefault(x => x.Status == UnitStatus.EnRuta && x.LegEnd - sim.Now > 4);
            if (u == null) sim.Advance(1);
        }
        Assert.NotNull(u);
        double before = u.LegEnd;
        sim.AddIncident(u.Position.Lerp(u.To, 0.5), factor: 2);
        Assert.True(u.LegEnd > before);
        Assert.Contains(sim.Predict(), p => p.VehicleId == u.Vehicle.Id);
    }

    [Fact]
    public void Assistant_answers_with_live_data()
    {
        var e = Ready(90, 8, 9);
        e.Start(guided: true);
        e.Sim!.ReplanBudgetMs = 300;
        e.Sim.Advance(5 * 60);

        string savings = e.Ask("¿Cuánto llevamos ahorrado?");
        string risk = e.Ask("¿qué entregas están en riesgo?");
        string unit = e.Ask("¿cómo va la U-03?");
        string order = e.Ask("¿dónde va el P-010?");
        string why = e.Ask("¿Por qué va tarde la unidad 2?");
        foreach (var a in new[] { savings, risk, unit, order, why }) log.WriteLine(a + "\n");

        Assert.Contains("km menos", savings);
        Assert.Contains("U-03", unit);
        Assert.Contains("P-010", order);
        Assert.StartsWith("U-02", why);
        Assert.DoesNotContain("Puedo decirte", risk);
    }

    [Fact]
    public void Snapshot_serializes_to_json()
    {
        var e = Ready(40, 4, 1);
        Assert.Contains("\"phase\":\"ready\"", e.SnapshotJson());
        e.Start(guided: false);
        e.Sim!.Advance(60);
        string json = e.SnapshotJson();
        Assert.Contains("\"phase\":\"live\"", json);
        Assert.Contains("\"units\":[{", json);
        Assert.Contains("\"kpis\":{", json);
    }
}
