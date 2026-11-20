namespace Vertice.Core;

/// <summary>
/// Genera un día de reparto creíble en la CDMX: un CEDIS en Vallejo, entregas repartidas por colonias reales
/// (coordenadas aproximadas, clientes ficticios) y ventanas de horario como las que piden los negocios.
/// Con la misma semilla siempre sale el mismo día (reproducible en pruebas y en la demo).
/// </summary>
public static class Scenario
{
    public static readonly Depot Cedis = new("CEDIS Vallejo", new GeoPoint(19.4905, -99.1630));

    // (colonia, alcaldía, lat, lon, peso relativo de pedidos)
    static readonly (string Area, double Lat, double Lon, double W)[] Areas =
    [
        ("Polanco, Miguel Hidalgo", 19.4330, -99.1950, 1.3), ("Anzures, Miguel Hidalgo", 19.4280, -99.1800, 0.8),
        ("Condesa, Cuauhtémoc", 19.4115, -99.1730, 1.2), ("Roma Norte, Cuauhtémoc", 19.4190, -99.1620, 1.3),
        ("Centro, Cuauhtémoc", 19.4326, -99.1332, 1.4), ("Doctores, Cuauhtémoc", 19.4180, -99.1450, 0.8),
        ("Escandón, Miguel Hidalgo", 19.4030, -99.1830, 0.7), ("Del Valle, Benito Juárez", 19.3880, -99.1680, 1.2),
        ("Narvarte, Benito Juárez", 19.3950, -99.1530, 1.0), ("Portales, Benito Juárez", 19.3700, -99.1450, 0.9),
        ("Mixcoac, Benito Juárez", 19.3750, -99.1870, 0.7), ("San Ángel, Álvaro Obregón", 19.3460, -99.1900, 0.7),
        ("Coyoacán Centro, Coyoacán", 19.3500, -99.1620, 1.0), ("Churubusco, Coyoacán", 19.3600, -99.1450, 0.6),
        ("Coapa, Tlalpan", 19.3010, -99.1250, 0.7), ("Tlalpan Centro, Tlalpan", 19.2900, -99.1680, 0.5),
        ("Iztapalapa Centro, Iztapalapa", 19.3570, -99.0930, 1.0), ("Agrícola Oriental, Iztacalco", 19.3950, -99.0700, 0.7),
        ("Iztacalco, Iztacalco", 19.3950, -99.0970, 0.6), ("Moctezuma, Venustiano Carranza", 19.4290, -99.1000, 0.7),
        ("Aragón, Gustavo A. Madero", 19.4700, -99.0750, 0.7), ("Lindavista, Gustavo A. Madero", 19.4880, -99.1290, 0.9),
        ("Peralvillo, Cuauhtémoc", 19.4560, -99.1250, 0.6), ("Tlatelolco, Cuauhtémoc", 19.4510, -99.1370, 0.6),
        ("Tacuba, Miguel Hidalgo", 19.4590, -99.1890, 0.7), ("Azcapotzalco Centro, Azcapotzalco", 19.4840, -99.1840, 0.8),
        ("Santa Fe, Cuajimalpa", 19.3600, -99.2600, 0.8), ("Tlalnepantla Centro, Edomex", 19.5400, -99.1950, 0.6),
        ("Satélite, Naucalpan", 19.5100, -99.2350, 0.7), ("Xochimilco Centro, Xochimilco", 19.2620, -99.1050, 0.4),
    ];

    static readonly string[] Kinds = ["Farmacia", "Abarrotes", "Papelería", "Ferretería", "Cafetería", "Consultorio", "Minisúper", "Restaurante", "Oficinas", "Clínica dental", "Panadería", "Óptica", "Veterinaria", "Tortillería", "Tienda naturista"];
    static readonly string[] Names = ["La Esperanza", "Don Chucho", "del Valle", "Hermanos Ruiz", "San Judas", "El Sol", "Lupita", "Los Arcos", "La Central", "Santa Fe", "El Buen Precio", "La Paloma", "Doña Mary", "El Roble", "La Estrella", "Médica Norte", "Los Pinos", "El Faro", "Las Flores", "Mi Tiendita", "La Güera", "El Ahorro", "San Rafael", "Del Parque", "Vida Sana"];
    static readonly string[] Drivers = ["Juan Pérez", "Luis Hernández", "Carlos Ramírez", "Miguel Torres", "Jorge Flores", "Ricardo Morales", "Alejandro Cruz", "Fernando Reyes", "Roberto Jiménez", "Eduardo Vargas", "Óscar Mendoza", "Raúl Castillo", "Arturo Ortiz", "Héctor Silva", "Sergio Romero", "Daniel Aguilar"];

    public static Problem Build(int seed = 2026, int orders = 150, int vehicles = 12)
    {
        var rng = new Random(seed);
        var fleet = Enumerable.Range(0, vehicles).Select(i => new Vehicle
        {
            Id = i,
            Code = $"U-{i + 1:00}",
            Driver = Drivers[i % Drivers.Length],
            CapacityKg = 1000,
            ShiftStart = 8 * 60,
            ShiftEnd = 18 * 60,
        });
        var p = new Problem(Cedis, fleet);
        for (int i = 0; i < orders; i++) p.Add(RandomOrder(rng, i, createdAt: 0, urgent: false, now: 0));
        return p;
    }

    /// <summary>Un pedido nuevo; los urgentes llegan durante el día con ventana corta a partir de <paramref name="now"/>.</summary>
    public static Order RandomOrder(Random rng, int id, double createdAt, bool urgent, double now)
    {
        double total = Areas.Sum(a => a.W), x = rng.NextDouble() * total;
        var area = Areas[^1];
        foreach (var a in Areas) { x -= a.W; if (x <= 0) { area = a; break; } }
        // dispersión ~0.9 km alrededor del centro de la colonia
        double lat = area.Lat + Gauss(rng) * 0.0075, lon = area.Lon + Gauss(rng) * 0.0080;

        double open, close;
        if (urgent) { open = now; close = now + 75 + rng.Next(0, 4) * 15; }
        else
        {
            double r = rng.NextDouble();
            if (r < 0.40) { open = 9 * 60; close = 18 * 60; }                    // todo el día
            else if (r < 0.70) { open = 9 * 60; close = 13 * 60; }               // en la mañana
            else if (r < 0.90) { open = 13 * 60; close = 18 * 60; }              // en la tarde
            else { open = (9 + rng.Next(0, 7)) * 60; close = open + 120; }       // ventana de 2 horas
        }
        return new Order
        {
            Id = id,
            Code = $"P-{id + 1:000}",
            Customer = $"{Kinds[rng.Next(Kinds.Length)]} {Names[rng.Next(Names.Length)]}",
            Area = area.Area,
            Location = new GeoPoint(Math.Round(lat, 5), Math.Round(lon, 5)),
            Kg = Math.Round(8 + rng.NextDouble() * 85, 1),
            ServiceMin = 6 + rng.Next(0, 10),
            Open = open,
            Close = close,
            Urgent = urgent,
            CreatedAt = createdAt,
        };
    }

    static double Gauss(Random rng)
    {
        double u1 = 1 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}

/// <summary>
/// El plan "a mano" contra el que se compara: así lo arma un despachador con experiencia sin software.
/// Reparte la ciudad en rebanadas alrededor del CEDIS (barrido por ángulo, parejo entre unidades) y en cada
/// rebanada atiende primero a los que cierran temprano, siempre yendo al cliente más cercano.
/// </summary>
public static class ManualPlanner
{
    public static Solution Plan(Problem p, IEnumerable<int> orderIds)
    {
        var depot = p.Depot.Location;
        var byAngle = orderIds.Select(id => p.Orders[id])
            .OrderBy(o => Math.Atan2(o.Location.Lat - depot.Lat, o.Location.Lon - depot.Lon)).ToList();
        var s = Solution.Empty(p, []);
        // como se hace a mano: ~15 paradas por camioneta (las que "le caben" a un chofer en su turno)
        int vans = Math.Clamp((int)Math.Ceiling(byAngle.Count / 15.0), 1, p.Vehicles.Count);
        int perVehicle = (int)Math.Ceiling(byAngle.Count / (double)vans);
        int ri = 0;
        double kg = 0;
        foreach (var o in byAngle)
        {
            var r = s.Routes[ri];
            if ((r.Stops.Count >= perVehicle || kg + o.Kg > r.CapacityKg) && ri < s.Routes.Count - 1) { ri++; kg = 0; r = s.Routes[ri]; }
            r.Stops.Add(o.Id);
            kg += o.Kg;
        }
        foreach (var r in s.Routes)
        {
            var morning = r.Stops.Where(id => p.Orders[id].Close <= 13 * 60).ToList();
            var rest = r.Stops.Where(id => p.Orders[id].Close > 13 * 60).ToList();
            var seq = new List<int>();
            int at = 0;
            foreach (var group in new[] { morning, rest })
                while (group.Count > 0)
                {
                    int next = group.MinBy(id => p.Net.Km(at, Problem.NodeOf(id)));
                    group.Remove(next);
                    seq.Add(next);
                    at = Problem.NodeOf(next);
                }
            r.Stops.Clear();
            r.Stops.AddRange(seq);
        }
        return s;
    }
}
