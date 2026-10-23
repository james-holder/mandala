using System;
using System.Collections.Generic;
using System.Linq;
using MandalaMaker.Geom;
using Loops = System.Collections.Generic.List<System.Collections.Generic.List<MandalaMaker.Geom.Point>>;
using MandalaMaker.Model;

namespace MandalaMaker.Shapes;

/// <summary>
/// Turns a <see cref="Layer"/> into SVG path data in millimeters, centered on (0,0), y-down.
/// Motifs are drawn in local coordinates where +x points outward from the center and +y runs along the ring.
/// </summary>
public static class ShapeGenerator
{
    public static bool IsRingShape(ShapeKind k) =>
        k is ShapeKind.Ring or ShapeKind.Scallop or ShapeKind.Zigzag or ShapeKind.Wave or ShapeKind.Polygon
            or ShapeKind.Sunburst or ShapeKind.Gear or ShapeKind.Lattice
            or ShapeKind.Vine or ShapeKind.Rope or ShapeKind.GreekKey
            or ShapeKind.Waves or ShapeKind.Chain or ShapeKind.Stitch or ShapeKind.FlowerOfLife or ShapeKind.SwirlBand;

    /// <summary>The layer's shapes: closed outlines plus any open lines drawn as part of them.</summary>
    public static string BuildPathData(Layer layer) => BuildBody(layer).ToString();

    /// <summary>
    /// The layer's shapes split into the closed outlines (which may be filled) and the open lines inside them
    /// (stripes, ribs, veins), which must only be stroked.
    /// </summary>
    public static (string Closed, string Open) BuildPathParts(Layer layer)
    {
        var pb = BuildBody(layer);
        return (pb.ClosedData, pb.OpenData);
    }

    private static PathBuilder BuildBody(Layer layer)
    {
        var pb = new PathBuilder();
        var (n, start, step) = Placement(layer);

        if (IsRingShape(layer.Shape))
            BuildRing(pb, layer, n, start, step, layer.Radius);
        else
            ForEachCopy(pb, layer, n, start, step, () => BuildSingleMotif(pb, layer, layer.Length, layer.Width));
        return pb;
    }

    /// <summary>
    /// The layer's detail line work: echo outlines, inner shapes and hatching on every copy.
    /// It's kept separate from the main outlines so it's never filled and laser merging leaves it alone.
    /// </summary>
    public static string BuildDetailData(Layer layer)
    {
        var pb = new PathBuilder();
        var (n, start, step) = Placement(layer);

        if (IsRingShape(layer.Shape))
        {
            // Rings: their own line work (never filled), then concentric echoes stepping inward
            BuildRing(new PathBuilder(), layer, n, start, step, layer.Radius, lineWork: pb);
            double gap = layer.InsetGap * Math.Max(layer.Length, 8);
            for (int k = 1; k <= layer.InsetCount; k++)
            {
                double r = layer.Radius - k * gap;
                if (r <= 0.1) break;
                if (DecorFor(layer, k) is EchoAccent decor)
                {
                    var echo = new PathBuilder();
                    BuildRing(echo, layer, n, start, step, r);
                    double rd = Math.Clamp(gap * 0.28, 0.3, 1.1);
                    // Decorations keep clear of the line outside them, such as where neighboring scallops meet
                    var outside = new PathBuilder();
                    BuildRing(outside, layer, n, start, step, layer.Radius - (k - 1) * gap);
                    var neighbors = PathData.ToPolylines(outside.ClosedData, 0.05);
                    DecorateAlong(pb, echo.ClosedData, decor, rd, neighbors);
                }
                else
                {
                    BuildRing(pb, layer, n, start, step, r, lineWork: pb);
                    if (k == 1 && layer.EchoStyle == EchoStyle.Fringe)
                    {
                        // Slanted ticks from the ring's outline in to its first echo
                        var outline = new PathBuilder();
                        BuildRing(outline, layer, n, start, step, layer.Radius);
                        var pts = OuterLoopPoints(outline.ClosedData, Math.Max(4.0, gap * 1.6) / 2);
                        var ticks = new List<(Point, Point)>();
                        for (int j = 0; j + 1 < pts.Count; j += 2)
                        {
                            var q = pts[j + 1];
                            double len = Math.Sqrt(q.X * q.X + q.Y * q.Y); // distance from the ring center
                            if (len < gap * 1.5) continue;
                            double f = (len - gap) / len;
                            ticks.Add((pts[j], new Point(q.X * f, q.Y * f)));
                        }
                        foreach (var (a, b) in DropShortTicks(ticks)) { pb.MoveTo(a.X, a.Y); pb.LineTo(b.X, b.Y); }
                    }
                }
            }
            return pb.ToString();
        }

        if (layer.InsetCount <= 0 && layer.Inner == InnerMotif.None && layer.Hatch == HatchStyle.None)
            return "";

        var hatch = layer.Hatch == HatchStyle.None ? null : HatchSegments(layer);
        var decorated = DecoratedEchoes(layer);
        var fringe = FringeTicks(layer);
        ForEachCopy(pb, layer, n, start, step, () => BuildSingleDetail(pb, layer, hatch, decorated, fringe));
        return pb.ToString();
    }

    /// <summary>
    /// The decoration for echo number k (1 = outermost), or null for a plain solid echo.
    /// Mixed keeps the first echo solid and decorates the rest with the Echo accent; a lone Mixed echo is decorated.
    /// </summary>
    private static EchoAccent? DecorFor(Layer layer, int k) => layer.EchoStyle switch
    {
        EchoStyle.Dots => EchoAccent.Dots,
        EchoStyle.Dashes => EchoAccent.Dashes,
        EchoStyle.Diamonds => EchoAccent.Diamonds,
        EchoStyle.Leaves => EchoAccent.Leaves,
        EchoStyle.Scallops => EchoAccent.Scallops,
        EchoStyle.Zigzag => EchoAccent.Zigzag,
        EchoStyle.Mixed when k > 1 || layer.InsetCount == 1 => layer.EchoAccent,
        _ => null,
    };

    /// <summary>
    /// For each decorated echo of a motif, the decoration in the motif's local frame.
    /// It's worked out once per layer, then stamped onto every copy.
    /// </summary>
    private static Dictionary<int, string>? DecoratedEchoes(Layer layer)
    {
        if (layer.InsetCount <= 0 || layer.EchoStyle is EchoStyle.Lines or EchoStyle.Fringe) return null;
        var result = new Dictionary<int, string>();
        double rd = Math.Clamp(0.03 * Math.Min(layer.Length, layer.Width), 0.3, 1.1);

        // Outline polylines at a given echo level (1 = the shape itself), for clearance checks
        List<List<Point>> Outline(double scale)
        {
            var p = new PathBuilder();
            BuildSingleMotif(p, layer, layer.Length * scale, layer.Width * scale);
            return string.IsNullOrWhiteSpace(p.ClosedData)
                ? new List<List<Point>>()
                : PathData.ToPolylines(p.ClosedData, 0.03);
        }

        for (int k = 1; k <= layer.InsetCount; k++)
        {
            double s = 1 - k * layer.InsetGap;
            if (s <= 0.05) break;
            if (DecorFor(layer, k) is not EchoAccent decor) continue;
            var echo = new PathBuilder();
            BuildSingleMotif(echo, layer, layer.Length * s, layer.Width * s);

            // Where the shape narrows, an echo crowds up against its neighbors: decorations keep clear of
            // the line outside them (the outline or the previous echo) and the echo inside them.
            var neighbors = Outline(1 - (k - 1) * layer.InsetGap);
            double inner = 1 - (k + 1) * layer.InsetGap;
            if (k < layer.InsetCount && inner > 0.05) neighbors.AddRange(Outline(inner));

            var local = new PathBuilder();
            DecorateAlong(local, echo.ClosedData, decor, rd, neighbors);
            string data = local.ToString();
            if (string.IsNullOrWhiteSpace(data)) continue;
            result[k] = data;
        }
        return result;
    }

    /// <summary>
    /// Draws a decoration along every closed outline in <paramref name="echoData"/>: dots, dashes, diamonds, leaves,
    /// a scalloped line or a zigzag. Small separate pieces are left out where they'd touch a neighboring line,
    /// and the scallops and zigzag flatten out where the space narrows, so nothing collides.
    /// </summary>
    private static void DecorateAlong(PathBuilder pb, string echoData, EchoAccent decor, double rd, List<List<Point>> neighbors)
    {
        if (string.IsNullOrWhiteSpace(echoData)) return;
        double spacing = decor switch
        {
            EchoAccent.Dots => rd * 3.6,
            EchoAccent.Diamonds => rd * 4.2,
            EchoAccent.Leaves => rd * 4.0,
            EchoAccent.Dashes => rd * 3.8,
            EchoAccent.Scallops => rd * 4.5,
            _ => rd * 2.6, // zigzag: one point per half wave
        };

        foreach (var loop in PathData.ToPolylines(echoData, 0.03))
        {
            var pts = ResampleLoop(loop, spacing);
            int count = pts.Count;
            if (count < 3) continue;
            // A zigzag needs an even number of points to close up neatly
            if (decor == EchoAccent.Zigzag && count % 2 == 1)
            {
                pts = ResampleLoop(loop, spacing * count / (count + 1.0));
                count = pts.Count;
                if (count % 2 == 1) { pts.RemoveAt(count - 1); count--; }
            }

            double cx = 0, cy = 0;
            foreach (var p in pts) { cx += p.X; cy += p.Y; }
            var center = new Point(cx / count, cy / count);

            var tangents = new Vector[count];
            var normals = new Vector[count];
            var room = new double[count];
            for (int i = 0; i < count; i++)
            {
                var t = pts[(i + 1) % count] - pts[(i - 1 + count) % count];
                if (t.Length < 1e-9) t = new Vector(1, 0);
                t.Normalize();
                var nrm = new Vector(-t.Y, t.X);
                if (Vector.Multiply(nrm, pts[i] - center) < 0) nrm = -nrm; // point away from the shape's middle
                tangents[i] = t;
                normals[i] = nrm;
                room[i] = neighbors.Count == 0 ? double.MaxValue : MinDistance(pts[i], neighbors);
            }

            switch (decor)
            {
                case EchoAccent.Dots:
                    for (int i = 0; i < count; i++)
                        if (room[i] >= rd * 1.9) pb.Ellipse(pts[i].X, pts[i].Y, rd, rd);
                    break;

                case EchoAccent.Diamonds:
                    for (int i = 0; i < count; i++)
                    {
                        if (room[i] < rd * 1.9) continue;
                        var p = pts[i];
                        var t = tangents[i] * (rd * 1.4);
                        var nn = normals[i] * (rd * 0.8);
                        pb.MoveTo((p + t).X, (p + t).Y);
                        pb.LineTo((p + nn).X, (p + nn).Y);
                        pb.LineTo((p - t).X, (p - t).Y);
                        pb.LineTo((p - nn).X, (p - nn).Y);
                        pb.Close();
                    }
                    break;

                case EchoAccent.Leaves:
                    for (int i = 0; i < count; i++)
                    {
                        if (room[i] < rd * 2.0) continue;
                        // Alternate the tilt so the leaves read as a garland
                        double tilt = (i % 2 == 0 ? 1 : -1) * 35 * Math.PI / 180;
                        var t = tangents[i];
                        var dir = new Vector(t.X * Math.Cos(tilt) - t.Y * Math.Sin(tilt), t.X * Math.Sin(tilt) + t.Y * Math.Cos(tilt));
                        var side = new Vector(-dir.Y, dir.X);
                        double half = rd * 1.7, w = rd * 0.75;
                        var a = pts[i] - dir * half;
                        var b = pts[i] + dir * half;
                        var a1 = a + dir * half * 0.6;
                        var b1 = b - dir * half * 0.6;
                        pb.MoveTo(a.X, a.Y);
                        pb.CubicTo((a1 + side * w).X, (a1 + side * w).Y, (b1 + side * w).X, (b1 + side * w).Y, b.X, b.Y);
                        pb.CubicTo((b1 - side * w).X, (b1 - side * w).Y, (a1 - side * w).X, (a1 - side * w).Y, a.X, a.Y);
                        pb.Close();
                    }
                    break;

                case EchoAccent.Dashes:
                    for (int i = 0; i < count; i++)
                    {
                        if (room[i] < rd * 1.2) continue;
                        var a = pts[i] - tangents[i] * (rd * 1.1);
                        var b = pts[i] + tangents[i] * (rd * 1.1);
                        pb.MoveTo(a.X, a.Y);
                        pb.LineTo(b.X, b.Y);
                    }
                    break;

                case EchoAccent.Scallops:
                {
                    // A bump between each pair of points, bulging outward and flattening where space is tight
                    pb.MoveTo(pts[0].X, pts[0].Y);
                    for (int i = 0; i < count; i++)
                    {
                        int j = (i + 1) % count;
                        double h = Math.Min(rd * 1.4, 0.45 * Math.Min(room[i], room[j])) * 1.33;
                        var p0 = pts[i];
                        var p1 = pts[j];
                        var d = p1 - p0;
                        var c1 = p0 + d / 3 + normals[i] * h;
                        var c2 = p0 + d * 2 / 3 + normals[j] * h;
                        pb.CubicTo(c1.X, c1.Y, c2.X, c2.Y, p1.X, p1.Y);
                    }
                    pb.Close();
                    break;
                }

                case EchoAccent.Zigzag:
                {
                    for (int i = 0; i < count; i++)
                    {
                        double amp = Math.Min(rd * 1.1, 0.4 * room[i]) * (i % 2 == 0 ? 1 : -1);
                        var p = pts[i] + normals[i] * amp;
                        if (i == 0) pb.MoveTo(p.X, p.Y); else pb.LineTo(p.X, p.Y);
                    }
                    pb.Close();
                    break;
                }
            }
        }
    }

    /// <summary>Evenly spaced points around one closed polyline, about <paramref name="spacing"/> apart.</summary>
    private static List<Point> ResampleLoop(List<Point> loop, double spacing)
    {
        var data = new PathBuilder();
        data.MoveTo(loop[0].X, loop[0].Y);
        for (int i = 1; i < loop.Count; i++) data.LineTo(loop[i].X, loop[i].Y);
        data.Close();
        return DotsAlong(data.ClosedData, spacing);
    }

    /// <summary>
    /// Fringe ticks for a motif in its local frame: short slanted lines from the outline to the first echo
    /// (the echo is the same shape scaled about its middle, so each outline point maps to point × scale).
    /// Spaced at least 4 mm (and about 1.6× the band width) so the cells between ticks can still be colored.
    /// </summary>
    private static List<(Point A, Point B)>? FringeTicks(Layer layer)
    {
        if (layer.InsetCount <= 0 || layer.EchoStyle != EchoStyle.Fringe) return null;
        double s = 1 - layer.InsetGap;
        if (s <= 0.05) return null;
        var body = new PathBuilder();
        BuildSingleMotif(body, layer, layer.Length, layer.Width);
        // The cells between ticks must be big enough to color: at least 4 mm apart, and never much closer than the band is wide
        double band = layer.InsetGap * Math.Min(layer.Length, layer.Width) / 2;
        double spacing = Math.Max(4.0, Math.Max(0.12 * Math.Min(layer.Length, layer.Width), band * 1.6));
        var pts = OuterLoopPoints(body.ClosedData, spacing / 2);
        var ticks = new List<(Point, Point)>();
        // Each tick runs from one point to the scaled position of the next, which gives the slant
        for (int j = 0; j + 1 < pts.Count; j += 2)
            ticks.Add((pts[j], new Point(pts[j + 1].X * s, pts[j + 1].Y * s)));
        return DropShortTicks(ticks);
    }

    /// <summary>Evenly spaced points along the largest closed outline only (inner holes and details are ignored).</summary>
    private static List<Point> OuterLoopPoints(string closedData, double spacing)
    {
        if (string.IsNullOrWhiteSpace(closedData)) return new List<Point>();
        var loops = PathData.ToPolylines(closedData, 0.03);
        if (loops.Count == 0) return new List<Point>();
        var outer = loops.OrderByDescending(l => { var r = Rect.Empty; foreach (var p in l) r.Union(p); return r.Width * r.Height; }).First();
        var data = new PathBuilder();
        data.MoveTo(outer[0].X, outer[0].Y);
        for (int i = 1; i < outer.Count; i++) data.LineTo(outer[i].X, outer[i].Y);
        data.Close();
        return DotsAlong(data.ClosedData, spacing);
    }

    /// <summary>
    /// Where the band between outline and echo pinches (near the base of a petal), ticks get stubby and crowd together.
    /// Drop any that are much shorter than is typical.
    /// </summary>
    private static List<(Point A, Point B)> DropShortTicks(List<(Point A, Point B)> ticks)
    {
        if (ticks.Count == 0) return ticks;
        var lengths = ticks.Select(t => (t.B - t.A).Length).OrderBy(x => x).ToList();
        double median = lengths[lengths.Count / 2];
        return ticks.FindAll(t => (t.B - t.A).Length >= Math.Max(0.5, median * 0.45));
    }

    /// <summary>Shortest distance from a point to any segment of the polylines.</summary>
    public static double MinDistance(Point p, List<List<Point>> lines)
    {
        double best = double.MaxValue;
        foreach (var line in lines)
            for (int i = 1; i < line.Count; i++)
            {
                var a = line[i - 1];
                var ab = line[i] - a;
                double len2 = ab.LengthSquared;
                double t = len2 > 0 ? Math.Clamp(Vector.Multiply(p - a, ab) / len2, 0, 1) : 0;
                best = Math.Min(best, (a + ab * t - p).Length);
            }
        return best;
    }

    /// <summary>Evenly spaced points along every closed outline in the path data (at most the given spacing apart).</summary>
    private static List<Point> DotsAlong(string closedData, double spacing)
    {
        var points = new List<Point>();
        if (string.IsNullOrWhiteSpace(closedData) || spacing <= 0) return points;
        foreach (var loop in PathData.ToPolylines(closedData, 0.03))
        {
            double total = 0;
            for (int i = 1; i < loop.Count; i++) total += (loop[i] - loop[i - 1]).Length;
            int count = Math.Max(3, (int)Math.Floor(total / spacing));
            double each = total / count, walked = 0, target = 0;
            int seg = 1;
            for (int d = 0; d < count; d++, target += each)
            {
                while (seg < loop.Count - 1 && walked + (loop[seg] - loop[seg - 1]).Length < target)
                {
                    walked += (loop[seg] - loop[seg - 1]).Length;
                    seg++;
                }
                double len = (loop[seg] - loop[seg - 1]).Length;
                double f = len > 0 ? (target - walked) / len : 0;
                points.Add(loop[seg - 1] + (loop[seg] - loop[seg - 1]) * Math.Clamp(f, 0, 1));
            }
        }
        return points;
    }

    /// <summary>One copy of the layer's motif in its own local frame (+x outward), with no ring placement.</summary>
    public static string BuildMotifLocal(Layer layer)
    {
        var pb = new PathBuilder();
        BuildSingleMotif(pb, layer, layer.Length, layer.Width);
        return pb.ToString();
    }

    /// <summary>One copy of the motif in its local frame, split into closed outlines and open lines.</summary>
    public static (string Closed, string Open) BuildMotifLocalParts(Layer layer)
    {
        var pb = new PathBuilder();
        BuildSingleMotif(pb, layer, layer.Length, layer.Width);
        return (pb.ClosedData, pb.OpenData);
    }

    /// <summary>Detail line work for one copy in its local frame (for the shape editor).</summary>
    public static string BuildDetailLocal(Layer layer)
    {
        if (IsRingShape(layer.Shape)) return "";
        var pb = new PathBuilder();
        BuildSingleDetail(pb, layer, layer.Hatch == HatchStyle.None ? null : HatchSegments(layer), DecoratedEchoes(layer), FringeTicks(layer));
        return pb.ToString();
    }


    private static (int N, double Start, double Step) Placement(Layer layer)
    {
        int n = Math.Clamp(layer.Count, 1, 720);
        double step = 2 * Math.PI / n;
        // Start at 12 o'clock
        double start = Deg(layer.RingRotation - 90) + (layer.OffsetHalfStep ? step / 2 : 0);
        return (n, start, step);
    }

    private static void ForEachCopy(PathBuilder pb, Layer layer, int n, double start, double step, Action draw)
    {
        double spin = Deg(layer.Spin);
        for (int i = 0; i < n; i++)
        {
            double th = start + i * step;
            pb.SetTransform(th + spin, layer.Radius * Math.Cos(th), layer.Radius * Math.Sin(th));
            draw();
        }
    }

    private static void BuildSingleMotif(PathBuilder pb, Layer layer, double length, double width)
    {
        if (layer.Shape == ShapeKind.Custom) CustomMotif(pb, layer, length, width);
        else BuildMotif(pb, layer.Shape, length, width, layer.Tweak);
    }

    private static void BuildSingleDetail(PathBuilder pb, Layer layer, List<(Point A, Point B)>? hatch,
        Dictionary<int, string>? decorated,
        List<(Point A, Point B)>? fringe = null)
    {
        if (fringe != null)
            foreach (var (a, b) in fringe) { pb.MoveTo(a.X, a.Y); pb.LineTo(b.X, b.Y); }

        double L = layer.Length, W = layer.Width;

        // Echo outlines: the same shape, scaled down about its middle, drawn as a line or as a row of dots
        for (int k = 1; k <= layer.InsetCount; k++)
        {
            double s = 1 - k * layer.InsetGap;
            if (s <= 0.05) break;
            if (decorated != null && decorated.TryGetValue(k, out var decoration))
                PathData.Emit(pb, decoration);
            else
                BuildSingleMotif(pb, layer, L * s, W * s);
        }

        if (layer.Inner != InnerMotif.None)
        {
            double f = Math.Clamp(layer.InnerSize, 0.02, 1.5);
            double sq = Math.Min(L, W) * f;            // square-ish inner shapes
            double extent = layer.Inner is InnerMotif.Petal or InnerMotif.Teardrop or InnerMotif.TwoOvals or InnerMotif.ThreeOvals
                ? L * f : sq;
            double x = Math.Clamp(layer.InnerOffset, -1, 1) * Math.Max(L / 2 - extent / 2, 0);
            pb.WithOffset(x, 0, () => InnerShape(pb, layer.Inner, L * f, W * f, sq));
        }

        if (hatch != null)
            foreach (var (a, b) in hatch)
            {
                pb.MoveTo(a.X, a.Y);
                pb.LineTo(b.X, b.Y);
            }
    }

    private static void InnerShape(PathBuilder pb, InnerMotif inner, double len, double wid, double sq)
    {
        switch (inner)
        {
            case InnerMotif.Circle: pb.Ellipse(0, 0, sq / 2, sq / 2); break;
            case InnerMotif.Bullseye: pb.Ellipse(0, 0, sq / 2, sq / 2); pb.Ellipse(0, 0, sq / 4, sq / 4); break;
            case InnerMotif.Star: BuildMotif(pb, ShapeKind.Star, sq, sq, 0.4); break;
            case InnerMotif.Triangle: BuildMotif(pb, ShapeKind.Triangle, sq, sq, 0.5); break;
            case InnerMotif.Diamond: BuildMotif(pb, ShapeKind.Diamond, sq * 1.3, sq * 0.8, 0.5); break;
            case InnerMotif.Heart: BuildMotif(pb, ShapeKind.Heart, sq, sq, 0.5); break;
            case InnerMotif.Petal: BuildMotif(pb, ShapeKind.Petal, len, wid, 0.5); break;
            case InnerMotif.Teardrop: BuildMotif(pb, ShapeKind.Teardrop, len, wid, 0.5); break;
            case InnerMotif.TwoOvals:
                pb.Ellipse(0, -wid * 0.22, len / 2, wid / 6);
                pb.Ellipse(0, wid * 0.22, len / 2, wid / 6);
                break;
            case InnerMotif.ThreeOvals:
                for (int k = -1; k <= 1; k++) pb.Ellipse(0, k * wid * 0.3, len / 2.4, wid / 9);
                break;
        }
    }

    /// <summary>Hatch lines for one copy in local coordinates, clipped to the motif's outline.</summary>
    private static List<(Point A, Point B)> HatchSegments(Layer layer)
    {
        var result = new List<(Point, Point)>();
        // Clip against the closed outline only; interior lines like leaf veins aren't edges
        var local = new PathBuilder();
        BuildSingleMotif(local, layer, layer.Length, layer.Width);
        string body = local.ClosedData;
        if (string.IsNullOrWhiteSpace(body)) return result;
        var loops = PathData.ToPolylines(body, 0.05);
        var clip = new HatchClipper(loops);
        if (clip.Bounds.IsEmpty) return result;

        var bb = clip.Bounds;
        int n = Math.Clamp(layer.HatchCount, 1, 80);
        var X = new Vector(1, 0);
        var Y = new Vector(0, 1);

        void Parallel(Vector dir)
        {
            // Lines with direction dir, spread evenly across the shape's width in the perpendicular direction
            var normal = new Vector(-dir.Y, dir.X);
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var c in new[] { bb.TopLeft, bb.TopRight, bb.BottomLeft, bb.BottomRight })
            {
                double d = c.X * normal.X + c.Y * normal.Y;
                lo = Math.Min(lo, d); hi = Math.Max(hi, d);
            }
            for (int i = 1; i <= n; i++)
            {
                double d = lo + (hi - lo) * i / (n + 1);
                var origin = new Point(normal.X * d, normal.Y * d);
                result.AddRange(clip.Line(origin, dir));
            }
        }

        switch (layer.Hatch)
        {
            case HatchStyle.Across: Parallel(Y); break;
            case HatchStyle.Along: Parallel(X); break;
            case HatchStyle.Cross: Parallel(X); Parallel(Y); break;
            case HatchStyle.Diagonal:
            {
                var d = new Vector(1, 1);
                d.Normalize();
                Parallel(d);
                break;
            }
            case HatchStyle.Chevron:
            {
                result.AddRange(clip.Line(new Point(0, 0), X)); // the center vein
                double angle = Deg(50);
                var up = new Vector(Math.Cos(angle), -Math.Sin(angle));
                var down = new Vector(Math.Cos(angle), Math.Sin(angle));
                for (int i = 0; i < n; i++)
                {
                    var o = new Point(bb.Left + bb.Width * (i + 0.6) / (n + 0.6), 0);
                    if (clip.Ray(o, up) is { } r1) result.Add(r1);
                    if (clip.Ray(o, down) is { } r2) result.Add(r2);
                }
                break;
            }
            case HatchStyle.Fan:
            {
                var origin = new Point(bb.Left + bb.Width * 0.02, 0);
                for (int i = 0; i < n; i++)
                {
                    double ang = n == 1 ? 0 : Deg(-55 + 110.0 * i / (n - 1));
                    if (clip.Ray(origin, new Vector(Math.Cos(ang), Math.Sin(ang))) is { } r) result.Add(r);
                }
                break;
            }
        }
        return result;
    }

    /// <summary>The custom outline in normalized motif space, with the mirror half added if mirroring is on.</summary>
    public static List<ShapeNode> CustomOutline(Layer layer)
    {
        var pts = new List<ShapeNode>(layer.CustomNodes);
        if (layer.CustomMirror && pts.Count > 0)
        {
            // Mirror across the radial axis (y = 0). Skip end points that already sit on the axis so they aren't doubled.
            const double onAxis = 0.02;
            for (int i = pts.Count - 1; i >= 0; i--)
            {
                var p = layer.CustomNodes[i];
                bool isEnd = i == 0 || i == layer.CustomNodes.Count - 1;
                if (isEnd && Math.Abs(p.Y) < onAxis) continue;
                pts.Add(p with { Y = -p.Y });
            }
        }
        return pts;
    }

    private static void CustomMotif(PathBuilder pb, Layer layer, double length, double width)
    {
        double a = Math.Max(length, 0.01) / 2, b = Math.Max(width, 0.01) / 2;
        var nodes = CustomOutline(layer);
        if (nodes.Count < 2) return;
        var pts = nodes.ConvertAll(n => (X: n.X * a, Y: n.Y * b, n.Sharp));
        // A mirrored outline only closes properly when it ends back on the axis, so mirroring always closes it
        bool closed = layer.CustomClosed || layer.CustomMirror;
        int count = pts.Count;

        pb.MoveTo(pts[0].X, pts[0].Y);
        int segments = closed ? count : count - 1;
        for (int i = 0; i < segments; i++)
        {
            var p1 = pts[i];
            var p2 = pts[(i + 1) % count];
            if (!layer.CustomSmooth || count < 3)
            {
                pb.LineTo(p2.X, p2.Y);
                continue;
            }
            // Catmull-Rom spline through the points, written as cubic Béziers. Sharp points get no tangent, so they stay corners.
            var p0 = closed ? pts[(i - 1 + count) % count] : pts[Math.Max(i - 1, 0)];
            var p3 = closed ? pts[(i + 2) % count] : pts[Math.Min(i + 2, count - 1)];
            double c1x = p1.Sharp ? p1.X : p1.X + (p2.X - p0.X) / 6, c1y = p1.Sharp ? p1.Y : p1.Y + (p2.Y - p0.Y) / 6;
            double c2x = p2.Sharp ? p2.X : p2.X - (p3.X - p1.X) / 6, c2y = p2.Sharp ? p2.Y : p2.Y - (p3.Y - p1.Y) / 6;
            pb.CubicTo(c1x, c1y, c2x, c2y, p2.X, p2.Y);
        }
        if (closed) pb.Close();
    }

    /// <summary>
    /// Samples a built-in motif's outer outline as evenly spaced custom points so it can be edited by hand.
    /// Holes and inner details (like leaf veins or an eye's pupil) are dropped.
    /// </summary>
    public static IReadOnlyList<ShapeNode> ConvertToNodes(Layer layer, int pointCount = 16)
    {
        double a = Math.Max(layer.Length, 0.01) / 2, b = Math.Max(layer.Width, 0.01) / 2;
        string data = BuildMotifLocal(layer);
        if (string.IsNullOrWhiteSpace(data)) return Layer.DefaultCustomNodes;

        List<Point>? best = null;
        double bestArea = -1;
        foreach (var pts in PathData.ToPolylines(data, 0.01))
        {
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var p in pts) { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); }
            double area = (maxX - minX) * (maxY - minY);
            if (area > bestArea) { bestArea = area; best = pts; }
        }
        if (best == null || best.Count < 3) return Layer.DefaultCustomNodes;
        if ((best[0] - best[^1]).Length > 1e-9) best.Add(best[0]);

        // Arc length along the closed outline
        var cum = new double[best.Count];
        for (int i = 1; i < best.Count; i++) cum[i] = cum[i - 1] + (best[i] - best[i - 1]).Length;
        double total = cum[^1];
        if (total <= 0) return Layer.DefaultCustomNodes;

        Point At(double s)
        {
            s = ((s % total) + total) % total;
            int i = Array.BinarySearch(cum, s);
            if (i >= 0) return best[i];
            i = Math.Clamp(~i, 1, best.Count - 1);
            double len = cum[i] - cum[i - 1];
            double t = len > 0 ? (s - cum[i - 1]) / len : 0;
            return best[i - 1] + (best[i] - best[i - 1]) * t;
        }

        // Find corners: places where the outline turns sharply over a short distance
        double probe = total * 0.012;
        var turn = new double[best.Count - 1];
        for (int i = 0; i < turn.Length; i++)
        {
            var before = best[i] - At(cum[i] - probe);
            var after = At(cum[i] + probe) - best[i];
            turn[i] = before.Length > 0 && after.Length > 0 ? Math.Abs(Vector.AngleBetween(before, after)) : 0;
        }
        var corners = new List<double>();
        for (int i = 0; i < turn.Length; i++)
        {
            if (turn[i] < 40) continue;
            bool isPeak = true;
            for (int j = 0; j < turn.Length && isPeak; j++)
            {
                double gap = Math.Abs(cum[j] - cum[i]);
                gap = Math.Min(gap, total - gap);
                if (j != i && gap < probe * 2 && (turn[j] > turn[i] || (turn[j] == turn[i] && j < i))) isPeak = false;
            }
            if (isPeak) corners.Add(cum[i]);
        }

        // Evenly spaced points, plus every corner as a sharp point
        double spacing = total / pointCount;
        var stops = new List<(double S, bool Sharp)>();
        foreach (double c in corners) stops.Add((c, true));
        for (int k = 0; k < pointCount; k++)
        {
            double s = k * spacing;
            bool nearCorner = false;
            foreach (double c in corners)
            {
                double gap = Math.Abs(s - c);
                if (Math.Min(gap, total - gap) < spacing * 0.5) { nearCorner = true; break; }
            }
            if (!nearCorner) stops.Add((s, false));
        }
        stops.Sort((x, y) => x.S.CompareTo(y.S));

        var result = new List<ShapeNode>();
        foreach (var (s, sharp) in stops)
        {
            var p = At(s);
            result.Add(new ShapeNode(Math.Round(p.X / a, 3), Math.Round(p.Y / b, 3), sharp));
        }
        return result;
    }



    private static void BuildMotif(PathBuilder pb, ShapeKind kind, double length, double width, double t)
    {
        double L = Math.Max(length, 0.01);
        double W = Math.Max(width, 0.01);
        double a = L / 2, b = W / 2;

        switch (kind)
        {
            case ShapeKind.Petal:
                Petal(pb, a, b, L, t);
                break;

            case ShapeKind.Leaf:
                Petal(pb, a, b, L, t);
                pb.MoveTo(-a, 0);
                pb.LineTo(a * 0.8, 0);
                foreach (double f in new[] { 0.3, 0.5, 0.7 })
                {
                    double x0 = -a + L * f;
                    pb.MoveTo(x0, 0); pb.LineTo(x0 + L * 0.12, b * 0.45);
                    pb.MoveTo(x0, 0); pb.LineTo(x0 + L * 0.12, -b * 0.45);
                }
                break;

            case ShapeKind.Teardrop:
            {
                double c = -a + L * (0.2 + 0.5 * Math.Clamp(t, 0, 1));
                double h = b * 1.3;
                pb.MoveTo(-a, 0);
                pb.CubicTo(c, h, a, h, a, 0);
                pb.CubicTo(a, -h, c, -h, -a, 0);
                pb.Close();
                break;
            }

            case ShapeKind.Circle:
                pb.Ellipse(0, 0, a, b);
                break;

            case ShapeKind.Diamond:
            {
                double m = -a + L * Math.Clamp(t, 0, 1);
                pb.MoveTo(-a, 0);
                pb.LineTo(m, b);
                pb.LineTo(a, 0);
                pb.LineTo(m, -b);
                pb.Close();
                break;
            }

            case ShapeKind.Triangle:
                BowedPolygon(pb, new[] { (-a, -b), (a, 0.0), (-a, b) }, (t - 0.5) * 2 * Math.Min(a, b));
                break;

            case ShapeKind.Star:
            {
                double inner = a * (0.2 + 0.6 * Math.Clamp(t, 0, 1));
                double sy = b / a;
                for (int k = 0; k < 10; k++)
                {
                    double ang = k * Math.PI / 5;
                    double rad = k % 2 == 0 ? a : inner;
                    double x = rad * Math.Cos(ang), y = rad * Math.Sin(ang) * sy;
                    if (k == 0) pb.MoveTo(x, y); else pb.LineTo(x, y);
                }
                pb.Close();
                break;
            }

            case ShapeKind.Heart:
                Heart(pb, a, b);
                break;

            case ShapeKind.Arch:
            {
                double rr = Math.Min(b, L);
                double x0 = a - rr;
                const double K = 0.5522847498;
                pb.MoveTo(-a, -b);
                pb.LineTo(x0, -b);
                pb.CubicTo(x0 + K * rr, -b, a, -K * b, a, 0);
                pb.CubicTo(a, K * b, x0 + K * rr, b, x0, b);
                pb.LineTo(-a, b);
                pb.Close();
                break;
            }

            case ShapeKind.Eye:
            {
                Petal(pb, a, b, L, 0.5);
                // The petal winds counter-clockwise and Ellipse winds clockwise, so the pupil is a true hole
                double pupil = Math.Min(b * (0.15 + 0.7 * Math.Clamp(t, 0, 1)), Math.Min(a, b) * 0.9);
                pb.Ellipse(0, 0, pupil, pupil);
                break;
            }

            case ShapeKind.Lotus:
            {
                double base0 = b * (0.45 + 0.5 * Math.Clamp(t, 0, 1));
                pb.MoveTo(-a, -base0);
                // Shoulder bulges out, then a concave run into the tip gives the ogee point
                pb.CubicTo(-a + 0.3 * L, -b * 1.25, -a + 0.66 * L, -b * 1.15, -a + 0.74 * L, -b * 0.6);
                pb.CubicTo(-a + 0.8 * L, -b * 0.3, a - 0.1 * L, -b * 0.2, a, 0);
                pb.CubicTo(a - 0.1 * L, b * 0.2, -a + 0.8 * L, b * 0.3, -a + 0.74 * L, b * 0.6);
                pb.CubicTo(-a + 0.66 * L, b * 1.15, -a + 0.3 * L, b * 1.25, -a, base0);
                pb.Close();
                break;
            }

            case ShapeKind.Dome:
            {
                double bw = b * (0.2 + 0.7 * Math.Clamp(t, 0, 1));
                pb.MoveTo(-a, -bw);
                pb.CubicTo(-a + L * 0.35, -b * 1.45, a - L * 0.3, -b * 0.1, a, 0);
                pb.CubicTo(a - L * 0.3, b * 0.1, -a + L * 0.35, b * 1.45, -a, bw);
                pb.Close();
                break;
            }

            case ShapeKind.Paisley:
                Paisley(pb, a, b, t < 0.5 ? 1 : -1);
                break;

            case ShapeKind.Butterfly: Butterfly(pb, a, b, t); break;
            case ShapeKind.Dragonfly: Dragonfly(pb, a, b); break;
            case ShapeKind.Tulip: Tulip(pb, a, b, t); break;
            case ShapeKind.Daisy: Daisy(pb, Math.Min(a, b), t); break;
            case ShapeKind.Rose: Rose(pb, a, b, t); break;
            case ShapeKind.Quatrefoil: Quatrefoil(pb, a, b, t); break;
            case ShapeKind.FleurDeLis: FleurDeLis(pb, a, b); break;
            case ShapeKind.PeacockFeather: PeacockFeather(pb, a, b); break;
            case ShapeKind.LotusBloom: LotusBloom(pb, a, b); break;
            case ShapeKind.Sunflower: Sunflower(pb, Math.Min(a, b), t); break;
            case ShapeKind.Snowflake: Snowflake(pb, Math.Min(a, b), t); break;
            case ShapeKind.MoroccanStar: MoroccanStar(pb, Math.Min(a, b), t); break;
            case ShapeKind.Ladybug: Ladybug(pb, a, b, t); break;
            case ShapeKind.Owl: Owl(pb, a, b); break;
            case ShapeKind.Bee: Bee(pb, a, b); break;
            case ShapeKind.Snail: Snail(pb, a, b); break;
            case ShapeKind.Fish: Fish(pb, a, b); break;
            case ShapeKind.Mushroom: Mushroom(pb, a, b, t); break;
            case ShapeKind.PineTree: PineTree(pb, a, b, t); break;
            case ShapeKind.Pumpkin: Pumpkin(pb, a, b); break;
            case ShapeKind.Sun: Sun(pb, Math.Min(a, b), t); break;
            case ShapeKind.Hummingbird: Hummingbird(pb, a, b); break;
            case ShapeKind.Fern: Fern(pb, a, b, t); break;
            case ShapeKind.Hamsa: Hamsa(pb, a, b); break;
            case ShapeKind.Crown: Crown(pb, a, b); break;
            case ShapeKind.Key: Key(pb, a, b); break;
            case ShapeKind.Anchor: Anchor(pb, a, b); break;
            case ShapeKind.MoonStars: MoonStars(pb, a, b, t); break;
            case ShapeKind.Raindrop: Raindrop(pb, a, b); break;
            case ShapeKind.Fan: Fan(pb, a, b, t); break;

            case ShapeKind.Flower:
            {
                int petals = 5 + (int)Math.Round(4 * Math.Clamp(t, 0, 1));
                double sy = b / a;
                for (int j = 0; j < petals; j++)
                {
                    double ang = 2 * Math.PI * j / petals;
                    RotatedEllipse(pb, a * 0.5 * Math.Cos(ang), a * 0.5 * Math.Sin(ang), a * 0.5, a * 0.28, ang, sy);
                }
                RotatedEllipse(pb, 0, 0, a * 0.24, a * 0.24, 0, sy);
                break;
            }

            case ShapeKind.Donut:
            {
                double k = 0.15 + 0.7 * Math.Clamp(t, 0, 1);
                pb.Ellipse(0, 0, a, b);
                pb.Ellipse(0, 0, a * k, b * k, reverse: true);
                break;
            }

            case ShapeKind.Crescent:
            {
                double R = a;
                double d = Math.Min(R * (0.25 + 0.9 * Math.Clamp(t, 0, 1)), R * 1.9); // offset of the bite circle
                double hx = -d / 2, hy = Math.Sqrt(Math.Max(R * R - d * d / 4, 0));
                pb.MoveTo(hx, -hy);
                pb.ArcTo(R, largeArc: true, sweep: true, hx, hy);   // outer edge, through (R,0)
                pb.ArcTo(R, largeArc: false, sweep: false, hx, -hy); // inner edge: circle centered on (-d,0)
                pb.Close();
                break;
            }

            case ShapeKind.Gem:
            {
                int sides = 3 + (int)Math.Round(5 * Math.Clamp(t, 0, 1));
                for (int j = 0; j < sides; j++)
                {
                    double ang = 2 * Math.PI * j / sides;
                    double x = a * Math.Cos(ang), y = b * Math.Sin(ang);
                    if (j == 0) pb.MoveTo(x, y); else pb.LineTo(x, y);
                }
                pb.Close();
                break;
            }

            case ShapeKind.Cross:
            {
                double th = Math.Min(a, b) * (0.12 + 0.6 * Math.Clamp(t, 0, 1));
                var pts = new (double, double)[]
                {
                    (a, -th), (a, th), (th, th), (th, b), (-th, b), (-th, th),
                    (-a, th), (-a, -th), (-th, -th), (-th, -b), (th, -b), (th, -th),
                };
                pb.MoveTo(pts[0].Item1, pts[0].Item2);
                for (int j = 1; j < pts.Length; j++) pb.LineTo(pts[j].Item1, pts[j].Item2);
                pb.Close();
                break;
            }

            case ShapeKind.Swirl:
            {
                double turns = 1 + 3 * Math.Clamp(t, 0, 1);
                double thetaMax = turns * 2 * Math.PI;
                int steps = (int)(turns * 64);
                double sy = b / a;
                pb.MoveTo(0, 0);
                for (int j = 1; j <= steps; j++)
                {
                    double th = thetaMax * j / steps;
                    double rad = a * th / thetaMax;
                    pb.LineTo(rad * Math.Cos(th), rad * Math.Sin(th) * sy);
                }
                break;
            }

            case ShapeKind.Spoke:
                pb.MoveTo(-a, 0);
                pb.LineTo(a, 0);
                double dot = b * Math.Clamp(t, 0, 1) * 2;
                if (dot > 0.05) pb.Ellipse(a, 0, dot, dot);
                break;
        }
    }

    private static void Petal(PathBuilder pb, double a, double b, double L, double t)
    {
        t = Math.Clamp(t, 0, 1);
        double h = b * 4 / 3; // a cubic with both control points at h peaks at 0.75h
        double c1 = -a + L * t * 0.66;
        double c2 = -a + L * (0.34 + 0.66 * t);
        pb.MoveTo(-a, 0);
        pb.CubicTo(c1, h, c2, h, a, 0);
        pb.CubicTo(c2, -h, c1, -h, -a, 0);
        pb.Close();
    }

    // ---------- Picture motifs ----------
    // Drawn in normalized units: u from -1 (toward the center) to +1 (outward) is scaled by a = Length/2,
    // and v from -1 to +1 across the ring is scaled by b = Width/2.

    /// <summary>
    /// A start point followed by cubic segments (6 numbers each), in normalized (u, v) units.
    /// Set flip to mirror it across the center line.
    /// </summary>
    private static void NormCurve(PathBuilder pb, double a, double b, double[] pts, bool close, bool flip = false)
    {
        double f = flip ? -1 : 1;
        pb.MoveTo(pts[0] * a, pts[1] * b * f);
        for (int i = 2; i + 5 < pts.Length; i += 6)
            pb.CubicTo(pts[i] * a, pts[i + 1] * b * f, pts[i + 2] * a, pts[i + 3] * b * f, pts[i + 4] * a, pts[i + 5] * b * f);
        if (close) pb.Close();
    }

    private static readonly double[] ForeWing =
    {
        0.30, 0.06,
        0.45, 0.35, 0.62, 0.75, 0.80, 0.98,     // leading edge sweeping forward to the tip
        0.55, 1.06, 0.12, 0.98, -0.04, 0.72,    // outer edge
        -0.12, 0.45, 0.00, 0.20, 0.04, 0.06,    // trailing edge back to the body
    };

    private static readonly double[] HindWing =
    {
        0.02, 0.06,
        -0.02, 0.45, -0.32, 0.86, -0.62, 0.72,
        -0.88, 0.58, -0.74, 0.22, -0.42, 0.05,
        -0.30, 0.03, -0.10, 0.04, 0.02, 0.06,
    };

    private static void Butterfly(PathBuilder pb, double a, double b, double t)
    {
        foreach (bool flip in new[] { false, true })
        {
            NormCurve(pb, a, b, ForeWing, close: true, flip);
            NormCurve(pb, a, b, HindWing, close: true, flip);

            double f = flip ? -1 : 1;
            // Wing spots, sized by Tweak
            double spot = Math.Clamp(t, 0, 1);
            if (spot > 0.05)
            {
                pb.Ellipse(0.42 * a, 0.62 * b * f, 0.13 * a * spot + 0.02 * a, 0.13 * b * spot + 0.02 * b);
                pb.Ellipse(-0.42 * a, 0.45 * b * f, 0.10 * a * spot + 0.02 * a, 0.10 * b * spot + 0.02 * b);
            }

            // Antenna: a gentle curve from the head with a small knob
            pb.MoveTo(0.66 * a, 0.03 * b * f);
            pb.CubicTo(0.78 * a, 0.08 * b * f, 0.88 * a, 0.16 * b * f, 0.96 * a, 0.28 * b * f);
            double knob = Math.Min(a, b) * 0.035;
            pb.Ellipse(0.96 * a, 0.28 * b * f, knob, knob);
        }

        // Body and head
        pb.Ellipse(-0.05 * a, 0, 0.58 * a, Math.Max(0.06 * b, 0.04 * a));
        double head = Math.Min(0.09 * b, 0.1 * a);
        pb.Ellipse(0.6 * a, 0, head, head);
    }

    private static void Dragonfly(PathBuilder pb, double a, double b)
    {
        double sy = b / a;
        // Wings first: long narrow ellipses, the front pair leaning forward and the back pair leaning back
        foreach (int s in new[] { 1, -1 })
        {
            Wing(0.47, 0.50 * s, 0.50, 0.085, s * Deg(78));
            Wing(0.24, 0.47 * s, 0.47, 0.090, s * Deg(104));
        }

        void Wing(double cu, double cv, double rx, double ry, double ang)
        {
            RotatedEllipse(pb, cu * a, cv * a, rx * a, ry * a, ang, sy);
            // A vein down the middle of the wing
            double dx = Math.Cos(ang) * rx * a * 0.85, dy = Math.Sin(ang) * rx * a * 0.85;
            pb.MoveTo(cu * a - dx * 0.95, (cv * a - dy * 0.95) * sy);
            pb.LineTo(cu * a + dx, (cv * a + dy) * sy);
        }

        // Thorax, head and the long segmented abdomen
        pb.Ellipse(0.34 * a, 0, 0.15 * a, 0.09 * a);
        pb.Ellipse(0.58 * a, 0, 0.09 * a, 0.09 * a);
        double w = 0.045 * a;
        pb.Ellipse(-0.42 * a, 0, 0.6 * a, w);
        for (int i = 1; i <= 6; i++)
        {
            double u = (0.16 - i * 0.16) * a;
            double half = w * Math.Sqrt(Math.Max(0, 1 - Math.Pow((u + 0.42 * a) / (0.6 * a), 2)));
            pb.MoveTo(u, -half);
            pb.LineTo(u, half);
        }
    }

    private static readonly double[] TulipCup =
    {
        0.00, 0.00,
        -0.04, -0.45, 0.38, -0.72, 0.95, -0.50,   // left side up to the left petal tip
        0.86, -0.36, 0.78, -0.26, 0.72, -0.18,    // dip
        0.82, -0.10, 0.94, -0.05, 1.00, 0.00,     // center petal tip
        0.94, 0.05, 0.82, 0.10, 0.72, 0.18,
        0.78, 0.26, 0.86, 0.36, 0.95, 0.50,       // right petal tip
        0.38, 0.72, -0.04, 0.45, 0.00, 0.00,      // right side back down
    };

    private static void Tulip(PathBuilder pb, double a, double b, double t)
    {
        NormCurve(pb, a, b, TulipCup, close: true);
        // The middle petal's edges, inside the cup
        pb.MoveTo(0.72 * a, -0.18 * b); pb.CubicTo(0.55 * a, -0.2 * b, 0.3 * a, -0.12 * b, 0.12 * a, 0);
        pb.MoveTo(0.72 * a, 0.18 * b); pb.CubicTo(0.55 * a, 0.2 * b, 0.3 * a, 0.12 * b, 0.12 * a, 0);

        // Stem
        double sw = Math.Max(0.035 * b, 0.02 * a);
        pb.MoveTo(0.02 * a, -sw); pb.LineTo(-a, -sw); pb.LineTo(-a, sw); pb.LineTo(0.02 * a, sw); pb.Close();

        // Leaves
        double leaf = Math.Clamp(t, 0, 1);
        if (leaf > 0.05)
            foreach (bool flip in new[] { false, true })
            {
                double s = 0.4 + 0.6 * leaf;
                double[] pts =
                {
                    -0.82, 0.03,
                    -0.82 + 0.25 * s, 0.50 * s, -0.82 + 0.55 * s, 0.62 * s, -0.82 + 0.75 * s, 0.62 * s,
                    -0.82 + 0.5 * s, 0.42 * s, -0.82 + 0.3 * s, 0.22 * s, -0.70, 0.03,
                };
                NormCurve(pb, a, b, pts, close: true, flip);
            }
    }

    /// <summary>Polyline through polar points (radius in normalized units) scaled by a in x and b in y.</summary>
    private static void PolarCurve(PathBuilder pb, double a, double b, Func<double, double> radius, double from, double to, int samples, bool close)
    {
        for (int i = 0; i <= samples; i++)
        {
            double th = from + (to - from) * i / samples;
            double r = radius(th);
            double x = r * Math.Cos(th) * a, y = r * Math.Sin(th) * b;
            if (i == 0) pb.MoveTo(x, y); else pb.LineTo(x, y);
        }
        if (close) pb.Close();
    }

    private static void Rose(PathBuilder pb, double a, double b, double t)
    {
        int n = 5 + (int)Math.Round(2 * Math.Clamp(t, 0, 1));
        double step = 2 * Math.PI / n;

        // Outline: rounded outer petals
        PolarCurve(pb, a, b, th => 0.84 + 0.16 * Math.Abs(Math.Cos(n * th / 2)), 0, 2 * Math.PI, 360, close: true);

        // Middle petals: cupped arcs that overlap their neighbors, offset half a step from the outline
        for (int k = 0; k < n; k++)
        {
            double c = k * step + step / 2, span = step * 0.8;
            PolarCurve(pb, a, b, th => 0.5 + 0.17 * Math.Cos(Math.PI / 2 * (th - c) / span), c - span, c + span, 32, close: false);
        }

        // Inner petals: three tighter cups
        double s3 = 2 * Math.PI / 3;
        for (int k = 0; k < 3; k++)
        {
            double c = k * s3 + 0.4, span = s3 * 0.75;
            PolarCurve(pb, a, b, th => 0.27 + 0.12 * Math.Cos(Math.PI / 2 * (th - c) / span), c - span, c + span, 28, close: false);
        }

        // The rolled-up heart: a small spiral
        PolarCurve(pb, a, b, th => 0.03 + 0.17 * th / (3 * Math.PI), 0, 3 * Math.PI, 90, close: false);
    }

    private static void Quatrefoil(PathBuilder pb, double a, double b, double t)
    {
        int n = 3 + (int)Math.Round(2 * Math.Clamp(t, 0, 1));
        // Lobes are circles of radius 0.5 centered 0.5 from the middle; the outline is the far edge of their union
        const double d = 0.5, rc = 0.5;
        double Radius(double th)
        {
            double best = 0;
            for (int k = 0; k < n; k++)
            {
                double phi = 2 * Math.PI * k / n;
                double dc = d * Math.Cos(th - phi), ds = d * Math.Sin(th - phi);
                if (Math.Abs(ds) <= rc) best = Math.Max(best, dc + Math.Sqrt(rc * rc - ds * ds));
            }
            return best;
        }
        PolarCurve(pb, a, b, Radius, 0, 2 * Math.PI, 720, close: true);
    }

    private static readonly double[] FleurCenter =
    {
        -0.12, -0.17,
        0.22, -0.42, 0.62, -0.22, 1.00, 0.00,
        0.62, 0.22, 0.22, 0.42, -0.12, 0.17,
        -0.14, 0.06, -0.14, -0.06, -0.12, -0.17,
    };

    private static readonly double[] FleurSide =
    {
        -0.12, 0.22,
        0.30, 0.20, 0.64, 0.62, 0.44, 0.92,      // sweeps up and out
        0.34, 1.06, 0.10, 1.02, 0.08, 0.83,      // rolls over into the curl
        0.20, 0.90, 0.31, 0.80, 0.28, 0.68,      // tight inner hook
        0.20, 0.50, 0.04, 0.45, -0.12, 0.46,     // back down to the band
        -0.13, 0.38, -0.13, 0.30, -0.12, 0.22,
    };

    private static readonly double[] FleurFoot =
    {
        -0.32, 0.17,
        -0.50, 0.24, -0.72, 0.44, -0.64, 0.58,
        -0.57, 0.68, -0.45, 0.58, -0.48, 0.46,
        -0.50, 0.38, -0.42, 0.30, -0.32, 0.30,
        -0.32, 0.26, -0.32, 0.21, -0.32, 0.17,
    };

    private static readonly double[] FleurTail =
    {
        -0.32, -0.11,
        -0.55, -0.09, -0.80, -0.05, -1.00, 0.00,
        -0.80, 0.05, -0.55, 0.09, -0.32, 0.11,
        -0.32, 0.04, -0.32, -0.04, -0.32, -0.11,
    };

    private static void FleurDeLis(PathBuilder pb, double a, double b)
    {
        NormCurve(pb, a, b, FleurCenter, close: true);
        NormCurve(pb, a, b, FleurSide, close: true);
        NormCurve(pb, a, b, FleurSide, close: true, flip: true);
        NormCurve(pb, a, b, FleurFoot, close: true);
        NormCurve(pb, a, b, FleurFoot, close: true, flip: true);
        NormCurve(pb, a, b, FleurTail, close: true);
        // The band that ties it together
        pb.MoveTo(-0.34 * a, -0.52 * b); pb.LineTo(-0.12 * a, -0.52 * b); pb.LineTo(-0.12 * a, 0.52 * b); pb.LineTo(-0.34 * a, 0.52 * b); pb.Close();
    }

    private static readonly double[] FeatherOutline =
    {
        -1.00, 0.00,
        -0.60, 0.06, -0.20, 0.62, 0.35, 0.78,
        0.85, 0.90, 1.06, 0.42, 1.00, 0.00,
        1.06, -0.42, 0.85, -0.90, 0.35, -0.78,
        -0.20, -0.62, -0.60, -0.06, -1.00, 0.00,
    };

    private static void PeacockFeather(PathBuilder pb, double a, double b)
    {
        NormCurve(pb, a, b, FeatherOutline, close: true);

        // The eye: three nested rings near the tip
        pb.Ellipse(0.60 * a, 0, 0.30 * a, 0.46 * b);
        pb.Ellipse(0.65 * a, 0, 0.18 * a, 0.28 * b);
        pb.Ellipse(0.69 * a, 0, 0.08 * a, 0.13 * b);

        // Quill down the middle, stopping at the eye
        pb.MoveTo(-1.0 * a, 0); pb.LineTo(0.30 * a, 0);

        // Barbs: angled lines from the quill out to the edge
        var outline = new PathBuilder();
        NormCurve(outline, a, b, FeatherOutline, close: true);
        var clip = new HatchClipper(PathData.ToPolylines(outline.ToString(), 0.05));
        var eyeEdge = 0.30;
        foreach (int s in new[] { 1, -1 })
            for (int i = 0; i < 6; i++)
            {
                double u = -0.78 + i * 0.17;
                var start = new Point(u * a, 0);
                var dir = new Vector(Math.Cos(Deg(40)) * a, s * Math.Sin(Deg(40)) * b);
                dir.Normalize();
                if (clip.Ray(start, dir) is not var (p, q)) continue;
                // Stop barbs short of the eye so they don't cross it
                if (q.X > eyeEdge * a) q = p + (q - p) * Math.Max(0, (eyeEdge * a - p.X) / (q.X - p.X));
                var end = p + (q - p) * 0.92;
                pb.MoveTo(p.X, p.Y); pb.LineTo(end.X, end.Y);
            }
    }

    // ---------- Batch 2: lotus bloom, sunflower, snowflake, Moroccan star, ladybug, owl ----------

    private static readonly double[] LotusCenter =
    {
        -0.55, 0.00,
        -0.25, -0.42, 0.55, -0.32, 1.00, 0.00,
        0.55, 0.32, -0.25, 0.42, -0.55, 0.00,
    };

    private static readonly double[] LotusInner =
    {
        -0.58, 0.04,
        -0.25, 0.05, 0.35, 0.20, 0.62, 0.66,
        0.20, 0.68, -0.30, 0.45, -0.60, 0.12,
        -0.60, 0.09, -0.59, 0.06, -0.58, 0.04,
    };

    private static readonly double[] LotusOuter =
    {
        -0.62, 0.10,
        -0.40, 0.15, 0.05, 0.50, 0.12, 1.00,
        -0.25, 0.92, -0.60, 0.55, -0.68, 0.18,
        -0.66, 0.15, -0.64, 0.12, -0.62, 0.10,
    };

    private static readonly double[] LotusBase =
    {
        -0.58, -0.55,
        -0.80, -0.30, -0.80, 0.30, -0.58, 0.55,
        -0.68, 0.20, -0.68, -0.20, -0.58, -0.55,
    };

    private static void LotusBloom(PathBuilder pb, double a, double b)
    {
        foreach (bool flip in new[] { false, true })
        {
            NormCurve(pb, a, b, LotusOuter, close: true, flip);
            NormCurve(pb, a, b, LotusInner, close: true, flip);
        }
        NormCurve(pb, a, b, LotusCenter, close: true);
        NormCurve(pb, a, b, LotusBase, close: true);
    }

    private static void Sunflower(PathBuilder pb, double r, double t)
    {
        int n = 14 + (int)Math.Round(10 * Math.Clamp(t, 0, 1));
        double disc = 0.36 * r;

        void Row(double r0, double r1, double offset, double widthScale)
        {
            double len = r1 - r0, mid = (r0 + r1) / 2;
            double w = Math.Min(2 * Math.PI * mid / n * 0.45, len * 0.2) * widthScale;
            for (int i = 0; i < n; i++)
                pb.WithFrame(0, 0, 2 * Math.PI * (i + offset) / n, () =>
                {
                    pb.MoveTo(r0, 0);
                    pb.CubicTo(r0 + 0.35 * len, -w * 1.3, r1 - 0.3 * len, -w * 1.1, r1, 0);
                    pb.CubicTo(r1 - 0.3 * len, w * 1.1, r0 + 0.35 * len, w * 1.3, r0, 0);
                    pb.Close();
                });
        }

        Row(disc * 0.95, r, 0, 1.0);          // outer row
        Row(disc * 0.95, r * 0.8, 0.5, 0.85); // inner row, between the outer petals

        // Seed center: a disc with a diamond cross-hatch
        pb.Ellipse(0, 0, disc, disc);
        double rc = disc * 0.86;
        pb.Ellipse(0, 0, rc, rc);
        double spacing = rc * 0.24;
        foreach (double ang in new[] { Math.PI / 4, -Math.PI / 4 })
        {
            double dx = Math.Cos(ang), dy = Math.Sin(ang), nx = -dy, ny = dx;
            for (double o = -rc + spacing / 2; o < rc; o += spacing)
            {
                double half = Math.Sqrt(Math.Max(rc * rc - o * o, 0));
                pb.MoveTo(nx * o - dx * half, ny * o - dy * half);
                pb.LineTo(nx * o + dx * half, ny * o + dy * half);
            }
        }
    }

    /// <summary>
    /// One wave crest in a unit strip (x 0→1 along the ring, y 0→1 outward): a start point plus cubic segments.
    /// It rises along its back, rolls over the top, curls under (leaving a hollow), then its face sweeps down to x = 1.
    /// </summary>
    private static readonly double[] WaveCrest =
    {
        0.00, 0.00,
        0.20, 0.10, 0.30, 0.75, 0.62, 0.90,   // back of the wave up to the top
        0.85, 1.00, 1.00, 0.80, 0.92, 0.58,   // over the top and down the front
        0.88, 0.48, 0.78, 0.50, 0.76, 0.60,   // the lip curling back under
        0.70, 0.45, 0.80, 0.15, 1.00, 0.00,   // the face sweeping down to the next wave
    };

    /// <summary>
    /// A chunky snowflake made for coloring: every part is a closed area at least a few millimeters wide.
    /// Each arm is one shape (a bar ending in a broad diamond) with an inner diamond. Wide branches meet
    /// the arm at its edge, so nothing overlaps into slivers.
    /// </summary>
    private static void Snowflake(PathBuilder pb, double r, double t)
    {
        double hexR = 0.24 * r;                      // center hexagon, flat sides facing the arms
        double apothem = hexR * Math.Cos(Math.PI / 6);
        double w = 0.075 * r;                        // half the arm width
        // Capped so branches from neighboring arms never reach each other (that would make slivers)
        double branchLen = (0.15 + 0.1 * Math.Clamp(t, 0, 1)) * r;
        double branchHalf = 0.06 * r;

        for (int k = 0; k < 6; k++)
        {
            pb.WithFrame(0, 0, k * Math.PI / 3, () =>
            {
                // Arm: bar from the hexagon out to a broad diamond tip
                pb.MoveTo(apothem, -w);
                pb.LineTo(0.6 * r, -w);
                pb.LineTo(0.78 * r, -0.17 * r);
                pb.LineTo(1.0 * r, 0);
                pb.LineTo(0.78 * r, 0.17 * r);
                pb.LineTo(0.6 * r, w);
                pb.LineTo(apothem, w);
                pb.Close();
                // Inner diamond in the tip
                pb.MoveTo(0.67 * r, 0); pb.LineTo(0.78 * r, -0.085 * r); pb.LineTo(0.92 * r, 0); pb.LineTo(0.78 * r, 0.085 * r); pb.Close();

                // A pair of wide branches springing from the bar's edges
                foreach (int side in new[] { 1, -1 })
                {
                    pb.WithFrame(0.4 * r, side * w, side * Math.PI * 50 / 180, () =>
                    {
                        pb.MoveTo(0, 0);
                        pb.CubicTo(branchLen * 0.3, -branchHalf * 1.3, branchLen * 0.75, -branchHalf * 1.2, branchLen, 0);
                        pb.CubicTo(branchLen * 0.75, branchHalf * 1.2, branchLen * 0.3, branchHalf * 1.3, 0, 0);
                        pb.Close();
                    });
                }
            });
        }

        // Center: a hexagon with a ring inside it to color, and a smaller hexagon in the middle
        foreach (double size in new[] { hexR, hexR * 0.55 })
        {
            for (int k = 0; k <= 6; k++)
            {
                double ang = k * Math.PI / 3 + Math.PI / 6;
                double x = size * Math.Cos(ang), y = size * Math.Sin(ang);
                if (k == 0) pb.MoveTo(x, y); else pb.LineTo(x, y);
            }
            pb.Close();
        }
    }

    private static void MoroccanStar(PathBuilder pb, double r, double t)
    {
        // Two squares, one turned 45°, so a point faces outward
        foreach (double offset in new[] { 0.0, Math.PI / 4 })
        {
            for (int k = 0; k < 4; k++)
            {
                double ang = offset + k * Math.PI / 2;
                if (k == 0) pb.MoveTo(r * Math.Cos(ang), r * Math.Sin(ang));
                else pb.LineTo(r * Math.Cos(ang), r * Math.Sin(ang));
            }
            pb.Close();
        }
        double c = (0.25 + 0.35 * Math.Clamp(t, 0, 1)) * r;
        pb.Ellipse(0, 0, c, c);
    }

    private static void Ladybug(PathBuilder pb, double a, double b, double t)
    {
        double m = Math.Min(a, b);
        // Head first, then the shell over it
        pb.Ellipse(0.68 * a, 0, 0.2 * a, 0.38 * b);
        pb.Ellipse(-0.12 * a, 0, 0.82 * a, 0.95 * b);
        // Where the wing cases meet, and the front edge of the shell
        pb.MoveTo(0.5 * a, 0); pb.LineTo(-0.94 * a, 0);
        pb.MoveTo(0.45 * a, -0.8 * b); pb.CubicTo(0.6 * a, -0.4 * b, 0.6 * a, 0.4 * b, 0.45 * a, 0.8 * b);

        double spot = (0.08 + 0.12 * Math.Clamp(t, 0, 1)) * m;
        foreach (int s in new[] { 1, -1 })
        {
            foreach (var (u, v) in new[] { (0.15, 0.5), (-0.3, 0.62), (-0.62, 0.3) })
                pb.Ellipse(u * a, s * v * b, spot, spot);

            // Eyes and antennae
            pb.Ellipse(0.76 * a, s * 0.18 * b, 0.05 * m, 0.05 * m);
            pb.MoveTo(0.86 * a, s * 0.12 * b); pb.CubicTo(0.92 * a, s * 0.16 * b, 0.95 * a, s * 0.24 * b, 0.98 * a, s * 0.34 * b);

            // Three legs per side, poking out from under the shell
            foreach (double u in new[] { 0.25, -0.1, -0.45 })
            {
                double edge = 0.95 * Math.Sqrt(Math.Max(0, 1 - Math.Pow((u + 0.12) / 0.82, 2)));
                pb.MoveTo(u * a, s * edge * b);
                pb.LineTo((u - 0.06) * a, s * Math.Min(edge + 0.12, 1.05) * b);
            }
        }
    }

    private static void Owl(PathBuilder pb, double a, double b)
    {
        double m = Math.Min(a, b);
        foreach (int s in new[] { 1, -1 })
        {
            // Ear tufts
            pb.MoveTo(0.58 * a, s * 0.4 * b); pb.LineTo(0.97 * a, s * 0.62 * b); pb.LineTo(0.68 * a, s * 0.68 * b); pb.Close();
            // Wings folded at the sides
            pb.MoveTo(0.15 * a, s * 0.72 * b);
            pb.CubicTo(-0.2 * a, s * 0.88 * b, -0.6 * a, s * 0.78 * b, -0.8 * a, s * 0.45 * b);
            pb.CubicTo(-0.5 * a, s * 0.56 * b, -0.15 * a, s * 0.56 * b, 0.15 * a, s * 0.72 * b);
            pb.Close();
        }
        pb.Ellipse(-0.08 * a, 0, 0.86 * a, 0.78 * b);

        foreach (int s in new[] { 1, -1 })
        {
            // Big eyes with pupils and a highlight
            double ex = 0.36 * a, ey = s * 0.3 * b;
            pb.Ellipse(ex, ey, 0.25 * m, 0.25 * m);
            pb.Ellipse(ex, ey, 0.11 * m, 0.11 * m);
            pb.Ellipse(ex + 0.05 * m, ey + 0.04 * m * s, 0.035 * m, 0.035 * m);
            // Feet: three short toes each
            foreach (double toe in new[] { -0.07, 0.0, 0.07 })
            {
                pb.MoveTo(-0.84 * a, s * 0.22 * b);
                pb.LineTo(-0.98 * a, s * (0.22 + toe) * b);
            }
        }

        // Beak pointing down (toward the center)
        pb.MoveTo(0.2 * a, -0.08 * b); pb.LineTo(0.2 * a, 0.08 * b); pb.LineTo(0.02 * a, 0); pb.Close();

        // Belly feathers: rows of little cups
        foreach (var (u, count) in new[] { (-0.2, 3), (-0.4, 4), (-0.6, 3) })
        {
            double span = 0.7, hw = span / count / 2;
            for (int i = 0; i < count; i++)
            {
                double v = -span / 2 + hw + i * 2 * hw;
                pb.MoveTo(u * a, (v - hw * 0.9) * b);
                pb.CubicTo((u - 0.11) * a, (v - hw * 0.9) * b, (u - 0.11) * a, (v + hw * 0.9) * b, u * a, (v + hw * 0.9) * b);
            }
        }
    }

    // ---------- Batch 3: bee, snail, fish, mushroom, pine tree, pumpkin, sun ----------
    // All parts are closed areas or lines that split a closed area, so everything can be colored.

    private static void Bee(PathBuilder pb, double a, double b)
    {
        double m = Math.Min(a, b);
        // Wings first (they sit behind the body): long ovals pointing out and back
        foreach (int s in new[] { 1, -1 })
        {
            foreach (var (au, av, tu, tv, fat) in new[] { (0.25, 0.22, -0.3, 0.98, 0.5), (0.05, 0.22, -0.6, 0.78, 0.45) })
            {
                double ax = au * a, ay = s * av * b, dx = tu * a - ax, dy = s * tv * b - ay;
                double len = Math.Sqrt(dx * dx + dy * dy);
                pb.WithFrame(ax + dx / 2, ay + dy / 2, Math.Atan2(dy, dx), () => pb.Ellipse(0, 0, len * 0.55, len * 0.55 * fat));
            }
        }

        // Body with curved stripes that divide it into bands
        const double cu = -0.15, rx = 0.62, ry = 0.38;
        pb.Ellipse(cu * a, 0, rx * a, ry * b);
        foreach (double u in new[] { -0.5, -0.3, -0.1, 0.1 })
        {
            double half = ry * Math.Sqrt(Math.Max(0, 1 - Math.Pow((u - cu) / rx, 2)));
            pb.MoveTo(u * a, -half * b);
            pb.CubicTo((u + 0.09) * a, -half * 0.5 * b, (u + 0.09) * a, half * 0.5 * b, u * a, half * b);
        }

        // Head, eyes, antennae and stinger
        pb.Ellipse(0.6 * a, 0, 0.2 * a, 0.25 * b);
        foreach (int s in new[] { 1, -1 })
        {
            pb.Ellipse(0.66 * a, s * 0.12 * b, 0.07 * m, 0.07 * m);
            pb.MoveTo(0.74 * a, s * 0.1 * b); pb.CubicTo(0.84 * a, s * 0.14 * b, 0.9 * a, s * 0.22 * b, 0.94 * a, s * 0.34 * b);
            pb.Ellipse(0.94 * a, s * 0.34 * b, 0.05 * m, 0.05 * m);
        }
        pb.MoveTo(-0.75 * a, -0.08 * b); pb.LineTo(-0.97 * a, 0); pb.LineTo(-0.75 * a, 0.08 * b); pb.Close();
    }

    private static readonly double[] SnailFoot =
    {
        -0.92, -0.90,
        -0.92, -0.40, -0.92, 0.20, -0.92, 0.55,
        -0.92, 0.85, -0.60, 0.98, -0.22, 0.93,   // front of the foot rising into the head
        0.08, 0.88, 0.14, 0.66, -0.04, 0.60,     // round the head
        -0.28, 0.55, -0.48, 0.45, -0.62, 0.30,   // back down the neck
        -0.62, 0.00, -0.62, -0.50, -0.62, -0.74,
        -0.62, -0.90, -0.76, -0.95, -0.92, -0.90, // tail
    };

    private static void Snail(PathBuilder pb, double a, double b)
    {
        double m = Math.Min(a, b);
        NormCurve(pb, a, b, SnailFoot, close: true);

        // Eye stalks with knobs
        foreach (var (u0, v0, u1, v1) in new[] { (-0.06, 0.78, 0.26, 0.84), (-0.1, 0.86, 0.18, 1.0) })
        {
            pb.MoveTo(u0 * a, v0 * b); pb.LineTo(u1 * a, v1 * b);
            pb.Ellipse(u1 * a, v1 * b, 0.055 * m, 0.055 * m);
        }

        // Spiral shell sitting on the foot. The spiral line divides it into a band that winds inward.
        double R = 0.55 * m;
        double cx = -0.6 * a + R, cy = -0.12 * b;
        pb.Ellipse(cx, cy, R, R);
        const double turns = 2.4;
        double thMax = turns * 2 * Math.PI;
        for (int i = 0; i <= 160; i++)
        {
            double th = thMax * i / 160;
            double rr = R * (0.04 + 0.96 * th / thMax);
            double x = cx + rr * Math.Cos(th + Math.PI), y = cy + rr * Math.Sin(th + Math.PI);
            if (i == 0) pb.MoveTo(x, y); else pb.LineTo(x, y);
        }
    }

    private static readonly double[] FishBody =
    {
        0.95, 0.00,
        0.75, -0.62, -0.15, -0.68, -0.50, 0.00,
        -0.15, 0.68, 0.75, 0.62, 0.95, 0.00,
    };

    private static readonly double[] FishTail =
    {
        -0.42, 0.00,
        -0.60, -0.20, -0.80, -0.50, -1.00, -0.62,
        -0.90, -0.30, -0.88, -0.10, -0.82, 0.00,
        -0.88, 0.10, -0.90, 0.30, -1.00, 0.62,
        -0.80, 0.50, -0.60, 0.20, -0.42, 0.00,
    };

    private static void Fish(PathBuilder pb, double a, double b)
    {
        double m = Math.Min(a, b);
        NormCurve(pb, a, b, FishTail, close: true);
        // Fins on the back and belly
        NormCurve(pb, a, b, new[] { 0.35, 0.42, 0.25, 0.85, -0.05, 0.85, -0.2, 0.38, 0.0, 0.42, 0.2, 0.44, 0.35, 0.42 }, close: true);
        NormCurve(pb, a, b, new[] { 0.2, -0.42, 0.1, -0.70, -0.05, -0.68, -0.05, -0.40, 0.03, -0.42, 0.12, -0.43, 0.2, -0.42 }, close: true);
        NormCurve(pb, a, b, FishBody, close: true);

        // Body half-width along its length, for fitting the gill and scale rows
        double Hw(double u) => 0.47 * Math.Sqrt(Math.Max(0, 1 - Math.Pow((u - 0.225) / 0.725, 2)));

        // Gill: a curve that separates the head
        pb.MoveTo(0.5 * a, -Hw(0.5) * 0.95 * b);
        pb.CubicTo(0.64 * a, -Hw(0.5) * 0.4 * b, 0.64 * a, Hw(0.5) * 0.4 * b, 0.5 * a, Hw(0.5) * 0.95 * b);

        // Scale rows: scalloped lines across the body, which split it into bands to color
        foreach (double u in new[] { 0.3, 0.05, -0.2 })
        {
            double hw = Hw(u) * 0.97;
            int cups = 3;
            double w = 2 * hw / cups;
            pb.MoveTo(u * a, -hw * b);
            for (int i = 0; i < cups; i++)
            {
                double v0 = -hw + i * w, v1 = v0 + w;
                pb.CubicTo((u - 0.12) * a, (v0 + w * 0.1) * b, (u - 0.12) * a, (v1 - w * 0.1) * b, u * a, v1 * b);
            }
        }

        // Eye
        pb.Ellipse(0.72 * a, 0.12 * b, 0.08 * m, 0.08 * m);
        pb.Ellipse(0.74 * a, 0.12 * b, 0.035 * m, 0.035 * m);
    }

    private static readonly double[] MushroomCap =
    {
        0.02, -0.95,
        0.60, -1.00, 1.00, -0.50, 0.98, 0.00,
        1.00, 0.50, 0.60, 1.00, 0.02, 0.95,
        0.12, 0.50, 0.12, -0.50, 0.02, -0.95,
    };

    private static readonly double[] MushroomStem =
    {
        0.08, -0.30,
        -0.30, -0.32, -0.75, -0.40, -0.95, -0.42,
        -0.97, -0.15, -0.97, 0.15, -0.95, 0.42,
        -0.75, 0.40, -0.30, 0.32, 0.08, 0.30,
        0.09, 0.10, 0.09, -0.10, 0.08, -0.30,
    };

    private static void Mushroom(PathBuilder pb, double a, double b, double t)
    {
        double m = Math.Min(a, b);
        NormCurve(pb, a, b, MushroomStem, close: true);
        // A ring around the stem
        pb.MoveTo(-0.2 * a, -0.33 * b); pb.CubicTo(-0.28 * a, -0.15 * b, -0.28 * a, 0.15 * b, -0.2 * a, 0.33 * b);
        NormCurve(pb, a, b, MushroomCap, close: true);

        double spot = (0.05 + 0.1 * Math.Clamp(t, 0, 1)) * m;
        if (t > 0.05)
            foreach (var (u, v) in new[] { (0.78, 0.0), (0.55, -0.48), (0.55, 0.48), (0.3, -0.72), (0.3, 0.72), (0.38, 0.0) })
                pb.Ellipse(u * a, v * b, spot, spot);
    }

    private static void PineTree(PathBuilder pb, double a, double b, double t)
    {
        double m = Math.Min(a, b);
        // Trunk (its top tucks under the lowest tier)
        pb.MoveTo(-0.5 * a, -0.14 * b); pb.LineTo(-0.97 * a, -0.14 * b); pb.LineTo(-0.97 * a, 0.14 * b); pb.LineTo(-0.5 * a, 0.14 * b); pb.Close();

        // Three tiers, bottom to top, each with a scalloped lower edge
        foreach (var (baseU, apexU, hw) in new[] { (-0.55, 0.15, 0.95), (-0.15, 0.55, 0.75), (0.25, 0.98, 0.55) })
        {
            double len = apexU - baseU;
            pb.MoveTo(apexU * a, 0);
            pb.CubicTo((apexU - len * 0.33) * a, -hw * 0.27 * b, (apexU - len * 0.66) * a, -hw * 0.6 * b, baseU * a, -hw * b);
            const int bumps = 3;
            double w = 2 * hw / bumps;
            for (int i = 0; i < bumps; i++)
            {
                double v0 = -hw + i * w, v1 = v0 + w;
                pb.CubicTo((baseU - 0.1) * a, (v0 + w * 0.15) * b, (baseU - 0.1) * a, (v1 - w * 0.15) * b, baseU * a, v1 * b);
            }
            pb.CubicTo((apexU - len * 0.66) * a, hw * 0.6 * b, (apexU - len * 0.33) * a, hw * 0.27 * b, apexU * a, 0);
            pb.Close();
        }

        // Optional ornaments
        if (t > 0.05)
        {
            double r = (0.04 + 0.05 * Math.Clamp(t, 0, 1)) * m;
            foreach (var (u, v) in new[] { (-0.38, -0.5), (-0.38, 0.5), (-0.12, 0.0), (0.08, -0.36), (0.08, 0.36), (0.4, 0.0), (0.58, -0.18), (0.58, 0.18) })
                pb.Ellipse(u * a, v * b, r, r);
        }
    }

    private static void Pumpkin(PathBuilder pb, double a, double b)
    {
        // Stem and leaf first, then the body
        NormCurve(pb, a, b, new[] { 0.6, -0.1, 0.75, -0.12, 0.9, -0.06, 0.98, 0.06, 0.96, 0.10, 0.94, 0.14, 0.92, 0.18, 0.84, 0.08, 0.74, 0.08, 0.6, 0.1, 0.6, 0.03, 0.6, -0.03, 0.6, -0.1 }, close: true);
        NormCurve(pb, a, b, new[] { 0.66, 0.14, 0.8, 0.2, 0.92, 0.45, 0.82, 0.64, 0.7, 0.56, 0.62, 0.36, 0.66, 0.14 }, close: true);
        pb.Ellipse(-0.12 * a, 0, 0.8 * a, 0.95 * b);
        // Ribs: curves from the top to the bottom that divide the body into lobes
        foreach (double v in new[] { -0.62, -0.28, 0.28, 0.62 })
        {
            pb.MoveTo(0.62 * a, v * 0.42 * b);
            pb.CubicTo(0.35 * a, v * 1.3 * b, -0.6 * a, v * 1.3 * b, -0.88 * a, v * 0.42 * b);
        }
    }

    private static void Sun(PathBuilder pb, double r, double t)
    {
        int n = 12 + 2 * (int)Math.Round(4 * Math.Clamp(t, 0, 1));
        double step = 2 * Math.PI / n, half = step / 2, disc = 0.5 * r;

        // Rays sit edge to edge on the face's circle, alternating long and pointed with short and rounded.
        // Each base follows the circle exactly, so there are no slivers between the rays and the face.
        for (int k = 0; k < n; k++)
        {
            double th = k * step;
            void P(double rad, double ang, bool first) { double x = rad * Math.Cos(ang), y = rad * Math.Sin(ang); if (first) pb.MoveTo(x, y); else pb.LineTo(x, y); }
            P(disc, th - half, true);
            if (k % 2 == 0) P(r, th, false);
            else
            {
                double tip = 0.8 * r;
                for (int j = 0; j <= 10; j++)
                {
                    double f = j / 10.0, ang = th - half * 0.55 + half * 1.1 * f;
                    P(tip - 0.1 * r * Math.Pow(2 * f - 1, 2), ang, false);
                }
            }
            P(disc, th + half, false);
            for (int j = 1; j < 6; j++) P(disc, th + half - step * j / 6, false);
            pb.Close();
        }

        // Face
        pb.Ellipse(0, 0, disc, disc);
        foreach (int s in new[] { 1, -1 })
        {
            pb.Ellipse(0.15 * r, s * 0.18 * r, 0.09 * r, 0.06 * r);   // eyes
            pb.Ellipse(-0.08 * r, s * 0.32 * r, 0.07 * r, 0.07 * r);  // cheeks
        }
        pb.MoveTo(-0.12 * r, -0.2 * r);                              // smile
        pb.CubicTo(-0.34 * r, -0.12 * r, -0.34 * r, 0.12 * r, -0.12 * r, 0.2 * r);
        pb.CubicTo(-0.22 * r, 0.08 * r, -0.22 * r, -0.08 * r, -0.12 * r, -0.2 * r);
        pb.Close();
    }

    // ---------- Batch 4: hummingbird, fern, hamsa, crown, key, anchor, moon and stars, raindrop ----------

    /// <summary>Builds a closed shape in the motif's local frame (mm, no transform) as polylines.</summary>
    private static Loops LocalGeometry(Action<PathBuilder> draw)
    {
        var local = new PathBuilder();
        draw(local);
        return PathData.ToPolylines(local.ClosedData, 0.01);
    }

    private static Loops LocalRect(double u0, double u1, double v0, double v1, double radius = 0) => LocalGeometry(p =>
    {
        double r = Math.Min(radius, Math.Min((u1 - u0) / 2, (v1 - v0) / 2));
        if (r <= 0.001)
        {
            p.MoveTo(u0, v0); p.LineTo(u1, v0); p.LineTo(u1, v1); p.LineTo(u0, v1); p.Close();
            return;
        }
        double k = r * PathBuilder.Kappa;
        p.MoveTo(u0 + r, v0);
        p.LineTo(u1 - r, v0); p.CubicTo(u1 - r + k, v0, u1, v0 + r - k, u1, v0 + r);
        p.LineTo(u1, v1 - r); p.CubicTo(u1, v1 - r + k, u1 - r + k, v1, u1 - r, v1);
        p.LineTo(u0 + r, v1); p.CubicTo(u0 + r - k, v1, u0, v1 - r + k, u0, v1 - r);
        p.LineTo(u0, v0 + r); p.CubicTo(u0, v0 + r - k, u0 + r - k, v0, u0 + r, v0);
        p.Close();
    });

    private static Loops LocalCircle(double u, double v, double r) => LocalGeometry(p => p.Ellipse(u, v, r, r));

    /// <summary>
    /// Merges overlapping parts into one clean outline and draws it. Without merging, a finger's base inside the palm
    /// would leave a stray line and a sliver to color.
    /// </summary>
    private static void EmitUnion(PathBuilder pb, params Loops[] parts)
    {
        var all = new Loops();
        foreach (var part in parts) all.AddRange(part);
        foreach (var loop in PathData.Union(all))
        {
            if (loop.Count < 3) continue;
            pb.MoveTo(loop[0].X, loop[0].Y);
            for (int i = 1; i < loop.Count - 1; i++) pb.LineTo(loop[i].X, loop[i].Y);
            pb.Close();
        }
    }

    private static void Hummingbird(PathBuilder pb, double a, double b)
    {
        double m = Math.Min(a, b);
        // One clean silhouette: the far wing (behind), body, tail, head and beak merged together
        EmitUnion(pb,
            LocalGeometry(p => NormCurve(p, a, b, new[] { 0.05, -0.15, 0.0, -0.45, -0.15, -0.70, -0.30, -0.76, -0.30, -0.55, -0.25, -0.35, -0.15, -0.20, -0.08, -0.17, -0.02, -0.16, 0.05, -0.15 }, close: true)),
            LocalGeometry(p => NormCurve(p, a, b, new[] { -0.66, -0.06, -0.80, -0.14, -0.92, -0.22, -1.00, -0.30, -0.97, -0.15, -0.95, -0.08, -0.93, -0.04, -0.97, 0.04, -0.99, 0.12, -1.00, 0.18, -0.90, 0.13, -0.78, 0.10, -0.66, 0.08, -0.66, 0.03, -0.66, -0.01, -0.66, -0.06 }, close: true)),
            LocalGeometry(p => NormCurve(p, a, b, new[] { 0.48, 0.05, 0.30, -0.36, -0.40, -0.32, -0.74, -0.04, -0.70, 0.04, -0.70, 0.08, -0.72, 0.12, -0.40, 0.22, 0.20, 0.36, 0.48, 0.05 }, close: true)),
            LocalCircle(0.46 * a, 0.06 * b, 0.17 * m),
            LocalGeometry(p => { p.MoveTo(0.55 * a, 0.0); p.LineTo(1.0 * a, 0.1 * b); p.LineTo(0.55 * a, 0.14 * b); p.Close(); }));

        // Near wing on top, with feather lines
        NormCurve(pb, a, b, new[] { 0.14, 0.20, 0.10, 0.55, -0.05, 0.90, -0.26, 0.99, -0.31, 0.76, -0.26, 0.46, -0.16, 0.24, -0.06, 0.21, 0.04, 0.20, 0.14, 0.20 }, close: true);
        pb.MoveTo(0.04 * a, 0.26 * b); pb.CubicTo(0.0, 0.5 * b, -0.08 * a, 0.75 * b, -0.18 * a, 0.92 * b);
        pb.MoveTo(-0.07 * a, 0.25 * b); pb.CubicTo(-0.12 * a, 0.45 * b, -0.16 * a, 0.65 * b, -0.24 * a, 0.8 * b);
        // A line where the head meets the body, and the eye
        pb.MoveTo(0.33 * a, -0.08 * b); pb.CubicTo(0.3 * a, 0.0, 0.3 * a, 0.12 * b, 0.34 * a, 0.2 * b);
        pb.Ellipse(0.52 * a, 0.1 * b, 0.045 * m, 0.045 * m);
    }

    private static void Fern(PathBuilder pb, double a, double b, double t)
    {
        int pairs = 6 + (int)Math.Round(4 * Math.Clamp(t, 0, 1));
        double stemHalf = Math.Max(0.025 * b, 0.015 * a);
        pb.MoveTo(-a, -stemHalf); pb.LineTo(0.82 * a, -stemHalf); pb.LineTo(0.82 * a, stemHalf); pb.LineTo(-a, stemHalf); pb.Close();

        double spacing = 1.6 * a / pairs;
        double angle = 50 * Math.PI / 180;
        for (int i = 0; i < pairs; i++)
        {
            double f = (double)i / pairs;
            double len = (0.95 - 0.6 * f) * b;
            double half = Math.Min(len * 0.17, spacing * Math.Sin(angle) * 0.42);
            foreach (int s in new[] { 1, -1 })
            {
                // The two sides are offset half a step, so the leaflets alternate along the stem
                double u = -0.85 * a + i * spacing + (s < 0 ? spacing / 2 : 0);
                if (u > 0.78 * a) continue;
                pb.WithFrame(u, s * stemHalf, s * angle, () =>
                {
                    pb.MoveTo(0, 0);
                    pb.CubicTo(len * 0.3, -half * 1.3, len * 0.75, -half * 1.2, len, 0);
                    pb.CubicTo(len * 0.75, half * 1.2, len * 0.3, half * 1.3, 0, 0);
                    pb.Close();
                });
            }
        }
        // Tip leaflet
        double tipLen = 0.22 * a, tipHalf = Math.Min(0.07 * b, tipLen * 0.25);
        pb.MoveTo(0.78 * a, 0);
        pb.CubicTo(0.78 * a + tipLen * 0.3, -tipHalf * 1.3, 0.78 * a + tipLen * 0.75, -tipHalf * 1.2, a, 0);
        pb.CubicTo(0.78 * a + tipLen * 0.75, tipHalf * 1.2, 0.78 * a + tipLen * 0.3, tipHalf * 1.3, 0.78 * a, 0);
        pb.Close();
    }

    private static void Hamsa(PathBuilder pb, double a, double b)
    {
        double m = Math.Min(a, b);
        var parts = new List<Loops> { LocalRect(-0.9 * a, 0.14 * a, -0.6 * b, 0.6 * b, 0.3 * m) };
        // Three upright fingers, the middle one longest
        foreach (var (v, top) in new[] { (-0.27, 0.72), (0.0, 0.9), (0.27, 0.72) })
            parts.Add(LocalRect(-0.1 * a, top * a, (v - 0.11) * b, (v + 0.11) * b, 0.11 * Math.Min(b, a)));
        // Two matching thumbs that curl outward
        foreach (bool flip in new[] { false, true })
            parts.Add(LocalGeometry(p => NormCurve(p, a, b, new[] { -0.2, 0.5, 0.02, 0.6, 0.25, 0.62, 0.45, 0.8, 0.52, 0.9, 0.64, 0.84, 0.56, 0.7, 0.44, 0.5, 0.3, 0.38, 0.08, 0.36, 0.0, 0.36, -0.1, 0.4, -0.2, 0.5 }, close: true, flip)));
        EmitUnion(pb, parts.ToArray());

        // Eye in the palm, and a cuff line at the wrist
        pb.MoveTo(-0.32 * a, -0.34 * b);
        pb.CubicTo(-0.13 * a, -0.15 * b, -0.13 * a, 0.15 * b, -0.32 * a, 0.34 * b);
        pb.CubicTo(-0.51 * a, 0.15 * b, -0.51 * a, -0.15 * b, -0.32 * a, -0.34 * b);
        pb.Close();
        pb.Ellipse(-0.32 * a, 0, 0.12 * m, 0.12 * m);
        pb.Ellipse(-0.32 * a, 0, 0.05 * m, 0.05 * m);
        pb.MoveTo(-0.7 * a, -0.59 * b); pb.CubicTo(-0.63 * a, -0.2 * b, -0.63 * a, 0.2 * b, -0.7 * a, 0.59 * b);
    }

    private static void Crown(PathBuilder pb, double a, double b)
    {
        double m = Math.Min(a, b);
        var body = LocalGeometry(p =>
        {
            double[] pts = { -0.55, -0.85, 0.55, -0.85, 0.1, -0.62, 0.75, -0.42, 0.12, -0.2, 0.92, 0.0, 0.12, 0.2, 0.75, 0.42, 0.1, 0.62, 0.55, 0.85, -0.55, 0.85 };
            p.MoveTo(pts[0] * a, pts[1] * b);
            for (int i = 2; i < pts.Length; i += 2) p.LineTo(pts[i] * a, pts[i + 1] * b);
            p.Close();
        });
        var parts = new List<Loops> { body, LocalRect(-0.94 * a, -0.55 * a, -0.85 * b, 0.85 * b, 0.05 * m) };
        foreach (var (u, v) in new[] { (0.55, -0.85), (0.75, -0.42), (0.92, 0.0), (0.75, 0.42), (0.55, 0.85) })
            parts.Add(LocalCircle(u * a + 0.04 * m, v * b * 0.93, 0.085 * m));
        EmitUnion(pb, parts.ToArray());

        // The band's top edge, and jewels
        pb.MoveTo(-0.55 * a, -0.85 * b); pb.LineTo(-0.55 * a, 0.85 * b);
        pb.MoveTo(-0.6 * a, 0); pb.LineTo(-0.745 * a, -0.12 * b); pb.LineTo(-0.89 * a, 0); pb.LineTo(-0.745 * a, 0.12 * b); pb.Close();
        foreach (int s in new[] { 1, -1 }) pb.Ellipse(-0.745 * a, s * 0.5 * b, 0.075 * m, 0.075 * m);
        pb.MoveTo(0.45 * a, 0); pb.LineTo(0.2 * a, -0.12 * b); pb.LineTo(-0.05 * a, 0); pb.LineTo(0.2 * a, 0.12 * b); pb.Close();
        foreach (int s in new[] { 1, -1 }) pb.Ellipse(-0.15 * a, s * 0.42 * b, 0.07 * m, 0.07 * m);
    }

    private static void Key(PathBuilder pb, double a, double b)
    {
        double m = Math.Min(a, b);
        double bowR = 0.36 * m, bowU = a - bowR;
        double shaft = Math.Max(0.16 * b, 0.06 * a);
        EmitUnion(pb,
            LocalCircle(bowU, 0, bowR),
            LocalRect(0.12 * a, bowU - bowR * 0.6, -0.32 * b, 0.32 * b, 0.04 * m),   // collar
            LocalRect(-0.95 * a, bowU - bowR * 0.5, -shaft, shaft),                   // shaft
            LocalRect(-0.95 * a, -0.8 * a, shaft * 0.5, 0.66 * b),                   // teeth
            LocalRect(-0.7 * a, -0.56 * a, shaft * 0.5, 0.52 * b));
        // Hole in the bow, wound the other way so it's cut out of the fill
        pb.Ellipse(bowU, 0, bowR * 0.5, bowR * 0.5, reverse: true);
    }

    private static void Anchor(PathBuilder pb, double a, double b)
    {
        double m = Math.Min(a, b);
        double ringR = 0.17 * m, ringU = a - ringR;
        double half = Math.Max(0.09 * b, 0.05 * a);
        var arms = LocalGeometry(p =>
        {
            // A thick curved band below the shank
            const int steps = 40;
            for (int i = 0; i <= steps; i++)
            {
                double th = (100 + 160.0 * i / steps) * Math.PI / 180;
                double x = -0.3 * a + 0.62 * a * Math.Cos(th), y = 0.82 * b * Math.Sin(th);
                if (i == 0) p.MoveTo(x, y); else p.LineTo(x, y);
            }
            for (int i = steps; i >= 0; i--)
            {
                double th = (100 + 160.0 * i / steps) * Math.PI / 180;
                p.LineTo(-0.3 * a + 0.5 * a * Math.Cos(th), 0.68 * b * Math.Sin(th));
            }
            p.Close();
        });
        var parts = new List<Loops>
        {
            LocalCircle(ringU, 0, ringR),
            LocalRect(-0.86 * a, ringU, -half, half),                                // shank
            LocalRect(0.4 * a, 0.52 * a, -0.5 * b, 0.5 * b, 0.05 * m),               // crossbar
            arms,
        };
        // Flukes: arrowheads at the ends of the arms
        foreach (int s in new[] { 1, -1 })
            parts.Add(LocalGeometry(p => { p.MoveTo(-0.52 * a, s * 0.62 * b); p.LineTo(-0.1 * a, s * 0.86 * b); p.LineTo(-0.46 * a, s * 0.99 * b); p.Close(); }));
        EmitUnion(pb, parts.ToArray());
        pb.Ellipse(ringU, 0, ringR * 0.5, ringR * 0.5, reverse: true);
    }

    private static void MoonStars(PathBuilder pb, double a, double b, double t)
    {
        double m = Math.Min(a, b);
        double R = 0.88 * m, d = 0.55 * R;
        // Crescent turned so its hollow faces across the ring, with the stars nestled in the hollow
        pb.WithFrame(0, -0.08 * b, -Math.PI / 2, () =>
        {
            double hx = -d / 2, hy = Math.Sqrt(R * R - d * d / 4);
            pb.MoveTo(hx, -hy);
            pb.ArcTo(R, largeArc: true, sweep: true, hx, hy);
            pb.ArcTo(R, largeArc: false, sweep: false, hx, -hy);
            pb.Close();
        });
        double cv = -0.08 * b + d; // center of the hollow
        int stars = 1 + (int)Math.Round(2 * Math.Clamp(t, 0, 1));
        // Stars big enough to color, tucked into the hollow
        var spots = new[] { (0.0, cv + 0.12 * m, 0.56 * m), (0.5 * a, cv + 0.02 * m, 0.36 * m), (-0.5 * a, cv + 0.04 * m, 0.34 * m) };
        for (int i = 0; i < stars; i++)
        {
            var (u, v, size) = spots[i];
            pb.WithOffset(u, v, () => BuildMotif(pb, ShapeKind.Star, size, size, 0.45));
        }
    }

    private static readonly double[] DropOutline =
    {
        1.00, 0.00,
        0.50, -0.30, -0.10, -0.80, -0.55, -0.72,
        -0.95, -0.62, -1.05, -0.20, -1.00, 0.00,
        -1.05, 0.20, -0.95, 0.62, -0.55, 0.72,
        -0.10, 0.80, 0.50, 0.30, 1.00, 0.00,
    };

    private static void Raindrop(PathBuilder pb, double a, double b)
    {
        NormCurve(pb, a, b, DropOutline, close: true);
        // A smaller drop inside, nearer the round end
        var inner = new double[DropOutline.Length];
        for (int i = 0; i < inner.Length; i += 2)
        {
            inner[i] = DropOutline[i] * 0.55 - 0.3;
            inner[i + 1] = DropOutline[i + 1] * 0.55;
        }
        NormCurve(pb, a, b, inner, close: true);
        // A shine in the band between them
        NormCurve(pb, a, b, new[] { 0.18, 0.38, 0.0, 0.58, -0.4, 0.66, -0.6, 0.56, -0.4, 0.52, -0.05, 0.48, 0.18, 0.38 }, close: true);
    }

    /// <summary>
    /// Shell fan: a half-disc opening outward from a hub at its base, with rounded lobes along the edge and
    /// lines from the hub to each valley, so every lobe is its own area to color.
    /// </summary>
    private static void Fan(PathBuilder pb, double a, double b, double t)
    {
        int lobes = 5 + (int)Math.Round(4 * Math.Clamp(t, 0, 1));
        double cu = -0.9 * a;            // hub at the base, toward the center
        double ru = 1.9 * a, rv = b;     // reach of the fan along and across the ring
        double Edge(double th)
        {
            double f = (th + Math.PI / 2) / Math.PI * lobes;   // 0..lobes across the half circle
            double within = f - Math.Floor(f);
            return 0.86 + 0.14 * Math.Sin(Math.PI * within);   // a rounded bump per lobe
        }
        const int samples = 180;
        for (int i = 0; i <= samples; i++)
        {
            double th = -Math.PI / 2 + Math.PI * i / samples;
            double rr = Edge(th);
            double x = cu + ru * rr * Math.Cos(th), y = rv * rr * Math.Sin(th);
            if (i == 0) pb.MoveTo(x, y); else pb.LineTo(x, y);
        }
        pb.Close();

        // Hub: a small half-disc at the base
        double hu = 0.22 * ru, hv = 0.22 * rv;
        for (int i = 0; i <= 40; i++)
        {
            double th = -Math.PI / 2 + Math.PI * i / 40;
            double x = cu + hu * Math.Cos(th), y = hv * Math.Sin(th);
            if (i == 0) pb.MoveTo(x, y); else pb.LineTo(x, y);
        }
        pb.Close();

        // Lines from the hub out to each valley between lobes
        for (int k = 1; k < lobes; k++)
        {
            double th = -Math.PI / 2 + Math.PI * k / lobes;
            pb.MoveTo(cu + hu * Math.Cos(th), hv * Math.Sin(th));
            pb.LineTo(cu + ru * 0.86 * Math.Cos(th), rv * 0.86 * Math.Sin(th));
        }
    }

    private static void Daisy(PathBuilder pb, double r, double t)
    {
        int petals = 10 + (int)Math.Round(14 * Math.Clamp(t, 0, 1));
        double disc = 0.3 * r;
        double len = r - disc;
        double mid = disc + len / 2;
        // Petals are narrow enough that neighbors don't overlap at their widest point
        double half = Math.Min(2 * Math.PI * mid / petals * 0.42, len * 0.22);
        for (int i = 0; i < petals; i++)
        {
            pb.WithFrame(0, 0, 2 * Math.PI * i / petals, () =>
            {
                // Pointed where it meets the disc, rounded at the tip
                pb.MoveTo(disc, 0);
                pb.CubicTo(disc + 0.3 * len, -half * 1.25, r + 0.02 * r, -half * 0.95, r, 0);
                pb.CubicTo(r + 0.02 * r, half * 0.95, disc + 0.3 * len, half * 1.25, disc, 0);
                pb.Close();
            });
        }
        pb.Ellipse(0, 0, disc, disc);
        pb.Ellipse(0, 0, disc * 0.55, disc * 0.55);
    }

    /// <summary>Ellipse rotated by <paramref name="ang"/> about its center, then scaled in y by <paramref name="sy"/>.</summary>
    private static void RotatedEllipse(PathBuilder pb, double cx, double cy, double rx, double ry, double ang, double sy)
    {
        const double K = PathBuilder.Kappa;
        double c = Math.Cos(ang), s = Math.Sin(ang);
        (double, double) T(double u, double v) => (cx + u * rx * c - v * ry * s, (cy + u * rx * s + v * ry * c) * sy);

        var (x0, y0) = T(1, 0);
        pb.MoveTo(x0, y0);
        double[] q =
        {
            1, K, K, 1, 0, 1,
            -K, 1, -1, K, -1, 0,
            -1, -K, -K, -1, 0, -1,
            K, -1, 1, -K, 1, 0,
        };
        for (int i = 0; i < q.Length; i += 6)
        {
            var (x1, y1) = T(q[i], q[i + 1]);
            var (x2, y2) = T(q[i + 2], q[i + 3]);
            var (x3, y3) = T(q[i + 4], q[i + 5]);
            pb.CubicTo(x1, y1, x2, y2, x3, y3);
        }
        pb.Close();
    }

    // Paisley in a unit box (u: -1 = center side, +1 = outer; v across), as a start point plus cubic segments
    private static readonly double[] PaisleyPts =
    {
        1.0, 0.8,
        0.6, 0.35, 0.55, -0.9, -0.2, -0.95,
        -0.95, -1.0, -1.05, 0.1, -0.7, 0.55,
        -0.4, 0.95, 0.25, 0.9, 0.5, 0.6,
        0.62, 0.45, 0.8, 0.55, 1.0, 0.8,
    };

    private static void Paisley(PathBuilder pb, double a, double b, int flip)
    {
        (double, double) Map(int i) => (a * PaisleyPts[i], b * PaisleyPts[i + 1] * flip);

        var (sx, sy) = Map(0);
        pb.MoveTo(sx, sy);
        for (int i = 2; i < PaisleyPts.Length; i += 6)
        {
            var (x1, y1) = Map(i);
            var (x2, y2) = Map(i + 2);
            var (x3, y3) = Map(i + 4);
            pb.CubicTo(x1, y1, x2, y2, x3, y3);
        }
        pb.Close();
    }

    // Heart outline in a 100x100 box with the tip at (50,90), mapped so the tip points at the center
    private static readonly double[] HeartPts =
    {
        50, 90,
        20, 65, 0, 45, 0, 28,
        0, 10, 15, 0, 30, 0,
        40, 0, 47, 6, 50, 15,
        53, 6, 60, 0, 70, 0,
        85, 0, 100, 10, 100, 28,
        100, 45, 80, 65, 50, 90,
    };

    private static void Heart(PathBuilder pb, double a, double b)
    {
        (double, double) Map(int i) => (a * ((90 - HeartPts[i + 1]) / 45 - 1), b * (HeartPts[i] - 50) / 50);

        var (sx, sy) = Map(0);
        pb.MoveTo(sx, sy);
        for (int i = 2; i < HeartPts.Length; i += 6)
        {
            var (x1, y1) = Map(i);
            var (x2, y2) = Map(i + 2);
            var (x3, y3) = Map(i + 4);
            pb.CubicTo(x1, y1, x2, y2, x3, y3);
        }
        pb.Close();
    }

    /// <summary>Polygon whose sides bow outward (bow &gt; 0) or inward (bow &lt; 0).</summary>
    private static void BowedPolygon(PathBuilder pb, (double X, double Y)[] pts, double bow)
    {
        double cx = 0, cy = 0;
        foreach (var p in pts) { cx += p.X; cy += p.Y; }
        cx /= pts.Length; cy /= pts.Length;

        pb.MoveTo(pts[0].X, pts[0].Y);
        for (int i = 0; i < pts.Length; i++)
        {
            var p = pts[i];
            var q = pts[(i + 1) % pts.Length];
            if (Math.Abs(bow) < 1e-6)
            {
                pb.LineTo(q.X, q.Y);
                continue;
            }
            double mx = (p.X + q.X) / 2, my = (p.Y + q.Y) / 2;
            double nx = q.Y - p.Y, ny = -(q.X - p.X);
            double len = Math.Sqrt(nx * nx + ny * ny);
            if (len < 1e-9) continue;
            nx /= len; ny /= len;
            if ((mx - cx) * nx + (my - cy) * ny < 0) { nx = -nx; ny = -ny; } // point the normal away from the middle
            pb.QuadTo(p.X, p.Y, mx + nx * bow * 2, my + ny * bow * 2, q.X, q.Y);
        }
        pb.Close();
    }

    /// <param name="lineWork">Where open line work goes (rope strands, Greek key, leaf ribs). It's kept out of the fillable outline,
    /// because a line running all the way round the ring would otherwise fill the whole middle. Null leaves it out.</param>
    private static void BuildRing(PathBuilder pb, Layer layer, int n, double start, double step, double radius, PathBuilder? lineWork = null)
    {
        double r = Math.Max(radius, 0);
        double L = Math.Max(layer.Length, 0);
        double W = Math.Max(layer.Width, 0);
        double t = Math.Clamp(layer.Tweak, 0, 1);

        switch (layer.Shape)
        {
            case ShapeKind.Ring:
                if (W <= 0.05)
                {
                    if (r > 0) pb.Ellipse(0, 0, r, r);
                }
                else
                {
                    pb.Ellipse(0, 0, r + W / 2, r + W / 2);
                    double inner = r - W / 2;
                    if (inner > 0.01) pb.Ellipse(0, 0, inner, inner, reverse: true);
                }
                break;

            case ShapeKind.Scallop:
            {
                double spread = (t - 0.5) * 0.5 * step;
                double outer = r + L * 4 / 3;
                Move(pb, r, start);
                for (int i = 0; i < n; i++)
                {
                    double a0 = start + i * step, a1 = a0 + step;
                    var (x1, y1) = Polar(outer, a0 + spread);
                    var (x2, y2) = Polar(outer, a1 - spread);
                    var (x3, y3) = Polar(r, a1);
                    pb.CubicTo(x1, y1, x2, y2, x3, y3);
                }
                pb.Close();
                break;
            }

            case ShapeKind.Zigzag:
                for (int i = 0; i < n; i++)
                {
                    double a0 = start + i * step;
                    if (i == 0) Move(pb, r + L / 2, a0); else Line(pb, r + L / 2, a0);
                    Line(pb, Math.Max(r - L / 2, 0), a0 + step / 2);
                }
                pb.Close();
                break;

            case ShapeKind.Wave:
            {
                int samples = Math.Max(360, n * 32);
                for (int j = 0; j < samples; j++)
                {
                    double phase = 2 * Math.PI * j / samples;
                    double rad = r + L / 2 * Math.Cos(n * phase);
                    if (j == 0) Move(pb, rad, start + phase); else Line(pb, rad, start + phase);
                }
                pb.Close();
                break;
            }

            case ShapeKind.Sunburst:
            {
                double shortRay = L * (0.2 + 0.7 * t);
                for (int i = 0; i < n; i++)
                {
                    double a0 = start + i * step;
                    double ray = i % 2 == 0 ? L : shortRay;
                    if (i == 0) Move(pb, r + ray, a0); else Line(pb, r + ray, a0);
                    Line(pb, r, a0 + step / 2);
                }
                pb.Close();
                break;
            }

            case ShapeKind.Gear:
            {
                double toothBase = 0.3 + 0.5 * t; // fraction of the pitch the tooth occupies at the root
                double flank = Math.Min(0.1, toothBase / 3);
                for (int i = 0; i < n; i++)
                {
                    double a0 = start + i * step - toothBase * step / 2; // center each tooth on its angle
                    if (i == 0) Move(pb, r, a0); else Line(pb, r, a0);
                    Line(pb, r + L, a0 + flank * step);
                    Line(pb, r + L, a0 + (toothBase - flank) * step);
                    Line(pb, r, a0 + toothBase * step);
                }
                pb.Close();
                break;
            }

            case ShapeKind.Vine:
            {
                double amp = L * 0.16;
                double stem = Math.Max(0.35, L * 0.03);
                // The stem: a thin wavy band, so it can be filled without filling the whole middle of the ring
                Func<double, double> wave = th => r + amp * Math.Sin(n * (th - start));
                int samples = Math.Max(360, n * 40);
                PolarBand(pb, th => wave(th) + stem, start, start + 2 * Math.PI, samples, reverse: false);
                PolarBand(pb, th => wave(th) - stem, start, start + 2 * Math.PI, samples, reverse: true);

                // A leaf on every crest (pointing out) and trough (pointing in), leaning along the stem
                double leafLen = L * (0.22 + 0.3 * t), leafW = leafLen * 0.38;
                for (int i = 0; i < n; i++)
                {
                    foreach (bool crest in new[] { true, false })
                    {
                        double th = start + (i + (crest ? 0.25 : 0.75)) * step;
                        double rr = wave(th);
                        double dir = crest ? th - 0.55 : th + Math.PI + 0.55;
                        pb.WithFrame(rr * Math.Cos(th), rr * Math.Sin(th), dir, () =>
                        {
                            pb.MoveTo(0, 0);
                            pb.CubicTo(leafLen * 0.3, -leafW, leafLen * 0.75, -leafW * 0.9, leafLen, 0);
                            pb.CubicTo(leafLen * 0.75, leafW * 0.9, leafLen * 0.3, leafW, 0, 0);
                            pb.Close();
                        });
                        lineWork?.WithFrame(rr * Math.Cos(th), rr * Math.Sin(th), dir, () =>
                        {
                            lineWork.MoveTo(leafLen * 0.08, 0); lineWork.LineTo(leafLen * 0.8, 0); // midrib
                        });
                    }
                }
                break;
            }

            case ShapeKind.Rope:
            {
                double inner = Math.Max(r - L / 2, 0), outer = r + L / 2;
                pb.Ellipse(0, 0, outer, outer);
                if (inner > 0.01) pb.Ellipse(0, 0, inner, inner, reverse: true);
                // Each strand is an S-curve leaning across the band; a second one makes it look twisted
                double lean = (0.6 + 0.8 * t) * step;
                if (lineWork != null)
                foreach (double shift in new[] { 0.0, 0.42 })
                    for (int i = 0; i < n; i++)
                    {
                        double a0 = start + (i + shift) * step;
                        var (x0, y0) = Polar(inner, a0);
                        var (x1, y1) = Polar(inner + L * 0.55, a0 + lean * 0.05);
                        var (x2, y2) = Polar(outer - L * 0.55, a0 + lean * 0.95);
                        var (x3, y3) = Polar(outer, a0 + lean);
                        lineWork.MoveTo(x0, y0);
                        lineWork.CubicTo(x1, y1, x2, y2, x3, y3);
                    }
                break;
            }

            case ShapeKind.GreekKey:
            {
                double inner = Math.Max(r - L / 2, 0), outer = r + L / 2;
                pb.Ellipse(0, 0, outer, outer);
                if (inner > 0.01) pb.Ellipse(0, 0, inner, inner, reverse: true);
                // One continuous meander: each repeat is a squared spiral on a 5 × 4 grid
                double[] unit = { 0, 0, 0, 4, 4, 4, 4, 1, 2, 1, 2, 2, 3, 2, 3, 3, 1, 3, 1, 0 };
                double lo = inner + L * 0.15, hi = outer - L * 0.15;
                if (lineWork == null) break;
                for (int i = 0; i < n; i++)
                {
                    double a0 = start + i * step;
                    for (int j = 0; j < unit.Length; j += 2)
                    {
                        double ang = a0 + unit[j] / 5 * step;
                        double rad = lo + unit[j + 1] / 4 * (hi - lo);
                        if (i == 0 && j == 0) Move(lineWork, rad, ang); else Line(lineWork, rad, ang);
                    }
                }
                Line(lineWork, lo, start + n * step); // close the run back to where it began
                break;
            }

            case ShapeKind.Waves:
            {
                double inner = Math.Max(r - L / 2, 0), outer = r + L / 2;
                pb.Ellipse(0, 0, outer, outer);
                if (inner > 0.01) pb.Ellipse(0, 0, inner, inner, reverse: true);

                // Each wave is a closed crest standing on the inner circle: the back rises, the top rolls forward and
                // curls under, leaving a hollow beneath it, and the face sweeps back down to meet the next wave.
                // Shapes are laid out in a strip (x along the ring, y outward) and wrapped onto the ring.
                double sy = L * 0.88;
                for (int i = 0; i < n; i++)
                {
                    double a0 = start + i * step;
                    void Emit(double x, double y, bool first)
                    {
                        double ang = a0 + x * step, rad = inner + y * sy;
                        if (first) Move(pb, rad, ang); else Line(pb, rad, ang);
                    }
                    Emit(WaveCrest[0], WaveCrest[1], true);
                    for (int j = 2; j + 5 < WaveCrest.Length; j += 6)
                    {
                        double x0 = WaveCrest[j - 2], y0 = WaveCrest[j - 1];
                        for (int s = 1; s <= 14; s++)
                        {
                            double u = s / 14.0, v = 1 - u;
                            double x = v * v * v * x0 + 3 * v * v * u * WaveCrest[j] + 3 * v * u * u * WaveCrest[j + 2] + u * u * u * WaveCrest[j + 4];
                            double y = v * v * v * y0 + 3 * v * v * u * WaveCrest[j + 1] + 3 * v * u * u * WaveCrest[j + 3] + u * u * u * WaveCrest[j + 5];
                            Emit(x, y, false);
                        }
                    }
                    // Walk back along the inner circle so the base sits exactly on it (no sliver under the chord)
                    for (int s = 1; s <= 8; s++) Emit(1 - s / 8.0, 0, false);
                    pb.Close();
                }
                break;
            }

            case ShapeKind.Chain:
            {
                double arc = step * r;
                for (int i = 0; i < n; i++)
                {
                    double th = start + i * step;
                    bool faceOn = i % 2 == 0;
                    pb.WithFrame(r * Math.Cos(th), r * Math.Sin(th), th + Math.PI / 2, () =>
                    {
                        if (faceOn)
                        {
                            // Face-on link: an oval ring
                            pb.Ellipse(0, 0, 0.62 * arc, L / 2);
                            pb.Ellipse(0, 0, 0.62 * arc * 0.6, L / 2 * 0.45, reverse: true);
                        }
                        else
                        {
                            // Edge-on link: a slim oval threading through its neighbors
                            pb.Ellipse(0, 0, 0.55 * arc, L * 0.13);
                        }
                    });
                }
                break;
            }

            case ShapeKind.Stitch:
            {
                double inner = Math.Max(r - L / 2, 0), outer = r + L / 2;
                pb.Ellipse(0, 0, outer, outer);
                if (inner > 0.01) pb.Ellipse(0, 0, inner, inner, reverse: true);
                if (lineWork == null) break;
                double dash = 0.3 + 0.5 * t;
                for (int i = 0; i < n; i++)
                {
                    double a0 = start + (i + (1 - dash) / 2) * step, a1 = a0 + dash * step;
                    for (int j = 0; j <= 6; j++)
                    {
                        double ang = a0 + (a1 - a0) * j / 6;
                        if (j == 0) Move(lineWork, r, ang); else Line(lineWork, r, ang);
                    }
                }
                break;
            }

            case ShapeKind.FlowerOfLife:
            {
                double inner = Math.Max(r - L / 2, 0), outer = r + L / 2;
                pb.Ellipse(0, 0, outer, outer);
                if (inner > 0.01) pb.Ellipse(0, 0, inner, inner, reverse: true);
                // Circles that each reach both rings and overlap their neighbors, so the overlaps form petal shapes
                double rho = L / 2;
                for (int i = 0; i < n; i++)
                {
                    var (x, y) = Polar(r, start + i * step);
                    pb.Ellipse(x, y, rho, rho);
                }
                break;
            }

            case ShapeKind.SwirlBand:
            {
                double inner = Math.Max(r - L / 2, 0), outer = r + L / 2;
                pb.Ellipse(0, 0, outer, outer);
                if (inner > 0.01) pb.Ellipse(0, 0, inner, inner, reverse: true);
                if (lineWork == null) break;

                // Curved blade edges sweeping from the inner circle to the outer one. They turn fastest near the
                // middle and straighten out toward the rim, like a pinwheel. Each blade between two edges is one area.
                double twist = step * (0.6 + 2.0 * t);
                for (int i = 0; i < n; i++)
                {
                    double a0 = start + i * step;
                    for (int j = 0; j <= 40; j++)
                    {
                        double f = j / 40.0;
                        double rad = inner + (outer - inner) * f;
                        double ang = a0 + twist * (1 - (1 - f) * (1 - f));
                        if (j == 0) Move(lineWork, rad, ang); else Line(lineWork, rad, ang);
                    }
                }
                break;
            }

            case ShapeKind.Lattice:
            {
                // Each point on the inner circle connects to the outer points k steps either side, so the lines cross
                double inner = Math.Max(r - L / 2, 0), outer = r + L / 2;
                int k = 1 + (int)Math.Round(2 * t);
                for (int i = 0; i < n; i++)
                {
                    double a0 = start + i * step;
                    Move(pb, inner, a0); Line(pb, outer, a0 + k * step / 2);
                    Move(pb, inner, a0); Line(pb, outer, a0 - k * step / 2);
                }
                break;
            }

            case ShapeKind.Polygon:
            {
                if (r <= 0) break;
                int maxSkip = Math.Max(1, (n - 1) / 2);
                int k = Math.Clamp(1 + (int)Math.Floor(t * (maxSkip - 1) + 1e-9), 1, maxSkip);
                if (n < 3) k = 1;
                int g = Gcd(n, k);
                for (int c = 0; c < g; c++)
                {
                    Move(pb, r, start + c * step);
                    for (int m = 1; m < n / g; m++)
                        Line(pb, r, start + ((c + m * k) % n) * step);
                    pb.Close();
                }
                break;
            }
        }
    }

    /// <summary>A closed loop following radius(angle) around the full circle (in mm, ring coordinates). Reverse winds it the other way.</summary>
    private static void PolarBand(PathBuilder pb, Func<double, double> radius, double from, double to, int samples, bool reverse)
    {
        for (int i = 0; i <= samples; i++)
        {
            double th = reverse ? to - (to - from) * i / samples : from + (to - from) * i / samples;
            if (i == 0) Move(pb, radius(th), th); else Line(pb, radius(th), th);
        }
        pb.Close();
    }

    private static void Move(PathBuilder pb, double r, double ang) { var (x, y) = Polar(r, ang); pb.MoveTo(x, y); }
    private static void Line(PathBuilder pb, double r, double ang) { var (x, y) = Polar(r, ang); pb.LineTo(x, y); }
    private static (double, double) Polar(double r, double ang) => (r * Math.Cos(ang), r * Math.Sin(ang));
    private static double Deg(double d) => d * Math.PI / 180;
    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return Math.Max(a, 1); }
}
