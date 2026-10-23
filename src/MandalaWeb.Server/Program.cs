// Hosts the Blazor WebAssembly app and a small API for saved designs (SQLite).
// Designs are generated in the browser; the server only stores and returns them.
using MandalaWeb.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);
// Designs with lots of detail make large SVGs, so allow bigger uploads than the default
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 20 * 1024 * 1024);

string dbPath = Environment.GetEnvironmentVariable("DESIGNS_DB")
    ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "designs.db");
builder.Services.AddSingleton(new DesignStore(dbPath));

var app = builder.Build();
app.UseResponseCompression();
app.UseBlazorFrameworkFiles();
// The page and its own scripts and styles keep the same names between versions, so browsers must check for
// updates every time; otherwise someone could keep running an old page after a deploy
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        string path = ctx.File.Name;
        if (path.EndsWith(".html") || ctx.Context.Request.Path.StartsWithSegments("/js") || ctx.Context.Request.Path.StartsWithSegments("/css"))
            ctx.Context.Response.Headers.CacheControl = "no-cache";
    },
});
app.MapGet("/healthz", () => "ok");

var api = app.MapGroup("/api/designs");
api.MapGet("/", (DesignStore s) => s.List());
api.MapGet("/next-name", (DesignStore s) => new { name = s.NextName() });
api.MapGet("/{id:long}", (long id, DesignStore s) =>
    s.GetJson(id) is { } d ? Results.Text(d.Json, "application/json") : Results.NotFound());
api.MapGet("/{id:long}/svg", (long id, DesignStore s, HttpContext ctx) =>
{
    if (s.GetSvg(id) is not { } d) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "no-cache"; // thumbnails refresh when a design is re-saved
    return Results.Text(d.Svg, "image/svg+xml");
});
api.MapPost("/", (DesignUpload upload, DesignStore s) =>
    string.IsNullOrWhiteSpace(upload.Json) ? Results.BadRequest() : Results.Ok(s.Create(upload)));
api.MapPut("/{id:long}", (long id, DesignUpload upload, DesignStore s) =>
    s.Update(id, upload) ? Results.Ok() : Results.NotFound());
api.MapPut("/{id:long}/name", (long id, RenameRequest req, DesignStore s) =>
    s.Rename(id, req.Name) ? Results.Ok() : Results.BadRequest());
api.MapDelete("/{id:long}", (long id, DesignStore s) =>
    s.Delete(id) ? Results.Ok() : Results.NotFound());

// Local testing only: lets a test script save a generated file to disk for inspection. Never on in production.
if (app.Environment.IsDevelopment() && Environment.GetEnvironmentVariable("MANDALA_TEST_OUT") is { Length: > 0 } testOut)
{
    app.MapPost("/dev/upload/{name}", async (string name, HttpRequest req) =>
    {
        Directory.CreateDirectory(testOut);
        await using var f = File.Create(Path.Combine(testOut, Path.GetFileName(name)));
        await req.Body.CopyToAsync(f);
        return Results.Ok();
    });
}

app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});
app.Run();

record RenameRequest(string Name);
