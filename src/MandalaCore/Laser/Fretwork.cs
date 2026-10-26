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
    /// <summary>"lattice" (interlocking squares), "diamonds" (the lattice turned 45°), "circles" (overlapping rings), "mandala" or "wreath" (leaves and berries).</summary>
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
    /// <summary>Outline of the piece with the rim: "round" or "scalloped".</summary>
    public string Edge { get; set; } = "round";
    /// <summary>How many scallops around a scalloped edge.</summary>
    public int Scallops { get; set; } = 16;
    /// <summary>In a stack, how much smaller each layer is than the one below it, so every edge shows (0 = flush).</summary>
    public double LayerStepMm { get; set; }
    /// <summary>Preview colors: "alternate" (walnut and birch) or "gradient" (dark on top to light below).</summary>
    public string Look { get; set; } = "alternate";
    /// <summary>How many lattice cells fit across the pattern.</summary>
    public int Cells { get; set; } = 9;
    public Difficulty Detail { get; set; } = Difficulty.Simple;
    public int Seed { get; set; } = 1;

    public bool Clock { get; set; } = true;
    /// <summary>"arabic" (1–12), "roman" (I–XII) or "none".</summary>
    public string Numbers { get; set; } = "arabic";
    /// <summary>
    /// Where the numbers go: "band" (all 12 in a ring near the edge), "medallions" (12, 3, 6 and 9 in round medallions on a ring)
    /// or "face" (12, 3, 6 and 9 and hour dots cut through a solid face in the middle, on the bottom layer).
    /// </summary>
    public string Placement { get; set; } = "band";
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
    /// <summary>The backing disc's outline: the bottom layer's outer edge, filled solid.</summary>
    public PathsD BackerShape { get; init; } = new();
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
    /// <summary>Where the top layer's medallions sit (radius of their centers) and how big they are; 0 without medallions.</summary>
    public double MedallionRingMm { get; init; }
    public double MedallionMm { get; init; }
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
            // With a face, every layer above the bottom opens wide enough to show it
            if (s.Clock && s.Placement == "face" && t < n - 1) open = Math.Max(open, FaceFraction + 0.04);
            var m = mandalas != null && t < mandalas.Count ? mandalas[t] : null;
            double inset = Math.Max(0, s.LayerStepMm) * (n - 1 - t); // the top layer is the smallest
            // Layers below the top get a solid disc under each medallion, so its number shows in plain wood
            var under = t == 0 ? null : layers[0];
            layers.Add(BuildLayer(LayerSettings(s, t), glyphs, m, open, top: t == 0, bottom: t == n - 1, inset,
                under?.MedallionRingMm ?? 0, under?.MedallionMm ?? 0));
        }
        bool round = s.Frame != "shape" || s.Pattern != "mandala";
        var back = round ? Outline(s, s.DiameterMm / 2, out _) : new PathsD { Circle(0, 0, s.DiameterMm / 2) };
        return new LaserStack { Layers = layers, RadiusMm = s.DiameterMm / 2, CenterHoleMm = s.CenterHoleMm, Backer = s.Backer, BackerShape = back };
    }

    /// <summary>A flat, single-layer piece.</summary>
    public static LaserResult Build(LaserSettings s, IReadOnlyDictionary<string, Glyph>? glyphs, MandalaDocument? mandala = null) =>
        BuildLayer(s, glyphs, mandala, 0, top: true, bottom: true, 0);

    /// <summary>How much of the pattern a solid clock face covers.</summary>
    private const double FaceFraction = 0.6;

    private static readonly string[] Quarter = { "12", "3", "6", "9" };
    private static readonly string[] QuarterRoman = { "XII", "III", "VI", "IX" };

    /// <summary>The outline of the whole piece: a circle, or a circle with scallops around it.</summary>
    private static PathsD Outline(LaserSettings s, double r, out double inner)
    {
        inner = r;
        if (s.Edge != "scalloped") return new PathsD { Circle(0, 0, r) };
        int n = Math.Clamp(s.Scallops, 6, 48);
        double bump = Math.PI * r / n * 0.62; // scallop radius: neighbors overlap a little so the edge stays smooth
        double baseR = r - bump * 0.55;
        inner = baseR;
        var parts = new PathsD { Circle(0, 0, baseR) };
        for (int k = 0; k < n; k++)
        {
            double a = 2 * Math.PI * k / n;
            parts.Add(Circle((r - bump) * Math.Sin(a), -(r - bump) * Math.Cos(a), bump));
        }
        return Clipper.Union(parts, FillRule.NonZero);
    }

    /// <param name="glyphs">Outline for each number text (see <see cref="NumberTexts"/>); only needed for a clock with numbers.</param>
    /// <param name="mandala">For the "mandala" pattern: the line-art design to cut. Made from the seed if null.</param>
    /// <param name="openFraction">How much of the pattern's middle is cut away as one big opening (0 = none).</param>
    /// <param name="top">The top layer carries the numbers; layers below have solid wood behind them.</param>
    /// <param name="bottom">The bottom layer carries a solid clock face, if there is one.</param>
    /// <param name="inset">How much smaller than full size this layer is (stepped stacks).</param>
    private static LaserResult BuildLayer(LaserSettings s, IReadOnlyDictionary<string, Glyph>? glyphs, MandalaDocument? mandala,
        double openFraction, bool top, bool bottom, double inset, double medallionRing = 0, double medallionR = 0)
    {
        double R = s.DiameterMm / 2 - inset;
        double bar = Math.Max(s.BarMm, s.BridgeMm);
        // Only a mandala has an outline of its own to follow; other patterns always get a rim
        bool round = s.Frame != "shape" || s.Pattern != "mandala";
        double rim = round ? Math.Clamp(s.RimMm, s.BridgeMm, R * 0.4) : 0;
        double innerR = R;
        var outline = round ? Outline(s, R, out innerR) : new PathsD { Circle(0, 0, R) };
        bool clock = round && s.Clock && s.Numbers != "none" && glyphs != null;
        string placement = clock ? s.Placement : "none";
        // In the band style, the numbers stand in a band between two rings near the edge
        bool inBand = placement == "band";
        bool numbers = inBand && top;
        double band = inBand ? Math.Clamp(s.NumberBandMm, 8, R * 0.35) : 0;
        // From the outside in: rim, then the number band (a hole the numbers stand in), then a ring, then the pattern
        double numbersOuter = innerR - rim, numbersInner = numbersOuter - band;
        double patternR = inBand ? numbersInner - bar : innerR - rim;
        double openR = openFraction * patternR;
        // A solid face in the middle of the bottom layer, with the numbers cut through it
        double faceR = placement == "face" && bottom ? FaceFraction * patternR : 0;

        var wood = new PathsD();
        if (rim > 0)
            wood.AddRange(Clipper.Difference(outline, Clipper.InflatePaths(outline, -rim, JoinType.Round, EndType.Polygon, 2, Precision, ArcTol), FillRule.NonZero, Precision));
        if (numbers) wood.AddRange(Annulus(numbersInner - bar, numbersInner));
        // Below the top layer the number band is solid, so the numbers stand out against it
        else if (inBand) wood.AddRange(Annulus(numbersInner - bar, innerR - rim + bar));
        // A ring around the opening, so the pattern's strips end on something
        if (openR > 0) wood.AddRange(Annulus(openR, openR + bar));
        if (faceR > 0) wood.Add(Circle(0, 0, faceR));

        // The pattern, trimmed to its circle (overlapping the ring around it so they join)
        double patternEdge = round ? patternR + bar / 2 : R;
        double patternInner = Math.Max(openR, faceR);
        var area = patternInner > 0 ? Annulus(patternInner, patternEdge) : new PathsD { Circle(0, 0, patternEdge) };
        var cuts = new PathsD();
        if (s.Pattern == "wreath")
        {
            // The wreath fills the band between the face (or opening) and the rim; without either it keeps clear of the middle
            double inner = patternInner > 0 ? patternInner : patternR * 0.5;
            if (patternInner <= 0) wood.AddRange(Annulus(inner - bar, inner));
            var (solids, wreathLines, slots) = Wreath(s, inner, patternR, bar);
            wood.AddRange(Clipper.Intersect(Clipper.Union(solids, Stroke(wreathLines, bar * 0.4), FillRule.NonZero, Precision), area, FillRule.NonZero, Precision));
            cuts.AddRange(slots);
        }
        else
        {
            var lines = s.Pattern == "mandala" ? MandalaLines(mandala, round ? patternR : R - bar / 2, s) : LatticeLines(s, patternR, bar);
            wood.AddRange(Clipper.Intersect(Stroke(lines, bar / 2), area, FillRule.NonZero, Precision));
        }

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

        // Medallions: four round plaques on the ring around the top layer's opening, each with its number cut through
        var numberHoles = new PathsD();
        if (placement == "medallions" && top)
        {
            double ringR = openR > 0 ? openR + bar / 2 : patternR * 0.62;
            if (openR <= 0) wood.AddRange(Annulus(ringR - bar / 2, ringR + bar / 2));
            double mr = Math.Clamp(s.NumberBandMm * 0.5, 8, patternR * 0.18);
            for (int q = 0; q < 4; q++)
            {
                double a = q * Math.PI / 2;
                wood.Add(Circle(ringR * Math.Sin(a), -ringR * Math.Cos(a), mr));
            }
            numberHoles.AddRange(QuarterNumbers(s, glyphs!, ringR, mr * 1.35, upright: true));
            medallionRing = ringR;
            medallionR = mr;
        }
        else if (placement == "medallions" && medallionRing > 0)
        {
            for (int q = 0; q < 4; q++)
            {
                double a = q * Math.PI / 2;
                wood.Add(Circle(medallionRing * Math.Sin(a), -medallionRing * Math.Cos(a), medallionR + bar));
            }
        }
        // A face: 12, 3, 6 and 9 near its edge and a dot at every other hour, all cut through
        if (faceR > 0)
        {
            double size = Math.Clamp(faceR * 0.26, 8, 70);
            numberHoles.AddRange(QuarterNumbers(s, glyphs!, faceR - size * 0.75, size, upright: true));
            double dot = Math.Max(s.MinHoleMm * 1.3, faceR * 0.035);
            for (int k = 0; k < 12; k++)
            {
                if (k % 3 == 0) continue;
                double a = k * Math.PI / 6, r = faceR - size * 0.75;
                numberHoles.Add(Circle(r * Math.Sin(a), -r * Math.Cos(a), dot / 2));
            }
        }

        wood = Clipper.Union(wood, FillRule.NonZero);
        if (cuts.Count > 0) wood = Clipper.Difference(wood, cuts, FillRule.NonZero, Precision);
        if (numberHoles.Count > 0) wood = Clipper.Difference(wood, numberHoles, FillRule.NonZero, Precision);

        // Holes too thin to cut cleanly are filled in: shrink the holes by half the minimum and grow them back,
        // which drops slivers and rounds sharp hole corners to a radius the laser can follow.
        var disc = round ? outline : new PathsD { Circle(0, 0, R) };
        var holes = Clipper.Difference(disc, wood, FillRule.NonZero, Precision);
        double m = Math.Max(0.1, s.MinHoleMm / 2);
        holes = Clipper.InflatePaths(holes, -m, JoinType.Round, EndType.Polygon, 2, Precision, ArcTol);
        holes = Clipper.InflatePaths(holes, m, JoinType.Round, EndType.Polygon, 2, Precision, ArcTol);
        wood = Clipper.Difference(disc, holes, FillRule.NonZero, Precision);

        // Loose bits: anything not joined to the main piece would fall out
        var (main, loose) = Pieces(wood);
        bool InNumber(PathsD piece) => numberHoles.Count > 0 && piece.Count > 0 &&
            numberHoles.Any(h => Clipper.PointInPolygon(piece[0][0], h) == PointInPolygonResult.IsInside);
        if (s.Loose == "bridge" || loose.Any(InNumber))
        {
            // A few rounds, since a bridged piece can be what the next one reaches
            double crumb = Math.Max(4 * s.MinHoleMm * s.MinHoleMm, 12);
            for (int pass = 0; pass < 4 && loose.Count > 0; pass++)
            {
                var extra = new PathsD();
                foreach (var piece in loose)
                {
                    if (s.Loose != "bridge" && !InNumber(piece)) continue;
                    if (Math.Abs(Clipper.Area(piece)) < crumb && !InNumber(piece)) continue;
                    double w = InNumber(piece) ? s.BridgeMm : Math.Max(s.BridgeMm, bar * 0.6);
                    if (BridgeToMain(piece, main, w, R) is PathsD b) { extra.AddRange(b); bridges++; }
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
            MedallionRingMm = top ? medallionRing : 0, MedallionMm = top ? medallionR : 0,
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

    /// <summary>
    /// 12, 3, 6 and 9 as shapes to cut out, centered on a circle of radius r, each about size tall. Thin strokes are
    /// widened a little so every part of the number is a hole the laser can cut.
    /// </summary>
    private static PathsD QuarterNumbers(LaserSettings s, IReadOnlyDictionary<string, Glyph> glyphs, double r, double size, bool upright)
    {
        var texts = s.Numbers == "roman" ? QuarterRoman : Quarter;
        var result = new PathsD();
        for (int q = 0; q < 4; q++)
        {
            if (!glyphs.TryGetValue(texts[q], out var g) || g.Height <= 0) continue;
            double a = q * Math.PI / 2, cx = r * Math.Sin(a), cy = -r * Math.Cos(a);
            double scale = Math.Min(size * 0.62 / g.Height, size * 0.8 / Math.Max(1, g.Width));
            double rot = upright ? 0 : a;
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
            shapes = Clipper.Union(shapes, FillRule.EvenOdd);
            result.AddRange(Clipper.InflatePaths(shapes, s.MinHoleMm * 0.25, JoinType.Round, EndType.Polygon, 2, Precision, ArcTol));
        }
        return Clipper.Union(result, FillRule.NonZero);
    }

    /// <summary>
    /// A botanical wreath between radius inner and outer: a vine with leaves sweeping clockwise (each with a cut vein),
    /// tipped in and out so they touch the rings on both sides and hold the wreath in place, and sprigs of berries.
    /// Returns solid shapes, lines (vine and stems) and the vein cuts.
    /// </summary>
    private static (PathsD Solids, List<(PathD, bool)> Lines, PathsD Cuts) Wreath(LaserSettings s, double inner, double outer, double bar)
    {
        var rng = new Random(s.Seed);
        var solids = new PathsD();
        var lines = new List<(PathD, bool)>();
        var cuts = new PathsD();
        double rv = (inner + outer) / 2, w = outer - inner;
        double gap = Math.Max(s.MinHoleMm, bar * 0.35);
        int count = Math.Max(8, (int)Math.Round(2 * Math.PI * rv / (w * 0.5)));
        // Each new leaf is cut a thin gap clear of the leaves under it, so overlapping leaves keep their own outlines
        var parts = new List<(PathsD Shape, RectD Box)>();
        void AddSolid(PathD shape)
        {
            var box = Clipper.GetBounds(shape);
            var clear = new RectD(box.left - gap, box.top - gap, box.right + gap, box.bottom + gap);
            PathsD? cutter = null;
            for (int i = 0; i < parts.Count; i++)
            {
                var b = parts[i].Box;
                if (b.right < clear.left || b.left > clear.right || b.bottom < clear.top || b.top > clear.bottom) continue;
                cutter ??= Clipper.InflatePaths(new PathsD { shape }, gap, JoinType.Round, EndType.Polygon, 2, Precision, ArcTol);
                parts[i] = (Clipper.Difference(parts[i].Shape, cutter, FillRule.NonZero, Precision), b);
            }
            parts.Add((new PathsD { shape }, box));
        }
        for (int k = 0; k < count; k++)
        {
            double a = 2 * Math.PI * (k + rng.NextDouble() * 0.25) / count;
            var at = new PointD(rv * Math.Sin(a), -rv * Math.Cos(a));
            // A pair of leaves at each point on the vine, leaning along it (clockwise): one out toward the rim, one in toward the middle
            foreach (bool outward in k % 2 == 0 ? new[] { true, false } : new[] { false, true })
            {
                double lean = (outward ? 1 : -1) * (0.5 + rng.NextDouble() * 0.3); // radians from the vine's direction
                double dirA = a + Math.PI / 2 - lean;
                var dir = new PointD(Math.Sin(dirA), -Math.Cos(dirA));
                double radial = Math.Abs(dir.x * Math.Sin(a) - dir.y * Math.Cos(a));
                // Every third point's leaves reach the rings beside them, which holds the wreath in place
                double reach = (w / 2 + bar * 0.5) / Math.Max(0.35, radial);
                double len = k % 3 == 0 ? reach : reach * (0.72 + rng.NextDouble() * 0.16);
                len = Math.Min(len, w * 0.95);
                double width = len * (0.32 + rng.NextDouble() * 0.07);
                AddSolid(Leaf(at, dir, len, width));
                // A vein down the middle, as a slim cut (wide enough to cut cleanly)
                double vw = Math.Max(s.MinHoleMm * 1.15, width * 0.14);
                if (width > vw * 3.2 && len > vw * 8)
                {
                    var v0 = new PointD(at.x + dir.x * len * 0.22, at.y + dir.y * len * 0.22);
                    cuts.Add(Leaf(v0, dir, len * 0.6, vw));
                }
            }
            // Every fourth point also gets a sprig of berries on a short stem
            if (k % 4 == 1)
            {
                double ba = a + Math.PI / 2 + (k % 8 == 1 ? -1.2 : 1.2);
                var bd = new PointD(Math.Sin(ba), -Math.Cos(ba));
                double stem = w * 0.26, br = Math.Max(s.MinHoleMm, w * 0.045);
                var tip = new PointD(at.x + bd.x * stem, at.y + bd.y * stem);
                lines.Add((new PathD { at, tip }, false));
                for (int b = 0; b < 3; b++)
                {
                    double off = (b - 1) * 0.9;
                    var bp = new PointD(tip.x + (bd.x * Math.Cos(off) - bd.y * Math.Sin(off)) * br * 1.7, tip.y + (bd.x * Math.Sin(off) + bd.y * Math.Cos(off)) * br * 1.7);
                    AddSolid(Circle(bp.x, bp.y, br));
                }
            }
        }
        // The vine goes over everything, joining all the leaves into one piece
        lines.Add((Circle(0, 0, rv), true));
        foreach (var part in parts) solids.AddRange(part.Shape);
        return (Clipper.Union(solids, FillRule.NonZero), lines, Clipper.Union(cuts, FillRule.NonZero));
    }

    /// <summary>A leaf (two arcs meeting in points) from base along dir.</summary>
    private static PathD Leaf(PointD baseP, PointD dir, double length, double width)
    {
        var p = new PathD();
        var nrm = new PointD(-dir.y, dir.x);
        const int steps = 24;
        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps, off = Math.Sin(Math.PI * t) * width / 2 * (1 - 0.25 * t);
            p.Add(new PointD(baseP.x + dir.x * length * t + nrm.x * off, baseP.y + dir.y * length * t + nrm.y * off));
        }
        for (int i = steps - 1; i > 0; i--)
        {
            double t = (double)i / steps, off = Math.Sin(Math.PI * t) * width / 2 * (1 - 0.25 * t);
            p.Add(new PointD(baseP.x + dir.x * length * t - nrm.x * off, baseP.y + dir.y * length * t - nrm.y * off));
        }
        if (!Clipper.IsPositive(p)) p.Reverse();
        return p;
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
            sb.Append($"<path {cut} d=\"{PathDataOf(BackerOutline(st))}\"/>");
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
    /// <param name="look">"alternate" (walnut and birch, like two-tone clocks) or "gradient" (dark on top to light below).</param>
    public static string PreviewSvg(LaserStack st, string look = "gradient")
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
        const string walnut = "#4E301D", birch = "#DDBF8F";
        string Color(int i) => look == "alternate" ? (i % 2 == 0 ? walnut : birch) : Mix("#5B3A21", "#C99A62", n == 1 ? 0 : (double)i / (n - 1));
        // The backing disc takes the bottom layer's outline (or a circle) in the other wood
        if (st.Backer)
        {
            string backColor = look == "alternate" ? (n % 2 == 0 ? walnut : birch) : "#E3C495";
            sb.AppendLine($"  <path fill=\"{backColor}\"{(shadows ? " filter=\"url(#lift)\"" : "")} d=\"{PathDataOf(BackerOutline(st))}\"/>");
        }
        for (int i = n - 1; i >= 0; i--)
            sb.AppendLine($"  <path fill=\"{Color(i)}\" fill-rule=\"evenodd\"{(shadows ? " filter=\"url(#lift)\"" : "")} d=\"{PathDataOf(st.Layers[i].Material)}\"/>");
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

    private static PathsD BackerOutline(LaserStack st) =>
        st.BackerShape.Count > 0 ? st.BackerShape : new PathsD { Circle(0, 0, st.RadiusMm) };

    private static string Mix(string a, string b, double f)
    {
        int C(string h, int i) => Convert.ToInt32(h.Substring(1 + 2 * i, 2), 16);
        return "#" + string.Concat(Enumerable.Range(0, 3).Select(i => ((int)Math.Round(C(a, i) + (C(b, i) - C(a, i)) * f)).ToString("X2")));
    }
}
