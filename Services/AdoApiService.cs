using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GithubBot.Services;

public record AdoWorkItem(
    int Id,
    string? Title,
    string? WorkItemType,
    string? State,
    string? AssignedTo,
    string? AreaPath,
    string? Url);

public class AdoApiService
{
    private readonly HttpClient _http;
    private readonly string _orgUrl;
    private readonly string _project;
    private readonly ILogger<AdoApiService> _logger;

    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public AdoApiService(string orgUrl, string project, string pat, ILogger<AdoApiService> logger)
    {
        _orgUrl  = orgUrl.TrimEnd('/');
        _project = project;
        _logger  = logger;

        _http = new HttpClient();
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Run an arbitrary WIQL query and return the matching work item IDs.
    /// </summary>
    public async Task<List<int>> RunWiqlAsync(string wiql, CancellationToken ct = default)
    {
        var url = $"{_orgUrl}/{Uri.EscapeDataString(_project)}/_apis/wit/wiql?api-version=7.1";
        var body = JsonSerializer.Serialize(new { query = wiql });
        var response = await _http.PostAsync(url,
            new StringContent(body, Encoding.UTF8, "application/json"), ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[ADO API] WIQL query failed: {Status}", response.StatusCode);
            return [];
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var ids = new List<int>();
        if (doc.RootElement.TryGetProperty("workItems", out var items))
            foreach (var item in items.EnumerateArray())
                if (item.TryGetProperty("id", out var id))
                    ids.Add(id.GetInt32());
        return ids;
    }

    /// <summary>
    /// Fetch full details for a batch of work item IDs.
    /// ADO allows up to 200 IDs per request.
    /// </summary>
    public async Task<List<AdoWorkItem>> GetWorkItemsAsync(IEnumerable<int> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return [];

        var results = new List<AdoWorkItem>();
        foreach (var batch in idList.Chunk(200))
        {
            var joined = string.Join(',', batch);
            var fields = "System.Id,System.Title,System.WorkItemType,System.State,System.AssignedTo,System.AreaPath";
            var url = $"{_orgUrl}/_apis/wit/workitems?ids={joined}&fields={fields}&api-version=7.1";
            var response = await _http.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[ADO API] GetWorkItems failed: {Status}", response.StatusCode);
                continue;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("value", out var value)) continue;

            foreach (var el in value.EnumerateArray())
            {
                var f = el.TryGetProperty("fields", out var fields_) ? fields_ : default;
                if (f.ValueKind == JsonValueKind.Undefined) continue;

                results.Add(new AdoWorkItem(
                    Id:           el.TryGetProperty("id", out var id_) ? id_.GetInt32() : 0,
                    Title:        Str(f, "System.Title"),
                    WorkItemType: Str(f, "System.WorkItemType"),
                    State:        Str(f, "System.State"),
                    AssignedTo:   Str(f, "System.AssignedTo"),
                    AreaPath:     Str(f, "System.AreaPath"),
                    Url:          el.TryGetProperty("url", out var u) ? u.GetString() : null));
            }
        }
        return results;
    }

    /// <summary>
    /// Returns all active unassigned work items in the project.
    /// </summary>
    public async Task<List<AdoWorkItem>> GetUnassignedWorkItemsAsync(CancellationToken ct = default)
    {
        var wiql = """
            SELECT [System.Id] FROM WorkItems
            WHERE [System.AssignedTo] = ''
            AND [System.State] NOT IN ('Closed', 'Resolved', 'Done', 'Removed')
            ORDER BY [System.CreatedDate] DESC
            """;
        var ids = await RunWiqlAsync(wiql, ct);
        return await GetWorkItemsAsync(ids, ct);
    }

    private static string? Str(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
