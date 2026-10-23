using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MandalaMaker.Model;

/// <summary>
/// One ring of a mandala. All distances are in millimeters, measured from the center.
/// </summary>
public sealed class Layer : ObservableObject
{
    private static readonly Dictionary<ShapeKind, string> Help = new()
    {
        [ShapeKind.Custom] = "Your own outline. Drag the points in the editor below and every copy updates. Length and Width still scale it.",
        [ShapeKind.Petal] = "Pointed lens shape. Length is radial, Width is across. Tweak moves the widest point (0 = near center, 1 = near tip).",
        [ShapeKind.Teardrop] = "Point faces the center and the round end faces outward. Tweak controls how sharp the point is.",
        [ShapeKind.Leaf] = "Petal with a center vein and side veins. Tweak moves the widest point.",
        [ShapeKind.Eye] = "Almond shape with a round hole cut through the middle. Tweak sets the hole size.",
        [ShapeKind.Dome] = "Onion dome: flat base toward the center, bulb, then a sharp tip. Tweak sets the base width.",
        [ShapeKind.Lotus] = "Lotus petal: wide base, rounded shoulders and an ogee point. Tweak sets how wide the base is.",
        [ShapeKind.Paisley] = "Paisley drop with a curled tail. Tweak below 0.5 curls one way and above curls the other.",
        [ShapeKind.Flower] = "Small flower of round petals. Tweak sets the petal count (5 to 9). Length is the flower's diameter.",
        [ShapeKind.Daisy] = "Daisy: a center disc ringed by slim round-tipped petals. Length is the flower's diameter, and Tweak sets the petal count (10 to 24).",
        [ShapeKind.Tulip] = "Tulip seen from the side: the bloom points outward, the stem toward the center. Tweak sets the leaf size (0 = no leaves).",
        [ShapeKind.Rose] = "Rose seen from the front: a spiral heart, layered cupped petals and a scalloped outline. Tweak sets the outer petal count (5 to 7).",
        [ShapeKind.Quatrefoil] = "Gothic lobed shape made of overlapping circles. Tweak sets the lobes: 0 = trefoil (3), 0.5 = quatrefoil (4), 1 = cinquefoil (5).",
        [ShapeKind.FleurDeLis] = "Fleur-de-lis pointing outward: a tall center petal, two curled side petals, a band and a small three-part base. Width about 0.8 × Length.",
        [ShapeKind.PeacockFeather] = "Peacock feather pointing outward, with a quill, side barbs and a layered eye near the tip. Width about 0.5 × Length.",
        [ShapeKind.LotusBloom] = "Lotus flower: a tall center petal flanked by two pairs of petals, on a small base. It points outward. Width about equal to Length.",
        [ShapeKind.Sunflower] = "Sunflower: two rows of pointed petals around a big cross-hatched seed center. Tweak sets the petal count (14 to 24).",
        [ShapeKind.Snowflake] = "Six-armed snowflake with branches and a hexagon center. Tweak sets the branch length.",
        [ShapeKind.MoroccanStar] = "Eight-point star made of two overlapping squares, with a circle in the middle. Tweak sets the circle size.",
        [ShapeKind.Ladybug] = "Ladybug seen from above, head pointing outward, with spots, legs and antennae. Tweak sets the spot size.",
        [ShapeKind.Owl] = "Owl facing you, head pointing outward: ear tufts, big eyes, beak, wings, belly feathers and feet. Width about 0.85 × Length.",
        [ShapeKind.Bee] = "Bee seen from above, head pointing outward: striped body, two pairs of wings, antennae and a stinger. Width about equal to Length.",
        [ShapeKind.Snail] = "Snail standing on its foot (toward the center), with a spiral shell and eye stalks. Width about equal to Length.",
        [ShapeKind.Fish] = "Fish seen from the side, nose pointing outward, with fins, a gill, an eye and rows of scales. Width about 0.6 × Length.",
        [ShapeKind.Mushroom] = "Toadstool with its cap pointing outward, a stem and spots on the cap. Tweak sets the spot size (0 = no spots).",
        [ShapeKind.PineTree] = "Pine tree with three scalloped tiers and a trunk, top pointing outward. Tweak adds ornaments (0 = none). Width about 0.75 × Length.",
        [ShapeKind.Pumpkin] = "Pumpkin with ribbed lobes, a curled stem and a leaf, stem pointing outward. Width about equal to Length.",
        [ShapeKind.Sun] = "Smiling sun: a face in the middle and rays that alternate long and short. Tweak sets the ray count (12 to 20).",
        [ShapeKind.Hummingbird] = "Hummingbird in flight, beak pointing outward: a raised wing with feather lines, a forked tail and a long beak. Width about equal to Length.",
        [ShapeKind.Fern] = "Fern frond pointing outward: a stem with alternating leaflets that get smaller toward the tip. Tweak sets the leaflet count (6 to 10 pairs). Width about 0.6 × Length.",
        [ShapeKind.Hamsa] = "Hamsa hand, fingers pointing outward, with an eye in the palm. Width about 0.85 × Length.",
        [ShapeKind.Crown] = "Crown with five points topped by jewels, and a jeweled band. Width about equal to Length.",
        [ShapeKind.Key] = "Old-fashioned key: a ring-shaped bow pointing outward, a collar, a shaft and teeth. Width about 0.45 × Length.",
        [ShapeKind.Anchor] = "Anchor with its ring pointing outward: a crossbar, a shank and curved arms with flukes. Width about 0.8 × Length.",
        [ShapeKind.MoonStars] = "Crescent moon cradling stars. Tweak sets the number of stars (1 to 3).",
        [ShapeKind.Raindrop] = "Raindrop with its point outward, an inner drop and a shine. Width about 0.7 × Length.",
        [ShapeKind.Fan] = "Shell fan: a half-disc of rounded lobes with lines between them and a small hub at the base, opening outward. Tweak sets the lobes (5 to 9).",
        [ShapeKind.Butterfly] = "Butterfly seen from above, head pointing outward. Width about 1.1 × Length looks natural. Tweak sets the wing spots (0 = none).",
        [ShapeKind.Dragonfly] = "Dragonfly seen from above, head pointing outward, with veined wings and a segmented tail. Width about equal to Length looks natural.",
        [ShapeKind.Circle] = "Ellipse. Length is radial, Width is across. Set them equal for a circle. Tweak is not used.",
        [ShapeKind.Donut] = "Ellipse with a matching hole. Tweak sets the hole size.",
        [ShapeKind.Crescent] = "Crescent moon bulging outward. Length is the moon's diameter, and Tweak sets how thick it is.",
        [ShapeKind.Diamond] = "Four-sided diamond. Tweak moves the side corners along the length.",
        [ShapeKind.Gem] = "Regular polygon motif. Tweak sets the number of sides (3 to 8).",
        [ShapeKind.Triangle] = "Points outward. Tweak bows the sides (0.5 = straight, higher = puffy, lower = curved in).",
        [ShapeKind.Star] = "Five-point star. Tweak sets the inner radius (low = spiky).",
        [ShapeKind.Heart] = "Heart with its point toward the center. Tweak is not used.",
        [ShapeKind.Cross] = "Plus/cross shape. Tweak sets how thick the arms are.",
        [ShapeKind.Arch] = "Arched window or tombstone that rounds outward. Width sets the arch radius.",
        [ShapeKind.Spoke] = "Radial line. Tweak adds a dot at the outer end, sized by Width. Use stroke only.",
        [ShapeKind.Swirl] = "Spiral curl drawn as a line. Tweak sets the number of turns (1 to 4). Use stroke only.",
        [ShapeKind.Ring] = "Circle at Radius. Width greater than 0 makes a band of that thickness. Count is not used.",
        [ShapeKind.Scallop] = "Count bumps around the ring, each Length deep. Tweak below 0.5 is bulbous and above is pointy.",
        [ShapeKind.Zigzag] = "Zigzag band of Count points, Length from trough to peak.",
        [ShapeKind.Wave] = "Smooth sine-wave ring with Count waves of amplitude Length.",
        [ShapeKind.Sunburst] = "Ring of rays that alternate long and short. Length is the long ray, and Tweak sets the short ray's length.",
        [ShapeKind.Lattice] = "Criss-cross lines between Radius − Length/2 and Radius + Length/2. Tweak sets how far each line leans (1 to 3 steps). Line art.",
        [ShapeKind.Vine] = "Wavy stem around the ring with a leaf on every crest and trough. Count = waves, and Length = band height. Tweak sets the leaf size.",
        [ShapeKind.Rope] = "Twisted-rope band between two circles. Count = twists, and Length = band height. Tweak sets how far each strand leans.",
        [ShapeKind.GreekKey] = "Classic Greek key (meander) border between two circles. Count = repeats, and Length = band height.",
        [ShapeKind.Waves] = "Ocean waves: a row of crests curling into spirals, between two circles. Count = waves, and Length = band height.",
        [ShapeKind.Chain] = "Chain of interlocking links around the ring, alternating face-on and edge-on. Count = links, and Length = link height.",
        [ShapeKind.Stitch] = "Stitched ribbon: a band between two circles with a dashed stitch line down the middle. Count = stitches. Tweak sets the stitch length.",
        [ShapeKind.FlowerOfLife] = "Flower of Life band: overlapping circles between two rings, making petal shapes to color. Count = circles, and Length = band height.",
        [ShapeKind.SwirlBand] = "Pinwheel swirl: curved blades sweeping around between Radius − Length/2 and Radius + Length/2. Count = blades. Tweak sets the twist. With the inner edge at the middle, it makes a swirl center.",
        [ShapeKind.Gear] = "Gear-tooth ring with Count teeth, each Length tall. Tweak sets the tooth width.",
        [ShapeKind.Polygon] = "Count-sided polygon. Raise Tweak to turn it into a star polygon (string-art look).",
    };

    private string _name = "Layer";
    private bool _visible = true;
    private ShapeKind _shape = ShapeKind.Petal;
    private int _count = 12;
    private double _radius = 40;
    private double _length = 24;
    private double _width = 10;
    private double _tweak = 0.5;
    private double _ringRotation;
    private bool _offsetHalfStep;
    private double _spin;
    private double _strokeWidth = 0.6;
    private string _strokeColor = "#264653";
    private bool _filled = true;
    private string _fillColor = "#E9C46A";

    public string Name { get => _name; set => Set(ref _name, value); }
    public bool Visible { get => _visible; set => Set(ref _visible, value); }
    public ShapeKind Shape { get => _shape; set => Set(ref _shape, value); }
    public int Count { get => _count; set => Set(ref _count, value < 1 ? 1 : value); }
    public double Radius { get => _radius; set => Set(ref _radius, value); }
    public double Length
    {
        get => _length;
        set
        {
            double old = _length;
            if (!Set(ref _length, value)) return;
            if (_lockRatio) ScalePartner(ref _width, nameof(Width), old, value);
        }
    }

    public double Width
    {
        get => _width;
        set
        {
            double old = _width;
            if (!Set(ref _width, value)) return;
            if (_lockRatio) ScalePartner(ref _length, nameof(Length), old, value);
        }
    }

    private bool _lockRatio;

    /// <summary>
    /// Keep Length:Width constant: changing one scales the other.
    /// Written last in saved files (so loading never rescales) and applied last in Clone.
    /// </summary>
    [JsonPropertyOrder(100)]
    public bool LockRatio { get => _lockRatio; set => Set(ref _lockRatio, value); }

    /// <summary>Length ÷ Width, for display next to the lock.</summary>
    [JsonIgnore]
    public string RatioText => _width > 0.0001 ? $"{_length / _width:0.##} : 1" : "—";

    private void ScalePartner(ref double partner, string partnerName, double oldValue, double newValue)
    {
        // Can't keep a ratio through zero, so leave the other value alone
        if (oldValue <= 0.0001 || newValue <= 0) return;
        partner = Math.Round(partner * newValue / oldValue, 3);
        OnPropertyChanged(partnerName);
    }
    public double Tweak { get => _tweak; set => Set(ref _tweak, value); }
    public double RingRotation { get => _ringRotation; set => Set(ref _ringRotation, value); }
    public bool OffsetHalfStep { get => _offsetHalfStep; set => Set(ref _offsetHalfStep, value); }
    public double Spin { get => _spin; set => Set(ref _spin, value); }
    public double StrokeWidth { get => _strokeWidth; set => Set(ref _strokeWidth, value); }
    public string StrokeColor { get => _strokeColor; set => Set(ref _strokeColor, value); }
    public bool Filled { get => _filled; set => Set(ref _filled, value); }
    public string FillColor { get => _fillColor; set => Set(ref _fillColor, value); }

    // ---- Custom shape ----
    private IReadOnlyList<ShapeNode> _customNodes = DefaultCustomNodes;
    private bool _customSmooth = true;
    private bool _customMirror = true;
    private bool _customClosed = true;

    /// <summary>
    /// Outline points of the Custom shape. The list is treated as immutable: editors assign a new list
    /// so that change notification fires and clones can safely share it.
    /// </summary>
    public IReadOnlyList<ShapeNode> CustomNodes { get => _customNodes; set => Set(ref _customNodes, value ?? DefaultCustomNodes); }

    /// <summary>Draw smooth curves through the points instead of straight lines.</summary>
    public bool CustomSmooth { get => _customSmooth; set => Set(ref _customSmooth, value); }

    /// <summary>The points describe one half, and they're mirrored across the radial center line.</summary>
    public bool CustomMirror { get => _customMirror; set => Set(ref _customMirror, value); }

    /// <summary>A closed outline (fillable) or an open line (line art).</summary>
    public bool CustomClosed { get => _customClosed; set => Set(ref _customClosed, value); }

    /// <summary>Half of a bulb-shaped petal, ready to be mirrored.</summary>
    public static IReadOnlyList<ShapeNode> DefaultCustomNodes { get; } = new[]
    {
        new ShapeNode(-1, 0), new ShapeNode(-0.55, -0.6), new ShapeNode(0.15, -0.95),
        new ShapeNode(0.7, -0.55), new ShapeNode(1, 0),
    };

    // ---- Details: extra line work drawn on every copy ----
    private int _insetCount;
    private double _insetGap = 0.15;
    private InnerMotif _inner = InnerMotif.None;
    private double _innerSize = 0.35;
    private double _innerOffset;
    private HatchStyle _hatch = HatchStyle.None;
    private int _hatchCount = 6;
    private EchoStyle _echoStyle = EchoStyle.Lines;
    private EchoAccent _echoAccent = EchoAccent.Dots;

    /// <summary>Number of smaller echo outlines inside each motif (or concentric echoes for ring shapes).</summary>
    public int InsetCount { get => _insetCount; set => Set(ref _insetCount, value < 0 ? 0 : value); }

    /// <summary>Spacing between echo outlines, as a fraction of the shape size.</summary>
    public double InsetGap { get => _insetGap; set => Set(ref _insetGap, value); }

    public InnerMotif Inner { get => _inner; set => Set(ref _inner, value); }

    /// <summary>Inner shape size as a fraction of the motif.</summary>
    public double InnerSize { get => _innerSize; set => Set(ref _innerSize, value); }

    /// <summary>Inner shape position along the motif: -1 = toward the center, 0 = middle, 1 = outer end.</summary>
    public double InnerOffset { get => _innerOffset; set => Set(ref _innerOffset, value); }

    public HatchStyle Hatch { get => _hatch; set => Set(ref _hatch, value); }

    public int HatchCount { get => _hatchCount; set => Set(ref _hatchCount, value < 1 ? 1 : value); }

    /// <summary>Echo outlines as solid lines, rows of dots, or a solid first echo with dotted ones inside.</summary>
    public EchoStyle EchoStyle { get => _echoStyle; set => Set(ref _echoStyle, value); }

    /// <summary>Decoration for the inner echoes when EchoStyle is Mixed.</summary>
    public EchoAccent EchoAccent { get => _echoAccent; set => Set(ref _echoAccent, value); }

    [JsonIgnore]
    public string Summary => $"{Shape} × {Count} @ {Radius:0.#} mm";

    [JsonIgnore]
    public string ShapeHelp => Help.TryGetValue(Shape, out var h) ? h : "";

    [JsonIgnore]
    public bool IsCustom => Shape == ShapeKind.Custom;

    /// <summary>Built-in motifs can be turned into an editable custom outline.</summary>
    [JsonIgnore]
    public bool CanEditOutline => Shape != ShapeKind.Custom && !Shapes.ShapeGenerator.IsRingShape(Shape);

    public Layer Clone() => new()
    {
        Name = Name, Visible = Visible, Shape = Shape, Count = Count, Radius = Radius, Length = Length,
        Width = Width, Tweak = Tweak, RingRotation = RingRotation, OffsetHalfStep = OffsetHalfStep, Spin = Spin,
        StrokeWidth = StrokeWidth, StrokeColor = StrokeColor, Filled = Filled, FillColor = FillColor,
        CustomNodes = CustomNodes, CustomSmooth = CustomSmooth, CustomMirror = CustomMirror, CustomClosed = CustomClosed,
        InsetCount = InsetCount, InsetGap = InsetGap, Inner = Inner, InnerSize = InnerSize, InnerOffset = InnerOffset,
        Hatch = Hatch, HatchCount = HatchCount, EchoStyle = EchoStyle, EchoAccent = EchoAccent,
        LockRatio = LockRatio, // last, so setting Length and Width above doesn't rescale anything
    };

    protected override void OnPropertyChanged(string? name = null)
    {
        base.OnPropertyChanged(name);
        if (name is nameof(Shape) or nameof(Count) or nameof(Radius))
            base.OnPropertyChanged(nameof(Summary));
        if (name is nameof(Length) or nameof(Width))
            base.OnPropertyChanged(nameof(RatioText));
        if (name is nameof(Shape))
        {
            base.OnPropertyChanged(nameof(ShapeHelp));
            base.OnPropertyChanged(nameof(IsCustom));
            base.OnPropertyChanged(nameof(CanEditOutline));
        }
    }
}
