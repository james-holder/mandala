using System;
using System.Collections.Generic;
using System.Globalization;
using Clipper2Lib;
using MandalaMaker.Shapes;

namespace MandalaMaker.Geom;

/// <summary>
/// Reads the SVG path data that <see cref="PathBuilder"/> writes (absolute M, L, C, A and Z commands) and turns it
/// into polylines. This replaces WPF's Geometry.Parse plus flattening, so the shape code can run in a browser.
/// </summary>
public static class PathData
{
    private enum Kind { Move, Line, Cubic, Arc, Close }

    private readonly record struct Command(Kind Kind, double[] Args);

    private static List<Command> Parse(string data)
    {
        var commands = new List<Command>();
        if (string.IsNullOrWhiteSpace(data)) return commands;
        int i = 0;
        char current = ' ';
        var numbers = new List<double>();

        double ReadNumber()
        {
            while (i < data.Length && (data[i] == ' ' || data[i] == ',')) i++;
            int start = i;
            if (i < data.Length && (data[i] == '-' || data[i] == '+')) i++;
            while (i < data.Length && (char.IsDigit(data[i]) || data[i] == '.')) i++;
            if (i < data.Length && (data[i] == 'e' || data[i] == 'E'))
            {
                i++;
                if (i < data.Length && (data[i] == '-' || data[i] == '+')) i++;
                while (i < data.Length && char.IsDigit(data[i])) i++;
            }
            return double.Parse(data.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        bool NumberNext()
        {
            int j = i;
            while (j < data.Length && (data[j] == ' ' || data[j] == ',')) j++;
            return j < data.Length && (char.IsDigit(data[j]) || data[j] == '-' || data[j] == '+' || data[j] == '.');
        }

        while (i < data.Length)
        {
            char c = data[i];
            if (char.IsLetter(c) && c != 'e' && c != 'E')
            {
                current = char.ToUpperInvariant(c);
                i++;
                if (current == 'Z') { commands.Add(new Command(Kind.Close, Array.Empty<double>())); continue; }
            }
            else if (c == ' ' || c == ',') { i++; continue; }

            int count = current switch { 'M' or 'L' => 2, 'C' => 6, 'A' => 7, _ => 0 };
            if (count == 0) { i++; continue; }
            while (NumberNext())
            {
                var args = new double[count];
                for (int k = 0; k < count; k++) args[k] = ReadNumber();
                var kind = current switch { 'M' => Kind.Move, 'L' => Kind.Line, 'C' => Kind.Cubic, _ => Kind.Arc };
                commands.Add(new Command(kind, args));
                if (current == 'M') current = 'L'; // extra pairs after a move are lines
            }
        }
        return commands;
    }

    /// <summary>
    /// Every subpath as a polyline, with curves flattened to within <paramref name="tolerance"/> mm.
    /// Closed subpaths end with a repeat of their first point (as WPF's flattening did).
    /// </summary>
    public static List<List<Point>> ToPolylines(string data, double tolerance)
    {
        var result = new List<List<Point>>();
        List<Point>? cur = null;
        Point start = default, pen = default;

        void Finish(bool closed)
        {
            if (cur == null) return;
            if (closed && cur.Count > 1 && (cur[0] - cur[^1]).Length > 1e-9) cur.Add(cur[0]);
            if (cur.Count > 1) result.Add(cur);
            cur = null;
        }

        foreach (var cmd in Parse(data))
        {
            var a = cmd.Args;
            switch (cmd.Kind)
            {
                case Kind.Move:
                    Finish(false);
                    pen = start = new Point(a[0], a[1]);
                    cur = new List<Point> { pen };
                    break;
                case Kind.Line:
                    cur ??= new List<Point> { pen };
                    pen = new Point(a[0], a[1]);
                    cur.Add(pen);
                    break;
                case Kind.Cubic:
                {
                    cur ??= new List<Point> { pen };
                    var p1 = new Point(a[0], a[1]); var p2 = new Point(a[2], a[3]); var p3 = new Point(a[4], a[5]);
                    double ctrl = (p1 - pen).Length + (p2 - p1).Length + (p3 - p2).Length;
                    int n = Math.Clamp((int)Math.Ceiling(Math.Sqrt(ctrl / Math.Max(tolerance, 1e-4)) * 0.6), 2, 256);
                    var p0 = pen;
                    for (int k = 1; k <= n; k++)
                    {
                        double t = (double)k / n, u = 1 - t;
                        cur.Add(new Point(
                            u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X,
                            u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y));
                    }
                    pen = p3;
                    break;
                }
                case Kind.Arc:
                {
                    cur ??= new List<Point> { pen };
                    var end = new Point(a[5], a[6]);
                    foreach (var p in FlattenArc(pen, end, a[0], a[3] != 0, a[4] != 0, tolerance)) cur.Add(p);
                    pen = end;
                    break;
                }
                case Kind.Close:
                    Finish(true);
                    pen = start;
                    break;
            }
        }
        Finish(false);
        return result;
    }

    /// <summary>A circular arc in SVG endpoint form (the only kind PathBuilder writes), as points after the start.</summary>
    private static IEnumerable<Point> FlattenArc(Point from, Point to, double r, bool large, bool sweep, double tolerance)
    {
        var d = to - from;
        double chord = d.Length;
        if (chord < 1e-9 || r <= 0) { yield return to; yield break; }
        r = Math.Max(r, chord / 2);
        // Center: on the perpendicular bisector, on the side picked by the flags
        double h = Math.Sqrt(Math.Max(r * r - chord * chord / 4, 0));
        var mid = new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2);
        var perp = new Vector(-d.Y / chord, d.X / chord);
        double side = large == sweep ? -1 : 1;
        var c = mid + perp * (h * side);
        double a0 = Math.Atan2(from.Y - c.Y, from.X - c.X), a1 = Math.Atan2(to.Y - c.Y, to.X - c.X);
        double delta = a1 - a0;
        if (sweep && delta < 0) delta += 2 * Math.PI;
        if (!sweep && delta > 0) delta -= 2 * Math.PI;
        double stepAngle = 2 * Math.Acos(Math.Max(-1, Math.Min(1, 1 - tolerance / r)));
        int n = Math.Clamp((int)Math.Ceiling(Math.Abs(delta) / Math.Max(stepAngle, 1e-3)), 2, 512);
        for (int k = 1; k <= n; k++)
        {
            double ang = a0 + delta * k / n;
            yield return new Point(c.X + r * Math.Cos(ang), c.Y + r * Math.Sin(ang));
        }
    }

    /// <summary>
    /// Re-draws stored path data through a path builder, so its current rotation and position apply.
    /// Used to stamp a decoration worked out once onto every copy around a ring.
    /// </summary>
    public static void Emit(PathBuilder pb, string data)
    {
        Point pen = default;
        foreach (var cmd in Parse(data))
        {
            var a = cmd.Args;
            switch (cmd.Kind)
            {
                case Kind.Move: pb.MoveTo(a[0], a[1]); pen = new Point(a[0], a[1]); break;
                case Kind.Line: pb.LineTo(a[0], a[1]); pen = new Point(a[0], a[1]); break;
                case Kind.Cubic: pb.CubicTo(a[0], a[1], a[2], a[3], a[4], a[5]); pen = new Point(a[4], a[5]); break;
                case Kind.Arc: pb.ArcTo(a[0], a[3] != 0, a[4] != 0, a[5], a[6]); pen = new Point(a[5], a[6]); break;
                case Kind.Close: pb.Close(); break;
            }
        }
    }

    /// <summary>Merges overlapping closed outlines into one clean outline (nonzero fill, so holes survive).</summary>
    public static List<List<Point>> Union(IEnumerable<List<Point>> loops)
    {
        var paths = new PathsD();
        foreach (var loop in loops)
        {
            if (loop.Count < 3) continue;
            var path = new PathD(loop.Count);
            foreach (var p in loop) path.Add(new PointD(p.X, p.Y));
            // Each part is a solid shape. Mirrored parts are drawn the other way round, and with the nonzero rule
            // an overlap of opposite directions would cancel into a hole, so point every part the same way first.
            if (!Clipper.IsPositive(path)) path.Reverse();
            paths.Add(path);
        }
        var merged = Clipper.BooleanOp(ClipType.Union, paths, new PathsD(), FillRule.NonZero, 4); // 4 decimal places = 0.1 µm
        // Where two parts nearly touch, the merge can leave a hairline sliver (a tiny hole or island).
        // Drop anything negligible next to the main outline so there's nothing too small to color.
        double largest = 0;
        foreach (var path in merged) largest = Math.Max(largest, Math.Abs(Clipper.Area(path)));
        var result = new List<List<Point>>();
        foreach (var path in merged)
        {
            if (Math.Abs(Clipper.Area(path)) < Math.Max(0.05, largest * 0.004)) continue;
            var loop = new List<Point>(path.Count + 1);
            foreach (var p in path) loop.Add(new Point(p.x, p.y));
            if (loop.Count > 0) loop.Add(loop[0]);
            result.Add(loop);
        }
        return result;
    }
}
