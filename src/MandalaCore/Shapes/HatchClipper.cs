using System;
using System.Collections.Generic;
using MandalaMaker.Geom;

namespace MandalaMaker.Shapes;

/// <summary>Clips lines and rays to a closed outline (even-odd, so holes stay empty).</summary>
public sealed class HatchClipper
{
    private readonly List<(Point A, Point B)> _edges = new();

    public HatchClipper(IEnumerable<List<Point>> loops)
    {
        Bounds = Rect.Empty;
        foreach (var loop in loops)
        {
            if (loop.Count < 3) continue;
            for (int i = 1; i < loop.Count; i++) _edges.Add((loop[i - 1], loop[i]));
            if ((loop[0] - loop[^1]).Length > 1e-9) _edges.Add((loop[^1], loop[0]));
            foreach (var p in loop) Bounds = Rect.Union(Bounds, p);
        }
    }

    public Rect Bounds { get; }

    /// <summary>The inside pieces of the infinite line through origin along dir.</summary>
    public IEnumerable<(Point, Point)> Line(Point origin, Vector dir)
    {
        var ts = Crossings(origin, dir);
        for (int i = 0; i + 1 < ts.Count; i += 2)
            yield return (origin + dir * ts[i], origin + dir * ts[i + 1]);
    }

    /// <summary>From origin along dir until the ray first leaves the shape, or null if origin isn't inside.</summary>
    public (Point, Point)? Ray(Point origin, Vector dir)
    {
        var ts = Crossings(origin, dir);
        int before = ts.FindAll(t => t <= 1e-9).Count;
        if (before % 2 == 0) return null; // origin is outside
        foreach (double t in ts)
            if (t > 1e-9) return (origin, origin + dir * t);
        return null;
    }

    private List<double> Crossings(Point p, Vector d)
    {
        var ts = new List<double>();
        foreach (var (a, b) in _edges)
        {
            var e = b - a;
            double denom = Vector.CrossProduct(d, e);
            if (Math.Abs(denom) < 1e-12) continue;
            var ap = a - p;
            double t = Vector.CrossProduct(ap, e) / denom;
            double u = Vector.CrossProduct(ap, d) / denom;
            if (u >= 0 && u < 1) ts.Add(t);
        }
        ts.Sort();
        return ts;
    }
}
