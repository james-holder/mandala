using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MandalaMaker.Model;

public enum ShapePreference
{
    Never,
    Normal,
    Favorite,
}

/// <summary>
/// Shape preferences for the random generators. The web app keeps these in the browser's local storage as JSON
/// (the desktop app keeps the same structure in a settings file).
/// </summary>
public sealed class AppSettings
{
    private const double FavoriteWeight = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>How often the random generator should use each shape. Shapes not listed use their default.</summary>
    public Dictionary<ShapeKind, ShapePreference> ShapePreferences { get; set; } = new();

    public ShapePreference GetPreference(ShapeKind kind) =>
        ShapePreferences.TryGetValue(kind, out var p) ? p : DefaultPreference(kind);

    /// <summary>Relative pick weight for the random generator (0 = never).</summary>
    public double Weight(ShapeKind kind) => WeightOf(GetPreference(kind));

    public static double WeightOf(ShapePreference p) => p switch
    {
        ShapePreference.Never => 0,
        ShapePreference.Favorite => FavoriteWeight,
        _ => 1,
    };

    public static ShapePreference DefaultPreference(ShapeKind kind) => kind switch
    {
        // Line art and your own shape stay out of random designs unless you opt in
        ShapeKind.Custom or ShapeKind.Spoke or ShapeKind.Swirl or ShapeKind.Polygon => ShapePreference.Never,
        ShapeKind.Petal or ShapeKind.Dome or ShapeKind.Scallop => ShapePreference.Favorite,
        _ => ShapePreference.Normal,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Reads settings JSON. Anything unreadable falls back to defaults rather than stopping the app.</summary>
    public static AppSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AppSettings();
        try { return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings(); }
        catch (Exception) { return new AppSettings(); }
    }
}
