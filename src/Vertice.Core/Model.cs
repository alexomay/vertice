namespace Vertice.Core;

/// <summary>Punto geográfico (grados decimales).</summary>
public readonly record struct GeoPoint(double Lat, double Lon)
{
    const double EarthKm = 6371.0088;

    /// <summary>Distancia en línea recta (gran círculo), en km.</summary>
    public double HaversineKm(GeoPoint o)
    {
        double dLat = Rad(o.Lat - Lat), dLon = Rad(o.Lon - Lon);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(Lat)) * Math.Cos(Rad(o.Lat)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthKm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    public GeoPoint Lerp(GeoPoint to, double f) => new(Lat + (to.Lat - Lat) * f, Lon + (to.Lon - Lon) * f);

    static double Rad(double d) => d * Math.PI / 180;
}

/// <summary>Centro de distribución de donde salen y a donde regresan las unidades.</summary>
public sealed record Depot(string Name, GeoPoint Location);

/// <summary>Una entrega con su ventana de horario (minutos desde medianoche).</summary>
public sealed class Order
{
    public required int Id { get; init; }
    public required string Code { get; init; }
    public required string Customer { get; init; }
    public required string Area { get; init; }
    public required GeoPoint Location { get; init; }
    public required double Kg { get; init; }
    public required double ServiceMin { get; init; }
    public required double Open { get; init; }
    public required double Close { get; init; }
    public bool Urgent { get; init; }
    public double CreatedAt { get; init; }
}

/// <summary>Una unidad de reparto (camioneta) con su chofer y su turno.</summary>
public sealed class Vehicle
{
    public required int Id { get; init; }
    public required string Code { get; init; }
    public required string Driver { get; init; }
    public required double CapacityKg { get; init; }
    public required double ShiftStart { get; init; }
    public required double ShiftEnd { get; init; }
    public double CostPerKm { get; init; } = 4.6;   // gasolina + desgaste, MXN/km
    public double FixedCost { get; init; } = 650;   // costo de sacar la unidad un día (chofer, seguro)
}

/// <summary>Zona con tráfico anormal (choque, marcha, obra) durante un periodo.</summary>
public sealed record Incident(int Id, string Label, GeoPoint Center, double RadiusKm, double Factor, double From, double Until);

public static class Clock
{
    public static string Hm(double minutes)
    {
        int m = (int)Math.Round(minutes);
        return $"{m / 60:00}:{m % 60:00}";
    }
}
