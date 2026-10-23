using System;
using System.Collections.Generic;
using System.Linq;

namespace MandalaMaker.Model;

public static class RandomMandala
{
    private sealed record Palette(string Name, string Background, string[] Colors);

    // Colors[0] is the darkest and is used for strokes
    private static readonly Palette[] Palettes =
    {
        new("Sunset", "#FFF8EE", new[] { "#264653", "#2A9D8F", "#E9C46A", "#F4A261", "#E76F51" }),
        new("Ocean", "#F4FBFF", new[] { "#03045E", "#0077B6", "#00B4D8", "#90E0EF", "#CAF0F8" }),
        new("Berry", "#FFF0F3", new[] { "#590D22", "#800F2F", "#C9184A", "#FF758F", "#FFCCD5" }),
        new("Forest", "#F6FBF4", new[] { "#1B4332", "#2D6A4F", "#40916C", "#74C69D", "#D8F3DC" }),
        new("Dusk", "#F2E9E4", new[] { "#22223B", "#4A4E69", "#9A8C98", "#C9ADA7", "#F2E9E4" }),
        new("Saffron", "#FFFBF0", new[] { "#370617", "#9D0208", "#E85D04", "#FAA307", "#FFE8A3" }),
        new("Lavender", "#FBF8FF", new[] { "#2D1E4F", "#5A4FCF", "#9D8DF1", "#C8B6FF", "#E7DEFF" }),
    };

    private static readonly ShapeKind[] Motifs =
        Array.FindAll(Enum.GetValues<ShapeKind>(), k => !Shapes.ShapeGenerator.IsRingShape(k));

    private static readonly ShapeKind[] RingShapes =
        Array.FindAll(Enum.GetValues<ShapeKind>(), Shapes.ShapeGenerator.IsRingShape);

    // Shapes that read well as a single center piece
    private static readonly ShapeKind[] SoloCenters =
        { ShapeKind.Star, ShapeKind.Circle, ShapeKind.Flower, ShapeKind.Gem, ShapeKind.Donut, ShapeKind.Cross };

    private static readonly ShapeKind[] LineArt = { ShapeKind.Spoke, ShapeKind.Swirl, ShapeKind.Lattice };

    /// <summary>
    /// Black line-art mandala in the style of coloring-book designs, built in concentric bands like hand-drawn mandalas.
    /// Each band sits between two rings, and its motifs are sized to fit that band exactly: petal bases on the inner ring,
    /// tips on the outer ring, neighbors touching. Alternate bands are offset half a step so tips nest between the
    /// shapes below. Detail (echoes, inner shapes, hatching) only goes on motifs big enough to color in.
    /// Only the border and its backing ring are filled (in white), so the silhouette laser modes can cut the piece out.
    /// </summary>
    /// <param name="difficulty">Simple: fewer, bigger spaces and bolder lines. Medium: in between. Intricate: the full detail.</param>
    public static MandalaDocument CreateLineArt(Random rng, double diameter = 200, Func<ShapeKind, double>? weight = null,
        Difficulty difficulty = Difficulty.Intricate)
    {
        weight ??= k => AppSettings.WeightOf(AppSettings.DefaultPreference(k));
        var doc = new MandalaDocument
        {
            DiameterMm = diameter, BackgroundColor = "#FFFFFF", NoFill = true, LaserMode = LaserMode.Raw,
        };
        var g = new LineArtBuilder(rng, weight, diameter / 2 * 0.95, difficulty);
        foreach (var l in g.Build()) doc.Layers.Add(l);
        return doc;
    }

    /// <summary>
    /// Re-rolls one layer of a design: a new shape of the same kind (tall petal-like, round bead-like or ring pattern)
    /// with new details, still fitting the same band. Returns false if there's nothing sensible to change (such as a plain ring).
    /// </summary>
    public static bool Reroll(Layer layer, Random rng, Func<ShapeKind, double> weight, double designDiameter)
    {
        var g = new LineArtBuilder(rng, weight, designDiameter / 2 * 0.95);
        return g.Reroll(layer);
    }

    /// <summary>The band-based line-art generator.</summary>
    private sealed class LineArtBuilder
    {
        private const string Ink = "#000000";
        private static readonly ShapeKind[] TallShapes =
            { ShapeKind.Petal, ShapeKind.Eye, ShapeKind.Teardrop, ShapeKind.Leaf, ShapeKind.Dome, ShapeKind.Lotus, ShapeKind.Arch, ShapeKind.Tulip, ShapeKind.Dragonfly, ShapeKind.FleurDeLis, ShapeKind.PeacockFeather, ShapeKind.Fish, ShapeKind.PineTree, ShapeKind.Fern, ShapeKind.Key, ShapeKind.Raindrop };
        private static readonly ShapeKind[] RoundShapes =
            { ShapeKind.Circle, ShapeKind.Gem, ShapeKind.Star, ShapeKind.Heart, ShapeKind.Diamond, ShapeKind.Donut, ShapeKind.Flower, ShapeKind.Daisy, ShapeKind.Butterfly, ShapeKind.Rose, ShapeKind.Quatrefoil,
              ShapeKind.LotusBloom, ShapeKind.Sunflower, ShapeKind.Snowflake, ShapeKind.MoroccanStar, ShapeKind.Ladybug, ShapeKind.Owl,
              ShapeKind.Bee, ShapeKind.Snail, ShapeKind.Mushroom, ShapeKind.Pumpkin, ShapeKind.Sun,
              ShapeKind.Hummingbird, ShapeKind.Hamsa, ShapeKind.Crown, ShapeKind.Anchor, ShapeKind.MoonStars };

        // Picture motifs already carry their own detail, so the generator doesn't add more
        private static readonly ShapeKind[] PictureShapes =
            { ShapeKind.Butterfly, ShapeKind.Dragonfly, ShapeKind.Tulip, ShapeKind.Daisy, ShapeKind.Rose, ShapeKind.FleurDeLis, ShapeKind.PeacockFeather,
              ShapeKind.LotusBloom, ShapeKind.Sunflower, ShapeKind.Snowflake, ShapeKind.Ladybug, ShapeKind.Owl,
              ShapeKind.Bee, ShapeKind.Snail, ShapeKind.Fish, ShapeKind.Mushroom, ShapeKind.PineTree, ShapeKind.Pumpkin, ShapeKind.Sun,
              ShapeKind.Hummingbird, ShapeKind.Fern, ShapeKind.Hamsa, ShapeKind.Crown, ShapeKind.Key, ShapeKind.Anchor, ShapeKind.MoonStars };
        private static readonly ShapeKind[] PatternShapes =
            { ShapeKind.Scallop, ShapeKind.Lattice, ShapeKind.Zigzag, ShapeKind.Wave, ShapeKind.Vine, ShapeKind.Rope, ShapeKind.GreekKey, ShapeKind.Waves, ShapeKind.Chain, ShapeKind.Stitch, ShapeKind.FlowerOfLife };
        private static readonly ShapeKind[] BorderShapes =
            { ShapeKind.Lotus, ShapeKind.Petal, ShapeKind.Dome, ShapeKind.Arch };

        private enum Band { Petals, Beads, Pattern }

        private readonly Random _rng;
        private readonly Func<ShapeKind, double> _weight;
        private readonly double _r;          // usable radius
        private readonly double _minFeature; // smallest motif worth drawing (about 4–5 mm on a 200 mm design)
        private readonly Difficulty _level;
        private readonly double _lineScale;  // bolder lines on easier pages
        private readonly int _maxRepeat;     // how many times the symmetry may multiply in outer bands
        private int _n;

        // Fine ring patterns are hard to color, so easier pages leave them out
        private static readonly ShapeKind[] FinePatterns =
            { ShapeKind.Lattice, ShapeKind.Rope, ShapeKind.Stitch, ShapeKind.FlowerOfLife, ShapeKind.Chain, ShapeKind.GreekKey };

        public LineArtBuilder(Random rng, Func<ShapeKind, double> weight, double radius, Difficulty level = Difficulty.Intricate)
        {
            _rng = rng;
            _level = level;
            _weight = level switch
            {
                Difficulty.Intricate => weight,
                // Sunflowers and snowflakes are dense even when large
                Difficulty.Simple => k => Array.IndexOf(FinePatterns, k) >= 0 || k is ShapeKind.Sunflower or ShapeKind.Snowflake ? 0 : weight(k),
                _ => k => Array.IndexOf(FinePatterns, k) >= 0 && k != ShapeKind.Chain ? 0 : weight(k),
            };
            _r = radius;
            (_minFeature, _lineScale, _maxRepeat) = level switch
            {
                Difficulty.Simple => (radius * 0.1, 1.6, 2),
                Difficulty.Medium => (radius * 0.065, 1.25, 4),
                _ => (radius * 0.045, 1.0, 8),
            };
        }

        private bool Chance(double p) => _rng.NextDouble() < p;
        /// <param name="length">If given, picture motifs only qualify when a motif this size (length × width) is big enough to color.</param>
        private ShapeKind? PickFrom(ShapeKind[] pool, ShapeKind? avoid = null, double? length = null, double? width = null)
        {
            double W(ShapeKind k) => length is double l && width is double w && !Fits(k, l, w) ? 0 : _weight(k);
            if (avoid is ShapeKind a && Pick(_rng, pool, k => k == a ? 0 : W(k)) is ShapeKind other) return other;
            return Pick(_rng, pool, W);
        }

        /// <summary>
        /// Butterflies, dragonflies, tulips and daisies have fine inner detail, so they need room:
        /// at least about 14% of the design radius, and wings need width close to their length.
        /// </summary>
        private bool Fits(ShapeKind k, double length, double width)
        {
            if (Array.IndexOf(PictureShapes, k) < 0) return true;
            // Easier pages need more room for the same picture, so its spaces stay big enough to color
            double need = _level switch { Difficulty.Simple => 0.24, Difficulty.Medium => 0.18, _ => 0.14 };
            if (Math.Min(length, width) < _r * need) return false;
            return k switch
            {
                ShapeKind.Butterfly or ShapeKind.Dragonfly => width >= length * 0.75,
                ShapeKind.Tulip => width >= length * 0.45,
                ShapeKind.FleurDeLis => width >= length * 0.6,
                ShapeKind.PeacockFeather => width >= length * 0.35,
                ShapeKind.Ladybug or ShapeKind.Owl or ShapeKind.LotusBloom => width >= length * 0.7,
                ShapeKind.Bee or ShapeKind.Snail or ShapeKind.Mushroom or ShapeKind.Pumpkin => width >= length * 0.75,
                ShapeKind.Fish => width >= length * 0.45,
                ShapeKind.PineTree => width >= length * 0.6,
                ShapeKind.Fern => width >= length * 0.4,
                ShapeKind.Key => width >= length * 0.35,
                ShapeKind.Hummingbird or ShapeKind.Hamsa or ShapeKind.Crown or ShapeKind.Anchor => width >= length * 0.7,
                _ => true,
            };
        }

        private Layer Make(string name, ShapeKind kind, int count, double radius, double length, double width, double stroke = 0.5) => new()
        {
            Name = name, Shape = kind, Count = count, Radius = Math.Round(radius, 2),
            Length = Math.Round(length, 2), Width = Math.Round(width, 2),
            Filled = false, StrokeColor = Ink, StrokeWidth = Math.Round(stroke * _lineScale, 2), FillColor = "#FFFFFF", Tweak = 0.5,
        };

        private static double Spacing(double radius, int count) => 2 * Math.PI * radius / count;

        /// <summary>Chains alternate face-on and edge-on links, so they need an even count to close up.</summary>
        private static int EvenCount(int count) => count % 2 == 0 ? count : count + 1;

        /// <summary>The smallest multiple of the symmetry whose spacing doesn't exceed target, kept above the minimum feature size.</summary>
        private int CountFor(double radius, double targetSpacing)
        {
            int k = 1;
            while (k < _maxRepeat && Spacing(radius, _n * k) > targetSpacing) k++;
            while (k > 1 && Spacing(radius, _n * k) < _minFeature) k--;
            return _n * k;
        }

        public List<Layer> Build()
        {
            _n = _level switch
            {
                Difficulty.Simple => new[] { 6, 8 }[_rng.Next(2)],
                Difficulty.Medium => new[] { 8, 10, 12 }[_rng.Next(3)],
                _ => new[] { 8, 10, 12, 12, 16 }[_rng.Next(5)],
            };
            var front = new List<Layer>();
            var rings = new List<Layer>();

            double center = _r * (0.13 + _rng.NextDouble() * 0.05);
            double borderStart = _r * (0.76 + _rng.NextDouble() * 0.07);

            // 3–5 bands between the center disc and the border, with varied heights
            int bandCount = _level switch { Difficulty.Simple => _rng.Next(2, 4), Difficulty.Medium => _rng.Next(3, 5), _ => _rng.Next(3, 6) };
            var heights = new double[bandCount];
            for (int i = 0; i < bandCount; i++) heights[i] = 0.7 + _rng.NextDouble();
            double sum = 0;
            foreach (double h in heights) sum += h;
            var edges = new List<double> { center };
            foreach (double h in heights) edges.Add(edges[^1] + (borderStart - center) * h / sum);

            AddCenter(front, center);

            Band? previous = null;
            for (int i = 0; i < bandCount; i++)
            {
                var band = ChooseBand(edges[i + 1] - edges[i], previous);
                AddBand(front, band, edges[i], edges[i + 1], offset: i % 2 == 1, index: i + 1);
                previous = band;
            }

            // A ring on every band edge. A few are drawn bold for structure, like the references' heavy circles.
            if (_weight(ShapeKind.Ring) > 0)
                for (int i = 0; i < edges.Count; i++)
                {
                    bool bold = i == 0 || i == edges.Count - 1 && Chance(0.5) || Chance(0.15);
                    var ring = Make(i == edges.Count - 1 ? "Border ring" : $"Ring {i}", ShapeKind.Ring, 1, edges[i], 0, 0, bold ? 0.9 : 0.5);
                    rings.Add(ring);
                }

            var border = AddBorder(borderStart);

            // Fill only the outermost shapes (white), so silhouette laser modes have a piece to cut out
            if (rings.Count > 0) rings[^1].Filled = true;

            var all = new List<Layer>();
            all.AddRange(front);
            all.AddRange(rings);
            all.AddRange(border);
            return all;
        }

        private void AddCenter(List<Layer> layers, double c)
        {
            double style = _rng.NextDouble();
            if (_level == Difficulty.Simple && style >= 0.4 && style < 0.7) style = 0.8;
            if (style < 0.4 && PickFrom(new[] { ShapeKind.Petal, ShapeKind.Eye, ShapeKind.Leaf, ShapeKind.Teardrop }) is ShapeKind petal)
            {
                // Petals from the center to the center ring
                int count = _n >= 12 && Chance(0.5) ? _n / 2 : _n;
                layers.Add(Make("Center petals", petal, count, c * 0.5, c * 0.96, Spacing(c * 0.55, count) * 1.05));
                if (_weight(ShapeKind.Ring) > 0) layers.Add(Make("Center dot", ShapeKind.Ring, 1, c * 0.14, 0, 0));
            }
            else if (style < 0.7 && _weight(ShapeKind.Circle) > 0)
            {
                // Spirograph rosette: circles that all pass through the middle
                int count = _n >= 12 ? _n / 2 + (Chance(0.5) ? _n / 2 : 0) : _n;
                layers.Add(Make("Center rosette", ShapeKind.Circle, count, c * 0.5, c * 0.98, c * 0.98, 0.45));
            }
            else if (PickFrom(new[] { ShapeKind.Flower, ShapeKind.Star, ShapeKind.Gem, ShapeKind.Daisy, ShapeKind.Butterfly, ShapeKind.Rose, ShapeKind.Quatrefoil, ShapeKind.Sunflower, ShapeKind.Snowflake, ShapeKind.MoroccanStar, ShapeKind.LotusBloom, ShapeKind.Sun }) is ShapeKind solo)
            {
                var l = Make("Center", solo, 1, 0, c * 1.8, c * 1.8);
                l.Tweak = solo == ShapeKind.Gem ? (_n % 8 == 0 ? 0.6 : 0.4) : 0.45;
                if (_level != Difficulty.Simple && Chance(0.6)) { l.InsetCount = 1; l.InsetGap = 0.25; }
                layers.Add(l);
            }
        }

        private Band ChooseBand(double height, Band? previous)
        {
            var options = new List<(Band B, double W)>();
            void Add(Band b, double w, ShapeKind[] pool)
            {
                if (Array.Exists(pool, k => _weight(k) > 0)) options.Add((b, b == previous ? w * 0.35 : w));
            }
            Add(Band.Petals, height >= _minFeature * 2 ? 0.5 : 0.1, TallShapes);
            Add(Band.Beads, 0.3, RoundShapes);
            Add(Band.Pattern, 0.2, PatternShapes);
            if (options.Count == 0) return Band.Beads;
            double total = 0;
            foreach (var o in options) total += o.W;
            double roll = _rng.NextDouble() * total;
            foreach (var o in options) { roll -= o.W; if (roll < 0) return o.B; }
            return options[^1].B;
        }

        private void AddBand(List<Layer> layers, Band band, double a, double b, bool offset, int index)
        {
            double h = b - a, m = (a + b) / 2;
            Layer? l = band switch
            {
                Band.Petals => PetalBand(h, m),
                Band.Beads => BeadBand(h, m),
                _ => PatternBand(a, h, m),
            };
            if (l == null) return;
            l.Name = $"Band {index} – {l.Shape}";
            l.OffsetHalfStep = offset;
            layers.Add(l);
        }

        private Layer? PetalBand(double h, double m)
        {
            int count = CountFor(m, h * 0.95);
            double s = Spacing(m, count);
            if (PickFrom(TallShapes, length: h * 0.97, width: s) is not ShapeKind kind) return null;
            var l = Make("", kind, count, m, h * 0.97, s * (kind == ShapeKind.Arch ? 0.9 : 1.03));
            Decorate(l);
            return l;
        }

        private Layer? BeadBand(double h, double m)
        {
            int count = CountFor(m, h * 1.25);
            double s = Spacing(m, count);
            double size = Math.Min(h * 0.8, s * 0.85);
            if (size < _minFeature) return null;
            if (PickFrom(RoundShapes, length: size, width: size) is not ShapeKind kind) return null;
            var l = Make("", kind, count, m, size, kind is ShapeKind.Heart or ShapeKind.Diamond ? size * 0.85 : size);
            l.Tweak = kind switch { ShapeKind.Gem => 0.6, ShapeKind.Star => 0.4, ShapeKind.Donut => 0.45, _ => 0.5 };
            Decorate(l);
            return l;
        }

        private Layer? PatternBand(double a, double h, double m) =>
            PickFrom(PatternShapes) is ShapeKind kind ? PatternBandOf(kind, a, h) : null;

        /// <summary>A ring pattern filling the band from radius a to a + h.</summary>
        private Layer PatternBandOf(ShapeKind kind, double a, double h)
        {
            double m = a + h / 2;
            var l = kind switch
            {
                // Scallops grow outward from the band's inner ring
                ShapeKind.Scallop => Make("", kind, CountFor(a, h * 1.3), a, h * 0.7, 0),
                ShapeKind.Lattice => Make("", kind, CountFor(m, h * 0.6), m, h, 0, 0.45),
                ShapeKind.Wave => Make("", kind, CountFor(m, h * 1.4), m, h * 0.6, 0),
                ShapeKind.Vine => Make("", kind, CountFor(m, h * 2.2), m, h * 0.95, 0),
                ShapeKind.Rope => Make("", kind, CountFor(m, h * 0.45), m, h * 0.85, 0),
                // A Greek key repeat is a 5 × 4 grid, so it wants to be a little wider than it is tall
                ShapeKind.GreekKey => Make("", kind, CountFor(m, h * 0.85 * 1.25), m, h * 0.85, 0),
                ShapeKind.Waves => Make("", kind, CountFor(m, h * 1.1), m, h * 0.9, 0),
                ShapeKind.Chain => Make("", kind, EvenCount(CountFor(m, h * 0.7)), m, h * 0.8, 0),
                ShapeKind.Stitch => Make("", kind, CountFor(m, h * 0.5), m, h * 0.6, 0),
                ShapeKind.FlowerOfLife => Make("", kind, CountFor(m, h * 0.45), m, h * 0.9, 0),
                _ => Make("", kind, CountFor(m, h * 0.8), m, h * 0.9, 0),
            };
            if (kind == ShapeKind.Lattice) l.Tweak = 0;
            return l;
        }

        /// <summary>Details scaled to the motif: only shapes big enough to color get echoes, inner shapes or hatching.</summary>
        public void Decorate(Layer l)
        {
            l.InsetCount = 0;
            l.Inner = InnerMotif.None;
            l.Hatch = HatchStyle.None;
            if (ShapeGenerator_IsRing(l.Shape)) return;
            if (Array.IndexOf(PictureShapes, l.Shape) >= 0) return;

            double size = Math.Min(l.Length, l.Width);
            bool round = Array.IndexOf(RoundShapes, l.Shape) >= 0;

            if (_level == Difficulty.Simple)
            {
                // At most one roomy detail, only on big shapes
                if (size > _r * 0.18 && Chance(0.5))
                {
                    if (Chance(0.5)) { l.InsetCount = 1; l.InsetGap = 0.25; }
                    else { l.Inner = InnerMotif.Circle; l.InnerSize = round ? 0.45 : 0.35; l.InnerOffset = round ? 0 : 0.15; }
                }
                return;
            }
            if (_level == Difficulty.Medium)
            {
                if (size > _r * 0.12)
                {
                    double roll = _rng.NextDouble();
                    if (roll < 0.4) { l.InsetCount = 1; l.InsetGap = round ? 0.28 : 0.2; }
                    else if (roll < 0.75) SetInner(l, round);
                    else if (!round && roll < 0.9) SetHatch(l);
                }
                return;
            }

            if (size > _r * 0.15)
            {
                if (Chance(0.7)) { l.InsetCount = 1; l.InsetGap = Math.Round(0.13 + _rng.NextDouble() * 0.05, 2); }
                if (Chance(0.6)) SetInner(l, round);
                else if (!round && Chance(0.5)) SetHatch(l);
            }
            else if (size > _r * 0.08)
            {
                double roll = _rng.NextDouble();
                if (roll < 0.4) { l.InsetCount = 1; l.InsetGap = round ? 0.3 : 0.18; }
                else if (roll < 0.7) SetInner(l, round);
                else if (!round && roll < 0.9) SetHatch(l);
            }
            else if (round && l.Shape == ShapeKind.Circle && size > _minFeature * 1.4 && Chance(0.5))
            {
                l.Inner = InnerMotif.Circle; // bullseye beads
                l.InnerSize = 0.5;
            }
        }

        private void SetInner(Layer l, bool round)
        {
            var options = round
                ? new[] { InnerMotif.Circle, InnerMotif.Bullseye, InnerMotif.Star }
                : new[] { InnerMotif.Circle, InnerMotif.Bullseye, InnerMotif.Teardrop, InnerMotif.TwoOvals, InnerMotif.Triangle, InnerMotif.Diamond, InnerMotif.Heart };
            l.Inner = options[_rng.Next(options.Length)];
            l.InnerSize = round ? 0.45 : Math.Round(0.28 + _rng.NextDouble() * 0.1, 2);
            l.InnerOffset = round ? 0 : Math.Round(0.05 + _rng.NextDouble() * 0.25, 2);
        }

        private void SetHatch(Layer l)
        {
            var options = new[] { HatchStyle.Chevron, HatchStyle.Across, HatchStyle.Fan };
            l.Hatch = options[_rng.Next(options.Length)];
            l.HatchCount = l.Hatch == HatchStyle.Fan ? 7 : 5 + _rng.Next(3);
            if (_level == Difficulty.Medium) l.HatchCount = Math.Max(3, l.HatchCount - 2);
        }

        private List<Layer> AddBorder(double start)
        {
            var result = new List<Layer>();
            double h = _r - start;
            double style = _rng.NextDouble();

            if (style < 0.65 && PickFrom(BorderShapes) is ShapeKind kind)
            {
                // Border petals: base on the border ring, tips at the edge, an ornament inside each one
                int count = CountFor(start + h / 2, h * 1.5);
                double s = Spacing(start + h / 2, count);
                var l = Make("Border petals", kind, count, start + h / 2, h, s * 1.08, 0.6);
                l.Filled = true;
                l.OffsetHalfStep = Chance(0.5);
                if (_level != Difficulty.Simple && Chance(0.6)) { l.InsetCount = 1; l.InsetGap = 0.16; }
                if (Chance(_level == Difficulty.Simple ? 0.4 : 0.7))
                {
                    var inner = new[] { InnerMotif.Circle, InnerMotif.Bullseye, InnerMotif.Teardrop, InnerMotif.Heart, InnerMotif.Diamond };
                    l.Inner = inner[_rng.Next(inner.Length)];
                    l.InnerSize = 0.3;
                    l.InnerOffset = 0.15;
                }
                result.Add(l);
            }
            else if (style < 0.85 && _weight(ShapeKind.Polygon) > 0)
            {
                // Polygon frame with arches tucked inside
                var frame = Make("Border polygon", ShapeKind.Polygon, _n, _r, 0, 0, 0.9);
                frame.Tweak = 0;
                frame.Filled = true;
                result.Add(frame);
                double apothem = _r * Math.Cos(Math.PI / _n);
                if (apothem - start > _minFeature && PickFrom(new[] { ShapeKind.Arch, ShapeKind.Dome, ShapeKind.Petal }) is ShapeKind arch)
                {
                    double hh = apothem - start;
                    var l = Make("Border arches", arch, _n, start + hh / 2, hh * 0.95, Spacing(start + hh / 2, _n) * 0.9);
                    Decorate(l);
                    result.Insert(0, l);
                }
            }
            else if (_weight(ShapeKind.Scallop) > 0)
            {
                var l = Make("Border scallops", ShapeKind.Scallop, CountFor(start, h * 1.4), start, h * 0.75, 0, 0.6);
                l.Filled = true;
                l.InsetCount = Chance(0.5) ? 1 : 0;
                l.InsetGap = 0.2;
                result.Add(l);
            }
            return result;
        }

        public bool Reroll(Layer l)
        {
            if (l.Shape == ShapeKind.Ring) return false;
            if (ShapeGenerator_IsRing(l.Shape))
            {
                if (PickFrom(PatternShapes, avoid: l.Shape) is not ShapeKind pattern || pattern == l.Shape) return false;
                // Work out which band the current pattern fills, then fit the new pattern into that same band.
                // Scallops grow outward from their radius, but the others are centered on theirs.
                double inner, height;
                switch (l.Shape)
                {
                    case ShapeKind.Scallop: inner = l.Radius; height = l.Length / 0.7; break;
                    case ShapeKind.Wave: height = l.Length / 0.6; inner = l.Radius - height / 2; break;
                    case ShapeKind.Zigzag: height = l.Length / 0.9; inner = l.Radius - height / 2; break;
                    case ShapeKind.Lattice: height = l.Length; inner = l.Radius - height / 2; break;
                    case ShapeKind.Vine: height = l.Length / 0.95; inner = l.Radius - height / 2; break;
                    case ShapeKind.Rope or ShapeKind.GreekKey: height = l.Length / 0.85; inner = l.Radius - height / 2; break;
                    case ShapeKind.Waves: height = l.Length / 0.9; inner = l.Radius - height / 2; break;
                    case ShapeKind.Chain: height = l.Length / 0.8; inner = l.Radius - height / 2; break;
                    case ShapeKind.Stitch: height = l.Length / 0.6; inner = l.Radius - height / 2; break;
                    case ShapeKind.FlowerOfLife: height = l.Length / 0.9; inner = l.Radius - height / 2; break;
                    default: return false; // other ring shapes (polygon, gear, sunburst…) aren't band patterns
                }
                _n = Math.Max(1, l.Count);
                var fresh = PatternBandOf(pattern, inner, height);
                l.Shape = pattern;
                l.Radius = fresh.Radius;
                l.Length = fresh.Length;
                l.Count = fresh.Count;
                l.Tweak = fresh.Tweak;
                return true;
            }

            // Keep the band (Radius, Length, Count); pick a new shape of the same family
            bool tall = l.Length >= l.Width * 1.15 && Array.IndexOf(RoundShapes, l.Shape) < 0;
            var pool = tall ? TallShapes : RoundShapes;
            if (PickFrom(pool, avoid: l.Shape, length: l.Length, width: l.Width) is not ShapeKind kind) return false;
            if (!tall)
            {
                double size = Math.Min(l.Length, l.Width);
                l.Length = size;
                l.Width = kind is ShapeKind.Heart or ShapeKind.Diamond ? Math.Round(size * 0.85, 2) : size;
                l.Tweak = kind switch { ShapeKind.Gem => 0.6, ShapeKind.Star => 0.4, ShapeKind.Donut => 0.45, _ => 0.5 };
            }
            l.Shape = kind;
            Decorate(l);
            return true;
        }
    }

    // Classic mandala petal shapes: pointed, layered forms
    private static readonly ShapeKind[] IntricatePetals = { ShapeKind.Lotus, ShapeKind.Dome, ShapeKind.Petal, ShapeKind.Teardrop, ShapeKind.Arch, ShapeKind.Fan };
    private static readonly double[] IntricatePetalBias = { 3, 3, 3, 1.5, 1, 1.2 };

    // Petal-like shapes that join the intricate petal layers only when you've made them favorites
    private static readonly ShapeKind[] IntricateFavoritePetals = { ShapeKind.Heart, ShapeKind.Leaf, ShapeKind.Eye };

    // Bead band shapes: a mix, with plain circles only now and then
    private static readonly ShapeKind[] BeadShapes =
        { ShapeKind.Diamond, ShapeKind.Teardrop, ShapeKind.Petal, ShapeKind.Gem, ShapeKind.Quatrefoil, ShapeKind.Eye, ShapeKind.Circle };
    private static readonly double[] BeadBias = { 1.5, 1.5, 1.2, 1.0, 1.0, 0.8, 0.6 };

    // Small shapes that can replace the round beads in intricate bead bands when they're favorites
    private static readonly ShapeKind[] IntricateBeadShapes =
        { ShapeKind.Heart, ShapeKind.Star, ShapeKind.Diamond, ShapeKind.Gem, ShapeKind.Flower, ShapeKind.Daisy, ShapeKind.Donut, ShapeKind.Quatrefoil };

    /// <summary>
    /// An intricate, traditional-style mandala: many stacked petal layers where each outer layer peeks out from behind
    /// the one in front, with nested echo outlines in every petal, dot accents along the tips, and a detailed center.
    /// Every layer is filled white, so front layers hide what's behind them. That occlusion gives the layered depth
    /// and keeps overlaps from tangling. Picture motifs aren't used; this sticks to traditional mandala shapes.
    /// </summary>
    /// <param name="difficulty">
    /// Simple: a few big layers, thick lines and no small decorations (kids, beginners, relaxing).
    /// Medium: fewer layers and lighter decoration. Intricate: the full detailed style.
    /// </param>
    public static MandalaDocument CreateIntricate(Random rng, double diameter = 200, Func<ShapeKind, double>? weight = null,
        Difficulty difficulty = Difficulty.Intricate)
    {
        weight ??= k => AppSettings.WeightOf(AppSettings.DefaultPreference(k));
        bool simple = difficulty == Difficulty.Simple, medium = difficulty == Difficulty.Medium;
        // Line weight: bolder for easier books, where wide outlines make coloring inside the lines easier
        double line = simple ? 0.8 : medium ? 0.6 : 0.45;
        double fine = simple ? 0.7 : medium ? 0.5 : 0.35;
        var doc = new MandalaDocument
        {
            DiameterMm = diameter, BackgroundColor = "#FFFFFF", NoFill = false, LaserMode = LaserMode.VisibleLines,
        };
        const string ink = "#000000", paper = "#FFFFFF";
        double R = diameter / 2 * 0.95;
        // Base symmetry. Every petal and band count is a multiple of it, so all the layers line up.
        int N = simple ? new[] { 6, 8 }[rng.Next(2)] : new[] { 8, 8, 12 }[rng.Next(3)];
        bool Chance(double p) => rng.NextDouble() < p;

        Layer Make(string name, ShapeKind kind, int count, double radius, double length, double width, double stroke = -1) => new()
        {
            Name = name, Shape = kind, Count = count, Radius = Math.Round(radius, 2), Length = Math.Round(length, 2), Width = Math.Round(width, 2),
            Filled = true, FillColor = paper, StrokeColor = ink,
            // -1 = the difficulty's main line; thinner requested lines are kept proportionally thinner
            StrokeWidth = stroke < 0 ? line : Math.Round(stroke / 0.45 * line, 2), Tweak = 0.5,
        };

        // Petal choice: respect "Never" in shape preferences, but bias toward the classic pointed forms
        ShapeKind PickPetal(ShapeKind? avoid)
        {
            double W(ShapeKind k)
            {
                int i = Array.IndexOf(IntricatePetals, k);
                return k == avoid || weight(k) <= 0 ? 0 : IntricatePetalBias[i] * weight(k);
            }
            return Pick(rng, IntricatePetals, W) ?? Pick(rng, IntricatePetals, k => weight(k) > 0 ? 1 : 0) ?? ShapeKind.Petal;
        }

        // Echo decorations: spread across all six so dots don't dominate
        var accentChoices = new[] { EchoAccent.Dots, EchoAccent.Dashes, EchoAccent.Diamonds, EchoAccent.Leaves, EchoAccent.Scallops, EchoAccent.Zigzag };
        EchoAccent PickAccent() => accentChoices[rng.Next(accentChoices.Length)];
        EchoStyle AsStyle(EchoAccent a) => a switch
        {
            EchoAccent.Dots => EchoStyle.Dots,
            EchoAccent.Dashes => EchoStyle.Dashes,
            EchoAccent.Diamonds => EchoStyle.Diamonds,
            EchoAccent.Leaves => EchoStyle.Leaves,
            EchoAccent.Scallops => EchoStyle.Scallops,
            _ => EchoStyle.Zigzag,
        };

        // Outer edge pickers: pointed tips (Lotus, Dome, Petal) or round ends (Teardrop, Arch)
        ShapeKind PickPointed() =>
            Pick(rng, new[] { ShapeKind.Lotus, ShapeKind.Dome, ShapeKind.Petal }, k => weight(k) > 0 ? (k == ShapeKind.Lotus ? 1.5 : 1) * weight(k) : 0)
            ?? PickPetal(null);
        ShapeKind PickRounded() =>
            Pick(rng, new[] { ShapeKind.Teardrop, ShapeKind.Arch, ShapeKind.Fan }, k => weight(k) > 0 ? weight(k) : 0)
            ?? PickPetal(null);

        // Favorites from outside the classic petals (hearts, stars…) are used as a feature, not a theme:
        // about half the designs pick one favorite and use it in one place only, either a single petal layer,
        // a single bead band, or the accents inside one layer. The rest stay purely classic.
        bool IsFavorite(ShapeKind k) => weight(k) > 1;
        ShapeKind? featured = null;
        string feature = "";
        var featureCandidates = new List<ShapeKind>();
        foreach (var k in IntricateFavoritePetals) if (IsFavorite(k) && !featureCandidates.Contains(k)) featureCandidates.Add(k);
        foreach (var k in IntricateBeadShapes) if (IsFavorite(k) && !featureCandidates.Contains(k)) featureCandidates.Add(k);
        if (featureCandidates.Count > 0 && Chance(0.5))
        {
            var pick = featureCandidates[rng.Next(featureCandidates.Count)];
            var uses = new List<string>();
            if (Array.IndexOf(IntricateFavoritePetals, pick) >= 0) uses.Add("petal");
            if (Array.IndexOf(IntricateBeadShapes, pick) >= 0) uses.Add("bead");
            if (!simple && pick is ShapeKind.Heart or ShapeKind.Star) uses.Add("accent");
            if (uses.Count > 0)
            {
                featured = pick;
                feature = uses[rng.Next(uses.Count)];
            }
        }

        var dots = new List<Layer>();   // drawn on top of everything
        var center = new List<Layer>();
        var petals = new List<Layer>(); // inner layers first (in front), outer layers behind

        // ---- Center: a small flower on a disc, ringed with dots ----
        double c = R * (simple ? 0.24 + rng.NextDouble() * 0.04 : 0.2 + rng.NextDouble() * 0.05);
        var flowerKind = Chance(0.5) ? ShapeKind.Daisy : ShapeKind.Flower;
        if (weight(flowerKind) <= 0) flowerKind = flowerKind == ShapeKind.Daisy ? ShapeKind.Flower : ShapeKind.Daisy;
        if (simple) flowerKind = ShapeKind.Flower; // a daisy's many thin petals are fiddly for an easy book
        if (weight(ShapeKind.SwirlBand) > 0 && Chance(simple ? 0.2 : 0.35))
        {
            // Pinwheel center: curved blades sweeping out from a small hub (the hub covers where the blades meet)
            center.Add(Make("Center hub", ShapeKind.Circle, 1, 0, c * 0.36, c * 0.36));
            var swirl = Make("Center swirl", ShapeKind.SwirlBand, simple ? Math.Min(N, 6) : N, c * 0.55, c * 0.9, 0);
            swirl.Tweak = Math.Round(0.35 + rng.NextDouble() * 0.35, 2);
            center.Add(swirl);
        }
        else
        {
            var flower = Make("Center flower", flowerKind, 1, 0, c * 1.3, c * 1.3);
            flower.Tweak = flowerKind == ShapeKind.Daisy ? 0.2 : 0.5;
            center.Add(flower);
        }
        var disc = Make("Center disc", ShapeKind.Ring, 1, c, 0, 0, 0.6);
        disc.InsetCount = 1; disc.InsetGap = 0.12 / 8 * c; // a second ring just inside the edge
        center.Add(disc);
        var centerAccent = new[] { ShapeKind.Circle, ShapeKind.Teardrop, ShapeKind.Diamond }[rng.Next(3)];
        double ca = centerAccent == ShapeKind.Circle ? R * 0.028 : R * 0.05;
        var centerRing = Make("Center accents", centerAccent, N * 2, c + ca * 0.75 + R * 0.01, ca, centerAccent == ShapeKind.Circle ? ca : ca * 0.55, 0.35);
        if (!simple) dots.Add(centerRing);

        // ---- The stack: petal layers, with one or two small decorative bands between them ----
        // Petal layers grow larger toward the outside. The bands (scallops or beads, at 2–4× the base count)
        // break up the rhythm, the way the round-scallop and dotted rings do in hand-drawn mandalas.
        int layerCount = simple ? rng.Next(2, 4) : medium ? rng.Next(3, 5) : rng.Next(4, 7);
        var slots = new List<bool>();                // false = petal layer, true = band
        for (int i = 0; i < layerCount; i++) slots.Add(false);
        if (!simple) slots.Insert(Chance(0.5) ? 0 : 1, true);
        if (!simple && !medium && layerCount >= 5 && Chance(0.45)) slots.Insert(slots.Count - 2, true);
        double bandH = R * (medium ? 0.08 + rng.NextDouble() * 0.02 : 0.06 + rng.NextDouble() * 0.02);
        int bandCount = slots.FindAll(s => s).Count;

        double tip = c;
        double outerLimit = R * 0.98;
        var share = new double[layerCount];
        double shareSum = 0;
        for (int i = 0; i < layerCount; i++) { share[i] = (1 + 0.35 * i) * (0.9 + rng.NextDouble() * 0.2); shareSum += share[i]; }
        // The outer edge: pointed petals, rounded petals, or a scalloped rim behind the last petal layer
        double edgeRoll = rng.NextDouble();
        string edge = edgeRoll < 0.4 ? "pointed" : edgeRoll < 0.75 ? "rounded" : "scalloped";
        if (edge == "scalloped" && weight(ShapeKind.Scallop) <= 0) edge = "rounded";
        double rimH = edge == "scalloped" ? R * (0.13 + rng.NextDouble() * 0.04) : 0;
        double petalOuter = outerLimit - rimH * 0.75;   // the last petals overlap the rim's base a little
        double petalRoom = petalOuter - c - bandCount * bandH;
        int featureLayer = rng.Next(1, Math.Max(2, layerCount - 1)); // a middle layer, never the bold outer edge
        ShapeKind? previous = null;
        int bandNumber = 0;
        int layer = -1;
        foreach (bool isBand in slots)
        {
            if (isBand)
            {
                AddBand(++bandNumber);
                continue;
            }
            layer++;
            bool last = layer == layerCount - 1;
            double grow = petalRoom * share[layer] / shareSum;
            double newTip = last ? petalOuter : tip + grow;
            grow = newTip - tip;
            // Each petal reaches back under the layer in front of it, so only its outer part shows
            double len = grow * (1.9 + rng.NextDouble() * 0.4);
            double mid = newTip - len / 2;
            // Few, big petals near the center. Further out, layers often double, but only if the petals stay wide enough to color.
            bool canDouble = 2 * Math.PI * mid / (N * 2) > R * 0.08;
            int count = !simple && layer > 0 && canDouble && Chance(medium ? 0.3 : last ? 0.55 : 0.45) ? N * 2 : N;
            double spacing = 2 * Math.PI * mid / count;
            // The outer layer sets the edge style: pointed, rounded, or a scalloped rim behind it
            var kind = !last || edge == "scalloped" ? PickPetal(previous) : edge == "pointed" ? PickPointed() : PickRounded();
            if (feature == "petal" && featured is ShapeKind fp && layer == featureLayer) { kind = fp; feature = "done"; }
            double width = Math.Min(spacing * (last && edge != "scalloped" ? 1.05 : 0.95 + rng.NextDouble() * 0.2), len * 0.95);
            // A shell fan only looks right when it's roughly as wide as it is long. Stretched long, it turns into
            // a thin feathery cluster, so use a different petal there.
            if (kind == ShapeKind.Fan && width < len * 0.8)
                kind = last && edge == "rounded" && weight(ShapeKind.Teardrop) > 0 ? ShapeKind.Teardrop : PickPetal(ShapeKind.Fan);

            var l = Make($"Petals {layer + 1} – {kind}", kind, count, mid, len, width);
            l.OffsetHalfStep = layer % 2 == 1;   // alternate, so each layer's tips fall between the ones in front
            l.Tweak = kind switch { ShapeKind.Lotus => 0.8, ShapeKind.Dome => 0.75, _ => 0.5 };

            // Nested echo outlines (the signature look), spaced so each band is wide enough to color,
            // plus a small accent inside the bigger petals
            double size = Math.Min(len, width);
            l.InsetCount = simple ? (size > R * 0.22 ? 1 : 0)
                         : medium ? (size > R * 0.12 ? 1 : 0)
                         : size > R * 0.16 ? 2 : size > R * 0.08 ? 1 : 0;
            l.InsetGap = Math.Round(simple ? 0.28 + rng.NextDouble() * 0.05 : 0.18 + rng.NextDouble() * 0.05, 2);
            // Dotted echoes for the delicate hand-drawn look: often a solid echo with a dot row inside it
            if (l.InsetCount >= 2)
            {
                // A solid echo with a decorated one inside it (two decorated rows crowd each other)
                l.EchoStyle = Chance(0.65) ? EchoStyle.Mixed : EchoStyle.Lines;
                l.EchoAccent = PickAccent();
            }
            else if (l.InsetCount == 1) l.EchoStyle = simple ? EchoStyle.Lines : Chance(medium ? 0.3 : 0.5) ? AsStyle(PickAccent()) : EchoStyle.Lines;
            // Fern-like fringe on some of the bigger petals
            if (!simple && !medium && l.InsetCount >= 1 && size > R * 0.12 && kind != ShapeKind.Fan && Chance(0.22))
            {
                l.EchoStyle = EchoStyle.Fringe;
                l.InsetGap = Math.Max(l.InsetGap, 0.3); // a wider band, so the cells between ticks are big enough to color
                l.InsetCount = 1;
            }
            // A heart's dip pinches a solid echo against the outline; a dotted echo simply leaves dots out there
            if (kind == ShapeKind.Heart && l.InsetCount > 0)
            {
                // Separate pieces (dots, diamonds, leaves) simply leave a gap at the dip
                l.InsetCount = simple ? 0 : 1;
                l.EchoStyle = new[] { EchoStyle.Dots, EchoStyle.Diamonds, EchoStyle.Leaves }[rng.Next(3)];
            }
            // The accent sits in the middle of the innermost echo, sized to fit inside it, so it never crosses an echo line or dot row
            double room = l.InsetCount > 0 ? 1 - (l.InsetCount + (l.EchoStyle == EchoStyle.Lines ? 0 : 0.6)) * l.InsetGap : 0.8;
            if (size > R * (simple ? 0.2 : 0.1) && room > 0.3 && Chance(simple ? 0.35 : medium ? 0.35 : 0.55))
            {
                var accents = new[] { InnerMotif.Teardrop, InnerMotif.Teardrop, InnerMotif.Diamond, InnerMotif.Diamond, InnerMotif.Circle, InnerMotif.Bullseye };
                l.Inner = accents[rng.Next(accents.Length)];
                if (feature == "accent" && featured is ShapeKind fa)
                {
                    l.Inner = fa == ShapeKind.Star ? InnerMotif.Star : InnerMotif.Heart;
                    feature = "done";
                }
                if (simple) l.Inner = new[] { InnerMotif.Teardrop, InnerMotif.Circle, InnerMotif.Diamond }[rng.Next(3)]; // one big, easy shape
                l.InnerSize = Math.Round(Math.Min(simple ? 0.3 : 0.22, room * (simple ? 0.5 : 0.4)), 2);
                l.InnerOffset = 0;
            }
            // A fan's lobe lines are its detail. Echoes would stack copies of every lobe into a tangle, and an accent
            // would sit on the lobe lines, so fans stay plain.
            if (kind == ShapeKind.Fan)
            {
                l.InsetCount = 0;
                l.Inner = InnerMotif.None;
                l.EchoStyle = EchoStyle.Lines;
            }
            petals.Add(l);

            // Dots just beyond the tips of some layers
            if (!simple && layer < layerCount - 1 && Chance(medium ? 0.3 : 0.55))
            {
                double dot = Math.Min(R * 0.03, spacing * 0.22);
                // Tip accents: small diamonds or teardrops as often as dots
                var tipKind = new[] { ShapeKind.Circle, ShapeKind.Diamond, ShapeKind.Teardrop }[rng.Next(3)];
                double accentLen = tipKind == ShapeKind.Circle ? dot : dot * 2.2;
                double accentWid = tipKind == ShapeKind.Circle ? dot : dot * 1.2;
                var d = Make($"Tip accents {layer + 1}", tipKind, count, newTip + accentLen * 0.8, accentLen, accentWid, 0.35);
                d.OffsetHalfStep = l.OffsetHalfStep;
                dots.Add(d);
            }

            tip = newTip;
            previous = kind;
        }

        // A decorative band sitting just outside the current tips, in front of the next petal layer
        void AddBand(int number)
        {
            double mid = tip + bandH / 2;
            int k = Math.Clamp((int)Math.Round(2 * Math.PI * mid / bandH / N), 2, 8);
            int count = N * k;
            double spacing = 2 * Math.PI * mid / count;
            bool featureHere = feature == "bead";
            if (!featureHere && weight(ShapeKind.SwirlBand) > 0 && Chance(0.25))
            {
                // A narrow pinwheel band: curved blades that give the design a sense of motion
                var sw = Make($"Band {number} – swirl", ShapeKind.SwirlBand, count, mid, bandH, 0, 0.4);
                sw.Tweak = 0.3;
                petals.Add(sw);
                tip += bandH;
                return;
            }
            bool scallops = !featureHere && weight(ShapeKind.Scallop) > 0 && (weight(ShapeKind.Circle) <= 0 || Chance(0.5));
            if (scallops)
            {
                // Round scallops growing out from the tips. As a filled shape it also hides the next layer's petal bases.
                var s = Make($"Band {number} – scallops", ShapeKind.Scallop, count, tip, bandH * 0.9, 0, 0.4);
                s.Tweak = 0.35;
                petals.Add(s);
            }
            else
            {
                // A string of beads on a circle. The circle (a filled disc) hides the next layer's petal bases.
                // A featured favorite (hearts, stars…) takes the place of the beads in one band; otherwise the beads
                // are a mix of shapes, with plain circles only now and then.
                ShapeKind beadKind;
                if (featureHere && featured is ShapeKind fb) beadKind = fb;
                else beadKind = Pick(rng, BeadShapes, k => weight(k) > 0 ? BeadBias[Array.IndexOf(BeadShapes, k)] : 0) ?? ShapeKind.Circle;
                if (featureHere) feature = "done";

                // Pointed beads stand tall across the band; round ones stay compact
                bool tall = beadKind is ShapeKind.Diamond or ShapeKind.Teardrop or ShapeKind.Petal or ShapeKind.Eye;
                double beadLen = tall ? Math.Min(bandH * 0.95, spacing * 1.6) : Math.Min(bandH * 0.85, spacing * 0.85);
                double beadWid = tall ? Math.Min(spacing * 0.72, beadLen * 0.75) : beadLen;
                if (!tall && beadKind != ShapeKind.Circle) beadLen = beadWid = Math.Min(bandH * 0.95, spacing * 0.95);
                var beads = Make($"Band {number} – {(beadKind == ShapeKind.Circle ? "beads" : beadKind.ToString())}", beadKind, count, mid, beadLen, beadWid, 0.4);
                beads.Tweak = beadKind switch
                {
                    ShapeKind.Star => 0.4, ShapeKind.Gem => 0.6, ShapeKind.Daisy => 0.0, ShapeKind.Eye => 0.35,
                    ShapeKind.Quatrefoil => 0.5, _ => 0.5,
                };
                petals.Add(beads);
                petals.Add(Make($"Band {number} – string", ShapeKind.Ring, 1, mid, 0, 0, 0.4));
            }
            tip += bandH;
        }

        // Scalloped rim: round scallops behind the last petal layer form the outer edge
        if (edge == "scalloped")
        {
            double depth = outerLimit - tip;
            int k = Math.Clamp((int)Math.Round(2 * Math.PI * tip / (depth * 2.2) / N), 1, 6); // wide, generous scallops
            var rim = Make("Edge – scallops", ShapeKind.Scallop, N * k, tip, depth * 0.95, 0, 0.5);
            rim.Tweak = 0.4;
            rim.OffsetHalfStep = (layerCount % 2) == 0; // scallops centered between the last layer's tips
            rim.InsetCount = simple ? 0 : 1;
            rim.InsetGap = Math.Round(0.35 * depth / Math.Max(depth * 0.95, 8), 3); // ring echoes step in by InsetGap × max(Length, 8) mm
            rim.EchoStyle = !simple && !medium && Chance(0.6) ? AsStyle(PickAccent()) : EchoStyle.Lines;
            petals.Add(rim);
        }

        foreach (var l in dots) doc.Layers.Add(l);
        foreach (var l in center) doc.Layers.Add(l);
        foreach (var l in petals) doc.Layers.Add(l);
        ClearDotRings(doc);
        return doc;
    }

    /// <summary>
    /// Dot rings sit over the layers behind them, so a dot can land on an outline or echo.
    /// Each ring is checked with one dot (the others are symmetric copies). If it touches a line, it's nudged
    /// outward or inward, and if no position is clear, the ring is dropped.
    /// </summary>
    private static void ClearDotRings(MandalaDocument doc)
    {
        foreach (var ring in doc.Layers.Where(l => l.Name.Contains("accents", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            double rd = Math.Max(ring.Length, ring.Width) / 2;
            // Every line near the ring's radius, from all the other layers
            var lines = new List<List<Geom.Point>>();
            foreach (var other in doc.Layers)
            {
                if (other == ring || !other.Visible) continue;
                foreach (string data in new[] { Shapes.ShapeGenerator.BuildPathData(other), Shapes.ShapeGenerator.BuildDetailData(other) })
                    if (!string.IsNullOrWhiteSpace(data))
                        lines.AddRange(Geom.PathData.ToPolylines(data, 0.05)
                            .Where(pl => pl.Exists(p => Math.Abs(Math.Sqrt(p.X * p.X + p.Y * p.Y) - ring.Radius) < rd * 6)));
            }

            double angle = (ring.RingRotation - 90) * Math.PI / 180 + (ring.OffsetHalfStep ? Math.PI / ring.Count : 0);
            bool Clear(double radius) =>
                Shapes.ShapeGenerator.MinDistance(new Geom.Point(radius * Math.Cos(angle), radius * Math.Sin(angle)), lines) >= rd * 1.35;

            double original = ring.Radius;
            double? fit = null;
            foreach (double shift in new[] { 0, 0.6, -0.6, 1.2, -1.2, 1.8 })
                if (Clear(original + shift * rd)) { fit = original + shift * rd; break; }
            if (fit is double r) ring.Radius = Math.Round(r, 2);
            else doc.Layers.Remove(ring);
        }
    }

    private static bool ShapeGenerator_IsRing(ShapeKind k) => Shapes.ShapeGenerator.IsRingShape(k);

    /// <summary>True if the weights allow at least one shape to be generated.</summary>
    public static bool AnyAllowed(Func<ShapeKind, double> weight) =>
        Array.Exists(Enum.GetValues<ShapeKind>(), k => weight(k) > 0);

    /// <summary>Weighted random choice, or null if every shape in the pool has weight 0.</summary>
    private static ShapeKind? Pick(Random rng, IEnumerable<ShapeKind> pool, Func<ShapeKind, double> weight)
    {
        var options = new List<(ShapeKind Kind, double W)>();
        double total = 0;
        foreach (var k in pool)
        {
            double w = weight(k);
            if (w <= 0) continue;
            options.Add((k, w));
            total += w;
        }
        if (total <= 0) return null;
        double roll = rng.NextDouble() * total;
        foreach (var (kind, w) in options)
        {
            roll -= w;
            if (roll < 0) return kind;
        }
        return options[^1].Kind;
    }

    /// <param name="weight">How likely each shape is (0 = never). Defaults to the built-in preferences.</param>
    public static MandalaDocument Create(Random rng, double diameter = 200, Func<ShapeKind, double>? weight = null)
    {
        weight ??= k => AppSettings.WeightOf(AppSettings.DefaultPreference(k));
        bool motifsAllowed = Array.Exists(Motifs, k => weight(k) > 0);
        bool ringsAllowed = Array.Exists(RingShapes, k => weight(k) > 0);
        double ringChance = !ringsAllowed ? 0 : !motifsAllowed ? 1 : 0.3;

        var pal = Palettes[rng.Next(Palettes.Length)];
        var doc = new MandalaDocument { DiameterMm = diameter, BackgroundColor = pal.Background };
        var layers = new List<Layer>();

        int baseSym = new[] { 6, 8, 10, 12 }[rng.Next(4)];
        double maxR = diameter / 2 * 0.92;
        string stroke = pal.Colors[0];
        int colorIndex = rng.Next(1, pal.Colors.Length);
        string NextFill() { var c = pal.Colors[colorIndex]; colorIndex = colorIndex % (pal.Colors.Length - 1) + 1; return c; }

        // Center piece: a single shape if one is allowed, otherwise a small rosette of any allowed motif
        if (Pick(rng, SoloCenters, weight) is ShapeKind solo)
            layers.Add(new Layer { Name = "Center", Shape = solo, Count = 1, Radius = 0, Length = 14, Width = 14, Tweak = 0.45, StrokeColor = stroke, FillColor = NextFill() });
        else if (Pick(rng, Motifs, weight) is ShapeKind rosette)
            layers.Add(Styled(new Layer { Name = "Center", Shape = rosette, Count = baseSym, Radius = 6, Length = 12, Width = 5, StrokeColor = stroke, FillColor = NextFill() }));

        double r = 10;
        int index = 1;
        while (r < maxR - 6)
        {
            double band = Math.Min(maxR - r, 8 + rng.NextDouble() * 16);
            double mid = r + band / 2;
            int mult = Math.Clamp((int)Math.Round(mid / 22), 1, 6);
            int count = Math.Min(baseSym * mult, 72);
            var layer = new Layer
            {
                Name = $"Ring {index}",
                Count = count,
                Radius = Math.Round(mid, 1),
                OffsetHalfStep = rng.Next(2) == 0,
                StrokeColor = stroke,
                StrokeWidth = 0.5,
                FillColor = NextFill(),
                Tweak = Math.Round(0.3 + rng.NextDouble() * 0.4, 2),
            };

            if (rng.NextDouble() < ringChance)
            {
                layer.Shape = Pick(rng, RingShapes, weight)!.Value;
                layer.Length = Math.Round(band * 0.6, 1);
                layer.Width = layer.Shape == ShapeKind.Ring ? Math.Round(band * 0.25, 1) : 0;
                layer.Radius = Math.Round(r + band * 0.3, 1);
            }
            else if (motifsAllowed)
            {
                layer.Shape = Pick(rng, Motifs, weight)!.Value;
                double spacing = 2 * Math.PI * mid / count;
                layer.Length = Math.Round(band, 1);
                layer.Width = Math.Round(Math.Min(spacing * 0.95, band * 0.85), 1);
                if (layer.Shape == ShapeKind.Circle)
                    layer.Length = layer.Width = Math.Round(Math.Min(band, spacing * 0.9), 1);
            }
            else break; // nothing is allowed at all

            layers.Add(Styled(layer));
            r += band * (0.75 + rng.NextDouble() * 0.35);
            index++;
        }

        if (weight(ShapeKind.Ring) > 0)
            layers.Add(new Layer { Name = "Outline", Shape = ShapeKind.Ring, Count = 1, Radius = Math.Round(maxR, 1), Width = 0, Filled = false, StrokeWidth = 0.8, StrokeColor = stroke });

        foreach (var l in layers) doc.Layers.Add(l);
        return doc;
    }

    /// <summary>Line-art shapes look wrong filled, so draw them as strokes only.</summary>
    private static Layer Styled(Layer layer)
    {
        if (Array.IndexOf(LineArt, layer.Shape) >= 0)
        {
            layer.Filled = false;
            layer.StrokeWidth = Math.Max(layer.StrokeWidth, 0.7);
        }
        return layer;
    }

    /// <summary>Applies a random palette to existing layers and keeps all of their geometry.</summary>
    public static void Recolor(MandalaDocument doc, Random rng)
    {
        var pal = Palettes[rng.Next(Palettes.Length)];
        doc.BackgroundColor = pal.Background;
        int i = rng.Next(1, pal.Colors.Length);
        foreach (var layer in doc.Layers)
        {
            layer.StrokeColor = pal.Colors[0];
            layer.FillColor = pal.Colors[i];
            i = i % (pal.Colors.Length - 1) + 1;
        }
    }
}
