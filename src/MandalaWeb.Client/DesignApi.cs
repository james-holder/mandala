using System.Net.Http.Json;

namespace MandalaWeb.Client;

/// <summary>Kind is "mandala", "traced" (a traced image), "page" (a laid-out page of designs and text) or "laser" (a laser-cut design).</summary>
public sealed record DesignSummary(long Id, string Name, DateTime UpdatedUtc, long? FolderId = null, string Kind = "mandala")
{
    public bool IsTraced => Kind == "traced";
    public bool IsPage => Kind == "page";
    public bool IsMandala => Kind == "mandala";
    public bool IsLaser => Kind == "laser";

    /// <summary>Mandalas, traced pictures/pages and laser designs are kept in separate libraries, each with its own folders.</summary>
    public string Library => Kind switch { "mandala" => DesignApi.Mandalas, "laser" => DesignApi.Laser, _ => DesignApi.Pages };

    /// <summary>Where the design is edited.</summary>
    public string EditUrl => Kind switch { "traced" => $"trace/{Id}", "page" => $"page/{Id}", "laser" => $"laser/{Id}", _ => $"d/{Id}" };
}

public sealed record FolderSummary(long Id, string Name, string Library = "mandala");

/// <summary>Talks to the server's saved-designs API.</summary>
public sealed class DesignApi
{
    public const string Mandalas = "mandala", Pages = "pages", Laser = "laser";

    private readonly HttpClient _http;

    public DesignApi(HttpClient http) => _http = http;

    private sealed record Upload(string? Name, string Json, string Svg, long? FolderId = null, string? Kind = null);
    private sealed record MoveRequest(long[] Ids, long? FolderId);
    private sealed record FolderRequest(string Name, string Library);
    private sealed record NameResult(string Name);
    private sealed record RenameRequest(string Name);

    public async Task<List<DesignSummary>> ListAsync() =>
        await _http.GetFromJsonAsync<List<DesignSummary>>("api/designs") ?? new();

    public async Task<string> NextNameAsync() =>
        (await _http.GetFromJsonAsync<NameResult>("api/designs/next-name"))?.Name ?? "Mandala_001";

    public Task<string> GetJsonAsync(long id) => _http.GetStringAsync($"api/designs/{id}");

    public Task<string> GetSvgAsync(long id) => _http.GetStringAsync($"api/designs/{id}/svg");

    public async Task<DesignSummary> CreateAsync(string? name, string json, string svg, long? folderId = null, string? kind = null)
    {
        var response = await _http.PostAsJsonAsync("api/designs", new Upload(name, json, svg, folderId, kind));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DesignSummary>())!;
    }

    public async Task UpdateAsync(long id, string? name, string json, string svg)
    {
        var response = await _http.PutAsJsonAsync($"api/designs/{id}", new Upload(name, json, svg));
        response.EnsureSuccessStatusCode();
    }

    public async Task RenameAsync(long id, string name) =>
        (await _http.PutAsJsonAsync($"api/designs/{id}/name", new RenameRequest(name))).EnsureSuccessStatusCode();

    public async Task DeleteAsync(long id) => (await _http.DeleteAsync($"api/designs/{id}")).EnsureSuccessStatusCode();

    /// <summary>Moves designs into a folder, or to Unfiled when folderId is null.</summary>
    public async Task MoveAsync(IEnumerable<long> ids, long? folderId) =>
        (await _http.PutAsJsonAsync("api/designs/move", new MoveRequest(ids.ToArray(), folderId))).EnsureSuccessStatusCode();

    // ---------- Folders ----------

    /// <summary>The folders of one library (Mandalas or Pages).</summary>
    public async Task<List<FolderSummary>> FoldersAsync(string library) =>
        (await _http.GetFromJsonAsync<List<FolderSummary>>("api/folders") ?? new()).Where(f => f.Library == library).ToList();

    public async Task<FolderSummary> CreateFolderAsync(string name, string library)
    {
        var response = await _http.PostAsJsonAsync("api/folders", new FolderRequest(name, library));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FolderSummary>())!;
    }

    public async Task RenameFolderAsync(long id, string name) =>
        (await _http.PutAsJsonAsync($"api/folders/{id}/name", new RenameRequest(name))).EnsureSuccessStatusCode();

    public async Task DeleteFolderAsync(long id) => (await _http.DeleteAsync($"api/folders/{id}")).EnsureSuccessStatusCode();
}
