namespace Vertice.Core;

/// <summary>
/// Tiempos de traslado dependientes de la hora: la misma calle no tarda lo mismo a las 7:00 que a las 18:30.
/// Distancia vial ≈ línea recta × factor de rodeo (calles reales nunca van en línea recta).
/// </summary>
public sealed class TravelModel
{
    /// <summary>En la CDMX una ruta por calles mide ~1.35 veces la línea recta.</summary>
    public const double Circuity = 1.35;

    // Velocidad promedio de reparto (km/h) por hora del día, CDMX en día hábil.
    static readonly (double FromHour, double Kmh)[] Profile =
    [
        (0, 34), (6, 30), (7, 19), (10, 25), (14, 21), (16, 17), (20, 27), (22, 32),
    ];

    readonly List<Incident> _incidents = [];

    public IReadOnlyList<Incident> Incidents => _incidents;

    public void AddIncident(Incident i) => _incidents.Add(i);

    // velocidad por minuto del día, precalculada (se consulta millones de veces al optimizar)
    static readonly double[] MinPerKm = BuildTable();

    static double[] BuildTable()
    {
        var t = new double[24 * 60];
        for (int m = 0; m < t.Length; m++)
        {
            double h = m / 60.0, v = Profile[0].Kmh;
            foreach (var (from, kmh) in Profile) if (h >= from) v = kmh;
            t[m] = 60 / v;
        }
        return t;
    }

    static int MinuteIndex(double minute)
    {
        int m = (int)minute % 1440;
        return m < 0 ? m + 1440 : m;
    }

    public static double SpeedKmh(double minute) => 60 / MinPerKm[MinuteIndex(minute)];

    public static double RoadKm(GeoPoint a, GeoPoint b) => a.HaversineKm(b) * Circuity;

    /// <summary>Factor de lentitud por incidentes activos que toca el tramo (se revisa el punto medio y los extremos).</summary>
    public double IncidentFactor(GeoPoint a, GeoPoint b, double minute)
    {
        if (_incidents.Count == 0) return 1;
        double f = 1;
        var mid = a.Lerp(b, 0.5);
        foreach (var i in _incidents)
        {
            if (minute < i.From || minute > i.Until) continue;
            if (mid.HaversineKm(i.Center) <= i.RadiusKm || a.HaversineKm(i.Center) <= i.RadiusKm || b.HaversineKm(i.Center) <= i.RadiusKm)
                f = Math.Max(f, i.Factor);
        }
        return f;
    }

    /// <summary>Minutos para recorrer <paramref name="km"/> saliendo a la hora <paramref name="depart"/>.</summary>
    public double Minutes(double km, GeoPoint a, GeoPoint b, double depart)
    {
        double min = km * MinPerKm[MinuteIndex(depart)];
        return _incidents.Count == 0 ? min : min * IncidentFactor(a, b, depart);
    }
}

/// <summary>
/// Los nodos del problema: 0 = CEDIS, 1..n = entregas. Guarda la matriz de distancias para no recalcular trigonometría.
/// </summary>
public sealed class Network
{
    readonly List<GeoPoint> _points = [];
    double[,] _km = new double[0, 0];

    public Network(Depot depot) { _points.Add(depot.Location); Rebuild(); }

    public int Count => _points.Count;
    public GeoPoint this[int node] => _points[node];

    public int Add(GeoPoint p)
    {
        _points.Add(p);
        Rebuild();
        return _points.Count - 1;
    }

    public double Km(int a, int b) => _km[a, b];

    void Rebuild()
    {
        int n = _points.Count;
        var km = new double[n, n];
        int old = _km.GetLength(0);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                km[i, j] = i < old && j < old ? _km[i, j] : i == j ? 0 : TravelModel.RoadKm(_points[i], _points[j]);
        _km = km;
    }
}
