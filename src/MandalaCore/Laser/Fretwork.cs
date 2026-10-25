using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Clipper2Lib;
using MandalaMaker.Model;
using MandalaMaker.Shapes;

namespace MandalaMaker.Laser;

/// <summary>What a laser-cut piece looks like and the rules it's cut by. Lengths are in mm.</summary>
public sealed class LaserSettings
{
    /// <summary>"lattice" (interlocking squares), "diamonds" (the lattice turned 45°), "circles" (overlapping rings) or "mandala".</summary>
    public string Pattern { get; set; } = "lattice";
    public double DiameterMm { get; set; } = 300;
    /// <summary>Width of the pattern's strips of wood.</summary>
    public double BarMm { get; set; } = 5;
    /// <summary>The narrowest wood allowed anywhere (bridges to loose bits, number strokes). Set by the material.</summary>
    public double BridgeMm { get; set; } = 1.6;
    /// <summary>Holes narrower than this are filled in, since the laser can't cut them cleanly.</summary>
    public double MinHoleMm { get; set; } = 2.5;
    /// <summary>"circle" (a solid rim around the outside) or "shape" (no rim: the edge follows the design, like a flower clock).</summary>
    public string Frame { get; set; } = "circle";
    /// <summary>Solid ring around the outside, with the circle frame.</summary>
    public double RimMm { get; set; } = 14;
    /// <summary>How many lattice cells fit across the pattern.</summary>
    public int Cells { get; set; } = 9;
    public Difficulty Detail { get; set; } = Difficulty.Simple;
    public int Seed { get; set; } = 1;

    public bool Clock { get; set; } = true;
    /// <summary>"arabic" (1–12), "roman" (I–XII) or "none".</summary>
    public string Numbers { get; set; } = "arabic";
    /// <summary>"upright" or "radial" (each number turned to face the center).</summary>
    public string NumberAngle { get; set; } = "upright";
    public string NumberFont { get; set; } = "Playfair Display";
    /// <summary>Depth of the band the numbers sit in.</summary>
    public double NumberBandMm { get; set; } = 30;
    /// <summary>Hole for the clock movement's shaft (or a hanging hole). 0 for none.</summary>
    public double CenterHoleMm { get; set; } = 8;

    /// <summary>Also cut a plain backing disc, for the two-tone look.</summary>
    public bool Backer { get; set; } = true;
    /// <summary>1 for a flat piece, or 2–8 for a layered 3D stack (each layer cut separately, then stacked and glued).</summary>
    public int Layers { get; set; } = 1;

    public LaserSettings Clone() => (LaserSettings)MemberwiseClone();

    /// <summary>
    /// Loose bits that would fall out: "bridge" ties every piece big enough to matter back to the main piece
    /// (crumbs are still removed), "remove" drops them all.
    /// </summary>
    public string Loose { get; set; } = "bridge";
}

/// <summary>A number's outline from the font, in font units with y pointing down, centered on its box.</summary>
public sealed record Glyph(string PathData, double Width, double Height);

/// <summary>All the pieces of a design, top layer first, plus totals.</summary>
public sealed class LaserStack
{
    public List<LaserResult> Layers { get; init; } = new();
    public double RadiusMm { get; init; }
    public double CenterHoleMm { get; init; }
    public bool Backer { get; init; }
    public int Holes => Layers.Sum(l => l.Holes);
    public int Bridges => Layers.Sum(l => l.Bridges);
    public int LooseRemoved => Layers.Sum(l => l.LooseRemoved);
    public double CutLengthMm => Layers.Sum(l => l.CutLengthMm) +
        (Backer ? 2 * Math.PI * RadiusMm + (CenterHoleMm > 0 ? Math.PI * CenterHoleMm : 0) : 0);
}

/// <summary>One cut piece (a layer) and some facts about it.</summary>
public sealed class LaserResult
{
    /// <summary>The wood: outer outlines and holes (even-odd).</summary>
    public PathsD Material { get; init; } = new();
    public double RadiusMm { get; init; }
    public double CenterHoleMm { get; init; }
    public bool Backer { get; init; }
    public int Holes { get; init; }
    public int Pieces { get; init; }
    public int LooseRemoved { get; init; }
    public int Bridges { get; init; }
    public double CutLengthMm { get; init; }
}

/// <summary>
/// Turns a pattern of lines into a one-piece laser cut: every line becomes a strip of wood at least the bridge width,
/// the spaces between become holes, holes too thin to cut are filled in, and loose bits are removed or bridged.
/// </summary>
public static class Fretwork
{
    private const int Precision = 3;        // 0.001 mm
    private const double ArcTol = 0.02;     // mm, how closely round joins follow true arcs
    private const double Flatten = 0.05;    // mm, curve flattening for pattern lines

    private static readonly string[] Arabic = { "12", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11" };
    private static readonly string[] Roman = { "XII", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI" };

    /// <summary>The texts the numbers need, so the caller can fetch their outlines from the font.</summary>
    public static IReadOnlyList<string> NumberTexts(LaserSettings s) => s.Numbers == "roman" ? Roman : Arabic;

    /// <summary>How many layers a design has (1 = flat).</summary>
    public static int LayerCount(LaserSettings s) => Math.Clamp(s.Layers, 1, 8);

    /// <summary>The settings for one layer of a stack (0 = top): each layer gets its own pattern, so the ones below show through.</summary>
    public static LaserSettings LayerSettings(LaserSettings s, int layer)
    {
        var l = s.Clone();
        if (layer == 0) return l;
        l.Seed = s.Seed + layer * 1013;
        if (s.Pattern is "lattice" or "diamonds" && layer % 2 == 1) l.Pattern = s.Pattern == "lattice" ? "diamonds" : "lattice";
        if (s.Pattern is "lattice" or "diamonds" or "circles") l.Cells = s.Cells + layer;
        return l;
    }

    /// <summary>
    /// Every piece of a design, top layer first. In a stack every layer is the full size, so the edges line up;
    /// the top has the widest opening in the middle and each layer down opens less, so you look down through them.
    /// The clock numbers are on the top layer, with solid wood behind them on the layers below.
    /// </summary>
    /// <param name="mandalas">For the "mandala" pattern: the line-art design for each layer (top first). Made from the seed where missing.</param>
    public static LaserStack BuildStack(LaserSettings s, IReadOnlyDictionary<string, Glyph>? glyphs, IReadOnlyList<MandalaDocument?>? mandalas = null)
    {
        int n = LayerCount(s);
        var layers = new List<LaserResult>();
        for (int t = 0; t < n; t++)
        {
            double open = n == 1 ? 0 : (double)(n - 1 - t) / n;
            var m = mandalas != null && t < mandalas.Count ? mandalas[t] : null;
            layers.Add(BuildLayer(LayerSettings(s, t), glyphs, m, open, top: t == 0));
        }
        return new LaserStack { Layers = layers, RadiusMm = s.DiameterMm / 2, CenterHoleMm = s.CenterHoleMm, Backer = s.Backer };
    }

    /// <summary>A flat, single-layer piece.</summary>
    public static LaserResult Build(LaserSettings s, IReadOnlyDictionary<string, Glyph>? glyphs, MandalaDocument? mandala = null) =>
        BuildLayer(s, glyphs, mandala, 0, top: true);

    /// <param name="glyphs">Outline for each number text (see <see cref="NumberTexts"/>); only needed for a clock with numbers.</param>
    /// <param name="mandala">For the "mandala" pattern: the line-art design to cut. Made from the seed if null.</param>
    /// <param name="openFraction">How much of the pattern's middle is cut away as one big opening (0 = none).</param>
    /// <param name="top">The top layer carries the numbers; layers below have solid wood behind them.</param>
    private static LaserResult BuildLayer(LaserSettings s, IReadOnlyDictionary<string, Glyph>? glyphs, MandalaDocument? mandala, double openFraction, bool top)
    {
        double R = s.DiameterMm / 2;
        double bar = Math.Max(s.BarMm, s.BridgeMm);
        // Only a mandala has an outline of its own to follow; lattices always get a rim
        bool round = s.Frame != "shape" || s.Pattern != "mandala";
        double rim = round ? Math.Clamp(s.RimMm, s.BridgeMm, R * 0.4) : 0;
        // Numbers need a band between two rings, so they go with the round frame
        bool numbersInDesign = round && s.Clock && s.Numbers != "none" && glyphs != null;
        bool numbers = numbersInDesign && top;
        double band = numbersInDesign ? Math.Clamp(s.NumberBandMm, 8, R * 0.35) : 0;
        // From the outside in: rim, then the number band (a hole the numbers stand in), then a ring, then the pattern
        double numbersOuter = R - rim, numbersInner = numbersOuter - band;
        double patternR = numbersInDesign ? numbersInner - bar : R - rim;
        double openR = openFraction * patternR;

        var wood = new PathsD();
        if (rim > 0) wood.AddRange(Annulus(R - rim, R));
        if (numbers) wood.AddRange(Annulus(numbersInner - bar, numbersInner));
        // Below the top layer the number band is solid, so the numbers stand out against it
        else if (numbersInDesign) wood.AddRange(Annulus(numbersInner - bar, R));
        // A ring around the opening, so the pattern's strips end on something
        if (openR > 0) wood.AddRange(Annulus(openR, openR + bar));

        // The pattern, as strips of wood, trimmed to its circle (overlapping the ring around it so they join)
        var lines = s.Pattern == "mandala" ? MandalaLines(mandala, round ? patternR : R - bar / 2, s) : LatticeLines(s, patternR, bar);
        var strips = Stroke(lines, bar / 2);
        // With no rim the pattern reaches the edge; everything outside its outline becomes outside the piece
        double patternEdge = round ? patternR + bar / 2 : R;
        var area = openR > 0 ? Annulus(openR, patternEdge) : new PathsD { Circle(0, 0, patternEdge) };
        wood.AddRange(Clipper.Intersect(strips, area, FillRule.NonZero, Precision));

        // A solid hub for the clock movement to bear against (only on a layer with a middle)
        bool hole = s.CenterHoleMm > 0 && openR <= 0;
        if (hole) wood.Add(Circle(0, 0, s.CenterHoleMm / 2 + Math.Max(bar, 4)));

        int bridges = 0;
        if (numbers)
        {
            var (numberWood, numberBridges) = NumberShapes(s, glyphs!, (numbersInner + numbersOuter) / 2, band);
            wood.AddRange(numberWood);
            bridges += numberBridges;
        }

        wood = Clipper.Union(wood, FillRule.NonZero);

        // Holes too thin to cut cleanly are filled in: shrink the holes by half the minimum and grow them back,
        // which drops slivers and rounds sharp hole corners to a radius the laser can follow.
        var disc = new PathsD { Circle(0, 0, R) };
        var holes = Clipper.Difference(disc, wood, FillRule.NonZero, Precision);
        double m = Math.Max(0.1, s.MinHoleMm / 2);
        holes = Clipper.InflatePaths(holes, -m, JoinType.Round, EndType.Polygon, 2, Precision, ArcTol);
        holes = Clipper.InflatePaths(holes, m, JoinType.Round, EndType.Polygon, 2, Precision, ArcTol);
        wood = Clipper.Difference(disc, holes, FillRule.NonZero, Precision);

        // Loose bits: anything not joined to the main piece would fall out
        var (main, loose) = Pieces(wood);
        if (s.Loose == "bridge")
        {
            // A few rounds, since a bridged piece can be what the next one reaches
            double crumb = Math.Max(4 * s.MinHoleMm * s.MinHoleMm, 12);
            for (int pass = 0; pass < 4 && loose.Count > 0; pass++)
            {
                var extra = new PathsD();
                foreach (var piece in loose)
                {
                    if (Math.Abs(Clipper.Area(piece)) < crumb) continue;
                    if (BridgeToMain(piece, main, Math.Max(s.BridgeMm, bar * 0.6), R) is PathsD b) { extra.AddRange(b); bridges++; }
                }
                if (extra.Count == 0) break;
                wood = Clipper.Union(wood, extra, FillRule.NonZero, Precision);
                (main, loose) = Pieces(wood);
            }
        }
        int removed = loose.Count;
        wood = main;

        if (hole)
            wood = Clipper.Difference(wood, new PathsD { Circle(0, 0, s.CenterHoleMm / 2) }, FillRule.NonZero, Precision);

        int holeCount = wood.Count(p => !Clipper.IsPositive(p));
        double cut = wood.Sum(Perimeter);
        return new LaserResult
        {
            Material = wood, RadiusMm = R, CenterHoleMm = hole ? s.CenterHoleMm : 0, Backer = s.Backer,
            Holes = holeCount, Pieces = 1, LooseRemoved = removed, Bridges = bridges, CutLengthMm = cut,
        };
    }

    // ---------- Patterns ----------

    /// <summary>Interlocking squares on a grid (or the same turned 45°), or overlapping rings on a hexagonal grid.</summary>
    private static List<(PathD Path, bool Closed)> LatticeLines(LaserSettings s, double r, double bar)
    {
        var result = new List<(PathD, bool)>();
        int cells = Math.Clamp(s.Cells, 2, 40);
        double c = 2 * r / cells;
        int n = cells / 2 + 2;
        if (s.Pattern == "circles")
        {
            // Rings centered on a hexagonal grid, each passing through its neighbors' centers
            double rr = c * 0.95;
            for (int j = -n * 2; j <= n * 2; j++)
                for (int i = -n * 2; i <= n * 2; i++)
                {
                    double x = (i + (j & 1) * 0.5) * rr, y = j * rr * Math.Sqrt(3) / 2;
                    if (Math.Sqrt(x * x + y * y) > r + rr) continue;
                    result.Add((Circle(x, y, rr), true));
                }
            return result;
        }
        // Squares on alternate grid points, each overlapping its diagonal neighbors so they interlock
        double angle = s.Pattern == "diamonds" ? Math.PI / 4 : 0;
        double half = c * 0.75;
        for (int j = -n; j <= n; j++)
            for (int i = -n; i <= n; i++)
            {
                if (((i + j) & 1) != 0) continue;
                double x = i * c, y = j * c;
                if (Math.Sqrt(x * x + y * y) > r + 2 * c) continue;
                var sq = new PathD
                {
                    new(x - half, y - half), new(x + half, y - half), new(x + half, y + half), new(x - half, y + half),
                };
                result.Add((Rotate(sq, angle), true));
            }
        return result;
    }

    /// <summary>Every line of a line-art mandala sized to the pattern circle: outlines, inner lines and details.</summary>
    private static List<(PathD Path, bool Closed)> MandalaLines(MandalaDocument? doc, double r, LaserSettings s)
    {
        doc ??= MakeMandala(s, r);
        double reach = Rendering.SvgWriter.ContentRadius(doc) ?? r;
        double scale = r / Math.Max(1, reach);
        var result = new List<(PathD, bool)>();
        foreach (var layer in doc.Layers)
        {
            if (!layer.Visible) continue;
            var (closed, open) = ShapeGenerator.BuildPathParts(layer);
            foreach (string data in new[] { closed, open, ShapeGenerator.BuildDetailData(layer) })
            {
                if (string.IsNullOrWhiteSpace(data)) continue;
                foreach (var line in Geom.PathData.ToPolylines(data, Flatten / scale))
                {
                    if (line.Count < 2) continue;
                    var path = new PathD(line.Count);
                    foreach (var p in line) path.Add(new PointD(p.X * scale, p.Y * scale));
                    bool isClosed = line.Count > 2 && Math.Abs(line[0].X - line[^1].X) < 1e-6 && Math.Abs(line[0].Y - line[^1].Y) < 1e-6;
                    result.Add((path, isClosed));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// A line-art mandala for cutting. The detail level keeps the spaces big enough for wood. With no rim the piece's
    /// edge is the mandala's border, so it picks one bordered with petals (a flower outline) rather than a polygon or scallops.
    /// </summary>
    public static MandalaDocument MakeMandala(LaserSettings s, double patternRadius)
    {
        MandalaDocument doc = RandomMandala.CreateLineArt(new Random(s.Seed), patternRadius * 2 / 0.95, null, s.Detail);
        if (s.Frame != "shape") return doc;
        for (int i = 1; i < 40 && !doc.Layers.Any(l => l.Name == "Border petals"); i++)
            doc = RandomMandala.CreateLineArt(new Random(s.Seed + i * 7919), patternRadius * 2 / 0.95, null, s.Detail);
        return doc;
    }

    // ---------- Numbers ----------

    /// <summary>
    /// The 12 numbers as wood standing in the number band. Each part of a number is tied to the rim and to the ring
    /// inside it by short bridges at its outermost and innermost points, so nothing falls out.
    /// </summary>
    private static (PathsD Wood, int Bridges) NumberShapes(LaserSettings s, IReadOnlyDictionary<string, Glyph> glyphs, double rc, double band)
    {
        var wood = new PathsD();
        int bridges = 0;
        var texts = NumberTexts(s);
        double targetH = band * 0.8;
        for (int k = 0; k < 12; k++)
        {
            if (!glyphs.TryGetValue(texts[k], out var g) || g.Height <= 0) continue;
            double theta = k * Math.PI / 6; // 0 at 12 o'clock, clockwise
            double cx = rc * Math.Sin(theta), cy = -rc * Math.Cos(theta);
            double scale = targetH / g.Height;
            bool radial = s.NumberAngle == "radial";
            if (!radial)
            {
                // Upright numbers at 3 and 9 o'clock lie across the band, so shrink wide ones to fit its depth
                double radialExtent = Math.Abs(g.Height * scale * Math.Cos(theta)) + Math.Abs(g.Width * scale * Math.Sin(theta));
                if (radialExtent > band * 0.86) scale *= band * 0.86 / radialExtent;
            }
            double rot = radial ? theta : 0;
            var shapes = new PathsD();
            foreach (var line in Geom.PathData.ToPolylines(g.PathData, 0.5))
            {
                if (line.Count < 3) continue;
                var path = new PathD(line.Count);
                foreach (var p in line)
                {
                    double x = p.X * scale, y = p.Y * scale;
                    path.Add(new PointD(cx + x * Math.Cos(rot) - y * Math.Sin(rot), cy + x * Math.Sin(rot) + y * Math.Cos(rot)));
                }
                shapes.Add(path);
            }
            // Fonts draw counters (the hole in a 0) the other way round, so even-odd keeps them as holes
            shapes = Clipper.Union(shapes, FillRule.EvenOdd);
            // Thin strokes in a font can be finer than the wood allows; thicken them to the bridge width
            var minStroke = Clipper.InflatePaths(Clipper.InflatePaths(shapes, -s.BridgeMm / 2, JoinType.Round, EndType.Polygon, 2, Precision, ArcTol),
                s.BridgeMm / 2, JoinType.Round, EndType.Polygon, 2, Precision, ArcTol);
            shapes = Clipper.Union(shapes, Clipper.InflatePaths(Clipper.Difference(shapes, minStroke, FillRule.NonZero, Precision),
                s.BridgeMm * 0.3, JoinType.Round, EndType.Polygon, 2, Precision, ArcTol), FillRule.NonZero, Precision);

            var (parts, _) = Pieces(shapes, keepAll: true);
            foreach (var part in parts)
            {
                foreach (var outer in part.Where(Clipper.IsPositive))
                {
                    // Bridges straight out to the rim and in to the ring, from the part's farthest and nearest points
                    PointD far = outer.MaxBy(p => p.x * p.x + p.y * p.y), near = outer.MinBy(p => p.x * p.x + p.y * p.y);
                    wood.AddRange(Stroke(new() { (Radial(far, rc + band / 2 + s.BridgeMm), false) }, s.BridgeMm / 2));
                    wood.AddRange(Stroke(new() { (Radial(near, rc - band / 2 - s.BridgeMm), false) }, s.BridgeMm / 2));
                    bridges += 2;
                }
            }
            wood.AddRange(shapes);
        }
        return (wood, bridges);
    }

    /// <summary>A line from p straight away from (or toward) the center, ending at radius toR.</summary>
    private static PathD Radial(PointD p, double toR)
    {
        double len = Math.Sqrt(p.x * p.x + p.y * p.y);
        if (len < 1e-6) return new PathD { p, p };
        return new PathD { p, new PointD(p.x / len * toR, p.y / len * toR) };
    }

    /// <summary>
    /// Bridges that tie a loose piece back to the main piece. A piece that goes all the way around the center (a ring of
    /// pattern) gets several, spaced evenly so it hangs straight; any other piece gets one outward and one inward.
    /// Each runs straight along the radius, just far enough to reach the main piece. Null if none reach.
    /// </summary>
    private static PathsD? BridgeToMain(PathsD piece, PathsD main, double width, double R)
    {
        var outer = piece.Where(Clipper.IsPositive).OrderByDescending(p => Math.Abs(Clipper.Area(p))).FirstOrDefault();
        if (outer == null) return null;
        bool In(PathsD shape, PointD q)
        {
            bool inside = false;
            foreach (var p in shape) // an outline comes before its holes
                if (Clipper.PointInPolygon(q, p) == PointInPolygonResult.IsInside) inside = Clipper.IsPositive(p);
            return inside;
        }

        var starts = new List<(PointD Start, double Dir)>();
        if (Clipper.PointInPolygon(new PointD(0, 0), outer) != PointInPolygonResult.IsOutside)
        {
            // Around the center: cast rays at even angles and bridge from where each leaves the piece
            double reach = outer.Max(p => Math.Sqrt(p.x * p.x + p.y * p.y));
            int n = Math.Clamp((int)Math.Round(2 * Math.PI * reach / 70), 4, 12);
            for (int k = 0; k < n; k++)
            {
                double a = 2 * Math.PI * (k + 0.5) / n, ux = Math.Sin(a), uy = -Math.Cos(a);
                double last = -1;
                for (double t = 0; t <= reach + 0.5; t += 0.5)
                    if (In(piece, new PointD(ux * t, uy * t))) last = t;
                if (last > 0) starts.Add((new PointD(ux * last, uy * last), 1));
            }
        }
        else
        {
            starts.Add((outer.MaxBy(p => p.x * p.x + p.y * p.y), 1));
            starts.Add((outer.MinBy(p => p.x * p.x + p.y * p.y), -1));
        }

        var lines = new List<(PathD, bool)>();
        foreach (var (start, dir) in starts)
        {
            double len = Math.Sqrt(start.x * start.x + start.y * start.y);
            if (len < 1e-6) continue;
            double ux = start.x / len * dir, uy = start.y / len * dir;
            for (double t = 0.5; t < 2 * R; t += 0.5)
            {
                var q = new PointD(start.x + ux * t, start.y + uy * t);
                if (q.x * q.x + q.y * q.y > R * R || (dir < 0 && t > len)) break;
                if (!In(main, q)) continue;
                // Reach a little into both pieces so they really join
                lines.Add((new PathD { new(start.x - ux * width * 0.5, start.y - uy * width * 0.5), new(q.x + ux * width, q.y + uy * width) }, false));
                break;
            }
        }
        return lines.Count == 0 ? null : Stroke(lines, width / 2);
    }

    // ---------- Geometry helpers ----------

    private static PathsD Stroke(List<(PathD Path, bool Closed)> lines, double halfWidth)
    {
        var closed = new PathsD();
        var open = new PathsD();
        foreach (var (p, c) in lines) (c ? closed : open).Add(p);
        var result = new PathsD();
        if (closed.Count > 0) result.AddRange(Clipper.InflatePaths(closed, halfWidth, JoinType.Round, EndType.Joined, 2, Precision, ArcTol));
        if (open.Count > 0) result.AddRange(Clipper.InflatePaths(open, halfWidth, JoinType.Round, EndType.Round, 2, Precision, ArcTol));
        return Clipper.Union(result, FillRule.NonZero);
    }

    /// <summary>
    /// Splits wood into separate pieces (each an outline with its holes). The biggest is the main piece; with keepAll
    /// every piece comes back in the first list.
    /// </summary>
    private static (PathsD Main, List<PathsD> Loose) Pieces(PathsD wood) { var (all, _) = Pieces(wood, keepAll: true); return Split(all); }

    private static (List<PathsD> All, int Count) Pieces(PathsD wood, bool keepAll)
    {
        var tree = new PolyTreeD();
        Clipper.BooleanOp(ClipType.Union, wood, new PathsD(), tree, FillRule.NonZero, Precision);
        var pieces = new List<PathsD>();
        void Walk(PolyPathD node)
        {
            // node is an outline: it and its holes make one piece; anything inside a hole is another piece
            var piece = new PathsD { node.Polygon! };
            for (int i = 0; i < node.Count; i++)
            {
                var hole = node[i];
                piece.Add(hole.Polygon!);
                for (int j = 0; j < hole.Count; j++) Walk(hole[j]);
            }
            pieces.Add(piece);
        }
        for (int i = 0; i < tree.Count; i++) Walk(tree[i]);
        return (pieces, pieces.Count);
    }

    private static (PathsD Main, List<PathsD> Loose) Split(List<PathsD> pieces)
    {
        if (pieces.Count == 0) return (new PathsD(), new List<PathsD>());
        var main = pieces.OrderByDescending(p => Math.Abs(Clipper.Area(p[0]))).First();
        return (main, pieces.Where(p => p != main).ToList());
    }

    private static PathD Circle(double x, double y, double r)
    {
        int steps = Math.Clamp((int)Math.Ceiling(2 * Math.PI * r / 0.6), 48, 1440);
        var p = new PathD(steps);
        for (int i = 0; i < steps; i++)
        {
            double a = 2 * Math.PI * i / steps;
            p.Add(new PointD(x + r * Math.Cos(a), y + r * Math.Sin(a)));
        }
        return p;
    }

    private static PathsD Annulus(double inner, double outer)
    {
        var o = Circle(0, 0, outer);
        var i = Circle(0, 0, inner);
        i.Reverse();
        return Clipper.Union(new PathsD { o, i }, FillRule.NonZero);
    }

    private static PathD Rotate(PathD p, double a)
    {
        if (a == 0) return p;
        double c = Math.Cos(a), s = Math.Sin(a);
        var r = new PathD(p.Count);
        foreach (var q in p) r.Add(new PointD(q.x * c - q.y * s, q.x * s + q.y * c));
        return r;
    }

    private static double Perimeter(PathD p)
    {
        double len = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Count];
            len += Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));
        }
        return len;
    }

    // ---------- Output ----------

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string PathDataOf(PathsD paths)
    {
        var sb = new StringBuilder();
        foreach (var p in paths)
        {
            if (p.Count < 3) continue;
            sb.Append('M').Append(F(p[0].x)).Append(',').Append(F(p[0].y));
            for (int i = 1; i < p.Count; i++) sb.Append('L').Append(F(p[i].x)).Append(',').Append(F(p[i].y));
            sb.Append('Z');
        }
        return sb.ToString();
    }

    /// <summary>
    /// The file for the laser: every edge as a red hairline (the usual "cut" color), at true size in mm.
    /// The pieces sit side by side on one sheet, top layer first, then the backing disc; each is a group named for its layer.
    /// </summary>
    public static string LaserSvg(LaserStack st)
    {
        double R = st.RadiusMm, gap = 10;
        int count = st.Layers.Count + (st.Backer ? 1 : 0);
        int cols = Math.Min(count, 3), rows = (count + cols - 1) / cols;
        double width = cols * 2 * R + (cols - 1) * gap, height = rows * 2 * R + (rows - 1) * gap;
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(width)}mm\" height=\"{F(height)}mm\" viewBox=\"0 0 {F(width)} {F(height)}\">");
        const string cut = "fill=\"none\" stroke=\"#FF0000\" stroke-width=\"0.1\"";
        (double X, double Y) Spot(int i) => (R + (i % cols) * (2 * R + gap), R + (i / cols) * (2 * R + gap));
        for (int i = 0; i < st.Layers.Count; i++)
        {
            var (x, y) = Spot(i);
            string label = st.Layers.Count == 1 ? "Top layer" : i == 0 ? "Layer 1 (top)" : i == st.Layers.Count - 1 ? $"Layer {i + 1} (bottom)" : $"Layer {i + 1}";
            sb.AppendLine($"  <g id=\"layer-{i + 1}\" transform=\"translate({F(x)} {F(y)})\"><title>{label} (cut)</title><path {cut} d=\"{PathDataOf(st.Layers[i].Material)}\"/></g>");
        }
        if (st.Backer)
        {
            var (x, y) = Spot(st.Layers.Count);
            sb.Append($"  <g id=\"backing-disc\" transform=\"translate({F(x)} {F(y)})\"><title>Backing disc (cut)</title>");
            sb.Append($"<circle {cut} cx=\"0\" cy=\"0\" r=\"{F(R)}\"/>");
            if (st.CenterHoleMm > 0) sb.Append($"<circle {cut} cx=\"0\" cy=\"0\" r=\"{F(st.CenterHoleMm / 2)}\"/>");
            sb.AppendLine("</g>");
        }
        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    /// <summary>
    /// A picture of the finished piece. Layers are drawn bottom up, from light wood to dark, each with a soft shadow,
    /// so a stack looks 3D; the backing disc (if any) is the lightest, under everything.
    /// </summary>
    public static string PreviewSvg(LaserStack st)
    {
        double R = st.RadiusMm;
        int n = st.Layers.Count;
        bool shadows = n > 1 || st.Backer;
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(2 * R)}mm\" height=\"{F(2 * R)}mm\" viewBox=\"{F(-R * 1.02)} {F(-R * 1.02)} {F(2.04 * R)} {F(2.04 * R)}\">");
        if (shadows)
            sb.AppendLine($"  <defs><filter id=\"lift\" x=\"-5%\" y=\"-5%\" width=\"110%\" height=\"110%\">" +
                // A plain blur-offset-merge shadow, which every SVG viewer understands (feDropShadow isn't everywhere)
                $"<feGaussianBlur in=\"SourceAlpha\" stdDeviation=\"{F(R * 0.006)}\"/><feOffset dx=\"{F(R * 0.004)}\" dy=\"{F(R * 0.008)}\" result=\"s\"/>" +
                "<feComponentTransfer in=\"s\" result=\"s2\"><feFuncA type=\"linear\" slope=\"0.45\"/></feComponentTransfer>" +
                "<feMerge><feMergeNode in=\"s2\"/><feMergeNode in=\"SourceGraphic\"/></feMerge></filter></defs>");
        if (st.Backer) sb.AppendLine($"  <circle cx=\"0\" cy=\"0\" r=\"{F(R)}\" fill=\"#E3C495\"/>");
        for (int i = n - 1; i >= 0; i--)
        {
            // Top layer darkest, bottom lightest
            double f = n == 1 ? 0 : (double)i / (n - 1);
            sb.AppendLine($"  <path fill=\"{Mix("#5B3A21", "#C99A62", f)}\" fill-rule=\"evenodd\"{(shadows ? " filter=\"url(#lift)\"" : "")} d=\"{PathDataOf(st.Layers[i].Material)}\"/>");
        }
        if (st.CenterHoleMm > 0) sb.AppendLine($"  <circle cx=\"0\" cy=\"0\" r=\"{F(st.CenterHoleMm / 2)}\" fill=\"#222\"/>");
        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    /// <summary>One layer on its own, for checking it before cutting.</summary>
    public static string LayerPreviewSvg(LaserResult layer)
    {
        double r = layer.RadiusMm;
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(2 * r)}mm\" height=\"{F(2 * r)}mm\" viewBox=\"{F(-r)} {F(-r)} {F(2 * r)} {F(2 * r)}\">");
        sb.AppendLine($"  <path fill=\"#8A5A33\" fill-rule=\"evenodd\" d=\"{PathDataOf(layer.Material)}\"/>");
        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static string Mix(string a, string b, double f)
    {
        int C(string h, int i) => Convert.ToInt32(h.Substring(1 + 2 * i, 2), 16);
        return "#" + string.Concat(Enumerable.Range(0, 3).Select(i => ((int)Math.Round(C(a, i) + (C(b, i) - C(a, i)) * f)).ToString("X2")));
    }
}
