using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Vertice.Core;

/// <summary>
/// Asistente de operación: responde preguntas del despachador con los datos en vivo
/// (por qué va tarde una unidad, qué está en riesgo, cuánto se ahorró, dónde va un pedido).
/// Es determinista: cada respuesta sale de cifras que se pueden auditar, sin inventar.
/// </summary>
public sealed partial class Assistant(Simulation sim, Func<Kpis> kpis)
{
    public static readonly string[] Suggestions =
    [
        "¿Qué entregas están en riesgo?",
        "¿Por qué va tarde la unidad más retrasada?",
        "¿Cuánto llevamos ahorrado?",
        "¿Cómo va el día?",
    ];

    public string Ask(string question)
    {
        string q = Normalize(question);
        var unit = UnitIn(q);
        var order = OrderIn(q);

        if (order != null) return OrderStatus(order);
        if (Regex.IsMatch(q, @"(por ?que|porque|motivo|causa).*(tarde|retras|atras)|(tarde|retras|atras).*(por ?que|porque)"))
            return WhyLate(unit ?? MostDelayed());
        if (Regex.IsMatch(q, @"riesgo|peligro|van a llegar tarde|se van a retrasar|alerta"))
            return AtRisk();
        if (Regex.IsMatch(q, @"ahorr|kilometr|gasolina|combustible|co2|costo|dinero|vs manual|contra el manual"))
            return Savings();
        if (unit != null) return DescribeUnit(unit);
        if (Regex.IsMatch(q, @"como va|resumen|estado|avance|panorama|dia"))
            return Overview();
        if (Regex.IsMatch(q, @"mas retrasad|peor|mas tarde"))
            return WhyLate(MostDelayed());
        return "Puedo decirte por qué va tarde una unidad (\"¿por qué va tarde la U-07?\"), qué entregas están en riesgo, cuánto llevamos ahorrado, cómo va el día o dónde va un pedido (\"¿dónde va el P-045?\").";
    }

    // ── respuestas ──────────────────────────────────────────────────────

    string WhyLate(Unit? u)
    {
        if (u == null) return "Ninguna unidad va tarde ahora mismo.";
        var etas = sim.Predict().Where(e => e.VehicleId == u.Vehicle.Id).ToList();
        var next = etas.OrderBy(e => e.Arrive).FirstOrDefault(e => e.Risk != Risk.ATiempo);
        var sb = new StringBuilder();
        if (u.Status == UnitStatus.Averiada)
            return $"{u.Vehicle.Code} ({u.Vehicle.Driver}) está descompuesta desde las {Clock.Hm(u.BrokenAt ?? sim.Now)}. Sus entregas pendientes ya se repartieron entre otras unidades.";

        if (next.OrderId == 0 && next.VehicleId == 0 && next.Arrive == 0)
        {
            sb.Append($"{u.Vehicle.Code} no trae entregas en riesgo. ");
            double drift = Drift(u);
            sb.Append(drift > 3 ? $"Va {drift:0} min más lenta que lo planeado, pero sus ventanas todavía aguantan." : "Va prácticamente según el plan.");
            return sb.ToString();
        }

        var o = sim.Problem.Orders[next.OrderId];
        sb.Append($"{u.Vehicle.Code} ({u.Vehicle.Driver}) llegaría a {o.Code} · {o.Customer} a las {Clock.Hm(next.Arrive)}; ");
        sb.Append(next.Risk == Risk.Tarde ? $"cierra a las {Clock.Hm(o.Close)}, o sea {Math.Round(next.LateMin)} min tarde.\n" : $"cierra a las {Clock.Hm(o.Close)}: va muy justa.\n");

        var legs = sim.LegsLog.Where(l => l.VehicleId == u.Vehicle.Id && l.Start + l.ActualMin <= sim.Now + 1e-6).ToList();
        double traffic = legs.Where(l => l.IncidentFactor > 1).Sum(l => l.ActualMin - l.PlannedMin);
        int trafficLegs = legs.Count(l => l.IncidentFactor > 1);
        double slow = legs.Where(l => l.IncidentFactor <= 1).Sum(l => Math.Max(0, l.ActualMin - l.PlannedMin));
        double fast = legs.Where(l => l.IncidentFactor <= 1).Sum(l => Math.Max(0, l.PlannedMin - l.ActualMin));
        var reasons = new List<string>();
        if (trafficLegs > 0) reasons.Add($"le tocaron {trafficLegs} tramo{(trafficLegs == 1 ? "" : "s")} con incidente (+{traffic:0} min)");
        if (slow - fast > 3) reasons.Add($"el tráfico normal estuvo más pesado de lo esperado (+{slow - fast:0} min netos)");
        var received = u.Received.Where(r => r.At <= sim.Now).ToList();
        if (received.Count > 0)
        {
            var groups = received.GroupBy(r => r.Why).Select(g => $"{g.Count()} {g.Key}");
            reasons.Add($"recibió {received.Count} entrega{(received.Count == 1 ? "" : "s")} extra ({string.Join("; ", groups)})");
        }
        if (sim.Problem.Travel.Incidents.Any(i => i.Until > sim.Now && i.From <= sim.Now && u.Status == UnitStatus.EnRuta && sim.Problem.Travel.IncidentFactor(u.From, u.To, sim.Now) > 1))
            reasons.Add("ahora mismo va atravesando una zona con incidente");
        if (reasons.Count == 0) reasons.Add("su ruta ya venía muy apretada en esa ventana de horario");
        sb.Append("Motivos: ").Append(string.Join("; ", reasons)).Append('.');

        var alt = sim.Predict().Where(e => e.VehicleId != u.Vehicle.Id && e.Risk == Risk.ATiempo)
            .GroupBy(e => e.VehicleId).Select(g => sim.Units[g.Key])
            .Where(x => x.Status is UnitStatus.EnRuta or UnitStatus.Entregando or UnitStatus.Esperando)
            .OrderBy(x => x.Position.HaversineKm(o.Location)).FirstOrDefault();
        if (alt != null && next.Risk == Risk.Tarde)
            sb.Append($"\nSugerencia: re-optimizar; {alt.Vehicle.Code} está a {alt.Position.HaversineKm(o.Location) * TravelModel.Circuity:0.0} km de ese cliente.");
        return sb.ToString();
    }

    double Drift(Unit u)
    {
        var legs = sim.LegsLog.Where(l => l.VehicleId == u.Vehicle.Id && l.Start + l.ActualMin <= sim.Now);
        return legs.Sum(l => l.ActualMin - l.PlannedMin);
    }

    string AtRisk()
    {
        var etas = sim.Predict().Where(e => e.Risk != Risk.ATiempo).OrderBy(e => e.Arrive).ToList();
        if (etas.Count == 0 && sim.Unassigned.Count == 0) return "Todas las entregas pendientes llegan dentro de su horario.";
        var sb = new StringBuilder();
        int late = etas.Count(e => e.Risk == Risk.Tarde), risk = etas.Count - late;
        sb.Append($"{late} llegarían tarde y {risk} van muy justas (menos de 15 min de margen)");
        if (sim.Unassigned.Count > 0) sb.Append($"; {sim.Unassigned.Count} no tienen unidad");
        sb.Append(":\n");
        foreach (var e in etas.Take(6))
        {
            var o = sim.Problem.Orders[e.OrderId];
            sb.Append($"• {o.Code} {o.Customer} ({sim.Units[e.VehicleId].Vehicle.Code}): llega {Clock.Hm(e.Arrive)}, cierra {Clock.Hm(o.Close)}{(e.Risk == Risk.Tarde ? $" → {Math.Round(e.LateMin)} min tarde" : "")}\n");
        }
        if (etas.Count > 6) sb.Append($"…y {etas.Count - 6} más.\n");
        if (late > 0) sb.Append("Puedes presionar «Re-optimizar» para que el sistema intente rescatarlas.");
        return sb.ToString().TrimEnd();
    }

    string Savings()
    {
        var k = kpis();
        return $"Contra el plan manual del día: {k.KmManual - k.KmPlanned:0} km menos ({Pct(k.KmManual - k.KmPlanned, k.KmManual)}), " +
               $"~{k.FuelSavedL:0} L de gasolina (${k.FuelSavedMxn:N0} MXN) y {k.Co2SavedKg:0} kg de CO₂ evitados, " +
               $"con {k.VansPlanned} unidades en vez de {k.VansManual}. El plan manual tenía {k.LateManual} entregas tarde; el optimizado, {k.LatePlanned}.";
    }

    string Overview()
    {
        var k = kpis();
        var etas = sim.Predict();
        int risk = etas.Count(e => e.Risk != Risk.ATiempo);
        var active = sim.Units.Count(u => u.Status is UnitStatus.EnRuta or UnitStatus.Entregando or UnitStatus.Esperando);
        return $"Son las {Clock.Hm(sim.Now)}. Entregadas {k.Delivered} de {k.Total} ({k.OnTimePct:0}% a tiempo). " +
               $"{active} unidades en la calle, {sim.Units.Count(u => u.Status == UnitStatus.Averiada)} descompuestas. " +
               (risk > 0 ? $"Hay {risk} entregas en riesgo." : "Nada en riesgo por ahora.") +
               $" Kilómetros recorridos: {k.KmDriven:0}.";
    }

    string DescribeUnit(Unit u)
    {
        var etas = sim.Predict().Where(e => e.VehicleId == u.Vehicle.Id).OrderBy(e => e.Arrive).ToList();
        string where = u.Status switch
        {
            UnitStatus.EnRuta when u.Target is int t => $"va hacia {sim.Problem.Orders[t].Code} · {sim.Problem.Orders[t].Customer}",
            UnitStatus.Entregando when u.Target is int t => $"está entregando en {sim.Problem.Orders[t].Customer}",
            UnitStatus.Esperando when u.Target is int t => $"espera a que abra {sim.Problem.Orders[t].Customer} ({Clock.Hm(sim.Problem.Orders[t].Open)})",
            UnitStatus.Regresando => "regresa al CEDIS",
            UnitStatus.Terminado => "ya terminó su ruta",
            UnitStatus.Averiada => "está descompuesta",
            _ => "está en el CEDIS",
        };
        var s = $"{u.Vehicle.Code} ({u.Vehicle.Driver}) {where}. Lleva {u.Done.Count} entregas y {u.Km:0} km; le quedan {u.Plan.Count}.";
        if (etas.Count > 0) s += $" Terminaría hacia las {Clock.Hm(etas[^1].Arrive)}.";
        int bad = etas.Count(e => e.Risk != Risk.ATiempo);
        if (bad > 0) s += $" {bad} de sus entregas están en riesgo.";
        return s;
    }

    string OrderStatus(Order o)
    {
        if (sim.Delivered.TryGetValue(o.Id, out var d))
            return $"{o.Code} · {o.Customer} se entregó a las {Clock.Hm(d.Start)} con {sim.Units[d.VehicleId].Vehicle.Code}{(d.LateMin > 0.5 ? $", {Math.Round(d.LateMin)} min tarde" : ", a tiempo")}.";
        if (sim.Unassigned.Contains(o.Id)) return $"{o.Code} · {o.Customer} todavía no tiene unidad asignada.";
        var e = sim.Predict().FirstOrDefault(x => x.OrderId == o.Id);
        var u = sim.Units[e.VehicleId];
        int pos = u.Plan.IndexOf(o.Id);
        return $"{o.Code} · {o.Customer} ({o.Area}) va en {u.Vehicle.Code} con {u.Vehicle.Driver}, es su parada {pos + 1} de {u.Plan.Count}. " +
               $"Llega aprox. a las {Clock.Hm(e.Arrive)} (ventana {Clock.Hm(o.Open)}–{Clock.Hm(o.Close)}){(e.Risk == Risk.Tarde ? " (llegaría tarde)" : e.Risk == Risk.EnRiesgo ? " (va justa)" : "")}.";
    }

    // ── interpretación de la pregunta ───────────────────────────────────

    Unit? MostDelayed()
    {
        var etas = sim.Predict().Where(e => e.Risk != Risk.ATiempo).ToList();
        if (etas.Count == 0) return null;
        int vid = etas.GroupBy(e => e.VehicleId).OrderByDescending(g => g.Sum(e => e.LateMin + (e.Risk == Risk.EnRiesgo ? 1 : 0))).First().Key;
        return sim.Units[vid];
    }

    Unit? UnitIn(string q)
    {
        var m = UnitRx().Match(q);
        if (!m.Success) return null;
        int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return sim.Units.FirstOrDefault(u => u.Vehicle.Id == n - 1);
    }

    Order? OrderIn(string q)
    {
        var m = OrderRx().Match(q);
        if (!m.Success) return null;
        int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return n >= 1 && n <= sim.Problem.Orders.Count ? sim.Problem.Orders[n - 1] : null;
    }

    // sin acentos ni mayúsculas; tabla propia porque en WebAssembly corre sin datos de idioma (globalización invariante)
    static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
            sb.Append(ch switch { 'á' or 'à' or 'ä' => 'a', 'é' or 'è' or 'ë' => 'e', 'í' or 'ì' or 'ï' => 'i', 'ó' or 'ò' or 'ö' => 'o', 'ú' or 'ù' or 'ü' => 'u', 'ñ' => 'n', '¿' or '¡' => ' ', _ => ch });
        return sb.ToString();
    }

    static string Pct(double part, double whole) => whole <= 0 ? "0%" : $"{part / whole * 100:0}%";

    [GeneratedRegex(@"\b(?:u-?|unidad\s*|camioneta\s*)0*(\d{1,2})\b")]
    private static partial Regex UnitRx();

    [GeneratedRegex(@"\b(?:p-?|pedido\s*)0*(\d{1,4})\b")]
    private static partial Regex OrderRx();
}
