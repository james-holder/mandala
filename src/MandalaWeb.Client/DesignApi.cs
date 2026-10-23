using System.Net.Http.Json;

namespace MandalaWeb.Client;

public sealed record DesignSummary(long Id, string Name, DateTime UpdatedUtc, long? FolderId = null);

public sealed record FolderSummary(long Id, string Name);

/// <summary>Talks to the server's saved-designs API.</summary>
public sealed class DesignApi
{
    private readonly HttpClient _http;

    public DesignApi(HttpClient http) => _http = http;

    private sealed record Upload(string? Name, string Json, string Svg, long? FolderId = null);
    private sealed record MoveRequest(long[] Ids, long? FolderId);
    private sealed record NameResult(string Name);
    private sealed record RenameRequest(string Name);

    public async Task<List<DesignSummary>> ListAsync() =>
        await _http.GetFromJsonAsync<List<DesignSummary>>("api/designs") ?? new();

    public async Task<string> NextNameAsync() =>
        (await _http.GetFromJsonAsync<NameResult>("api/designs/next-name"))?.Name ?? "Mandala_001";

    public Task<string> GetJsonAsync(long id) => _http.GetStringAsync($"api/designs/{id}");

    public Task<string> GetSvgAsync(long id) => _http.GetStringAsync($"api/designs/{id}/svg");

    public async Task<DesignSummary> CreateAsync(string? name, string json, string svg, long? folderId = null)
    {
        var response = await _http.PostAsJsonAsync("api/designs", new Upload(name, json, svg, folderId));
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

    public async Task<List<FolderSummary>> FoldersAsync() =>
        await _http.GetFromJsonAsync<List<FolderSummary>>("api/folders") ?? new();

    public async Task<FolderSummary> CreateFolderAsync(string name)
    {
        var response = await _http.PostAsJsonAsync("api/folders", new RenameRequest(name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FolderSummary>())!;
    }

    public async Task RenameFolderAsync(long id, string name) =>
        (await _http.PutAsJsonAsync($"api/folders/{id}/name", new RenameRequest(name))).EnsureSuccessStatusCode();

    public async Task DeleteFolderAsync(long id) => (await _http.DeleteAsync($"api/folders/{id}")).EnsureSuccessStatusCode();
}
