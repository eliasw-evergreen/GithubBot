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
    string? CreatedBy,
    string? AreaPath,
    string? Url,
    int? Priority = null,
    double? Size = null,
    string? Description = null,
    string? ReproSteps = null,
    string? ExpectedOutcome = null,
    string? ActualOutcome = null);

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
            var fields = "System.Id,System.Title,System.WorkItemType,System.State,System.AssignedTo,System.CreatedBy,System.AreaPath,System.Description,Microsoft.VSTS.Common.Priority,Microsoft.VSTS.Scheduling.StoryPoints,Microsoft.VSTS.Scheduling.Effort,Microsoft.VSTS.TCM.ReproSteps,Custom.ExpectedOutcome,Custom.ActualOutcome";
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

                var rawAssigned = f.TryGetProperty("System.AssignedTo", out var rawA) ? rawA.GetRawText() : "(missing)";
                var rawCreated  = f.TryGetProperty("System.CreatedBy",  out var rawC) ? rawC.GetRawText() : "(missing)";
                _logger.LogInformation("[ADO API] identity fields — AssignedTo={A} CreatedBy={C}", rawAssigned, rawCreated);

                results.Add(new AdoWorkItem(
                    Id:             el.TryGetProperty("id", out var id_) ? id_.GetInt32() : 0,
                    Title:          Str(f, "System.Title"),
                    WorkItemType:   Str(f, "System.WorkItemType"),
                    State:          Str(f, "System.State"),
                    AssignedTo:     Identity(f, "System.AssignedTo"),
                    CreatedBy:      Identity(f, "System.CreatedBy"),
                    AreaPath:       Str(f, "System.AreaPath"),
                    Url:            el.TryGetProperty("url", out var u) ? u.GetString() : null,
                    Priority:       Num(f, "Microsoft.VSTS.Common.Priority"),
                    Size:           NumDouble(f, "Microsoft.VSTS.Scheduling.StoryPoints") ?? NumDouble(f, "Microsoft.VSTS.Scheduling.Effort"),
                    Description:    Str(f, "System.Description"),
                    ReproSteps:     Str(f, "Microsoft.VSTS.TCM.ReproSteps"),
                    ExpectedOutcome: Str(f, "Custom.ExpectedOutcome"),
                    ActualOutcome:  Str(f, "Custom.ActualOutcome")));
            }
        }
        return results;
    }

    /// <summary>
    /// Returns active unassigned work items in the project, with optional filters.
    /// </summary>
    public async Task<List<AdoWorkItem>> GetUnassignedWorkItemsAsync(
        int? minPriority = null,
        double? maxSize = null,
        string? areaPath = null,
        string? type = null,
        string? state = null,
        DateTime? createdAfter = null,
        string? orderBy = null,
        CancellationToken ct = default)
    {
        var conditions = new List<string> { "[System.AssignedTo] = ''" };

        if (string.IsNullOrEmpty(state))
            conditions.Add("[System.State] NOT IN ('Closed', 'Resolved', 'Done', 'Removed')");
        else
            conditions.Add($"[System.State] = '{state.Replace("'", "''")}'");

        if (minPriority.HasValue)
            conditions.Add($"[Microsoft.VSTS.Common.Priority] <= {minPriority.Value}");
        if (maxSize.HasValue)
            conditions.Add($"([Microsoft.VSTS.Scheduling.StoryPoints] <= {maxSize.Value} OR [Microsoft.VSTS.Scheduling.Effort] <= {maxSize.Value})");
        if (!string.IsNullOrWhiteSpace(areaPath))
            conditions.Add($"[System.AreaPath] UNDER '{areaPath.Replace("'", "''")}'");
        if (!string.IsNullOrWhiteSpace(type))
            conditions.Add($"[System.WorkItemType] = '{type.Replace("'", "''")}'");
        if (createdAfter.HasValue)
            conditions.Add($"[System.CreatedDate] >= '{createdAfter.Value:yyyy-MM-ddTHH:mm:ssZ}'");

        var order = orderBy switch
        {
            "priority" => "[Microsoft.VSTS.Common.Priority] ASC",
            "size"     => "[Microsoft.VSTS.Scheduling.StoryPoints] ASC",
            "id"       => "[System.Id] ASC",
            _          => "[System.CreatedDate] DESC",
        };

        var wiql = $"SELECT [System.Id] FROM WorkItems WHERE {string.Join(" AND ", conditions)} ORDER BY {order}";
        var ids = await RunWiqlAsync(wiql, ct);
        return await GetWorkItemsAsync(ids, ct);
    }

    private List<(int Id, string? Title, string? Type)>? _summaryCache;

    /// <summary>
    /// Returns a lightweight list of work item IDs, titles, and types for autocomplete.
    /// Results are cached for 10 minutes.
    /// </summary>
    public async Task<List<(int Id, string? Title, string? Type)>> GetWorkItemSummariesAsync(CancellationToken ct = default)
    {
        if (_summaryCache != null) return _summaryCache;

        var wiql = "SELECT [System.Id] FROM WorkItems WHERE [System.State] NOT IN ('Removed') ORDER BY [System.ChangedDate] DESC";
        var ids = await RunWiqlAsync(wiql, ct);
        if (ids.Count == 0) return _summaryCache ?? [];

        // Fetch only Id, Title, WorkItemType — lightweight
        var results = new List<(int, string?, string?)>();
        foreach (var batch in ids.Chunk(200))
        {
            var joined = string.Join(',', batch);
            var url = $"{_orgUrl}/_apis/wit/workitems?ids={joined}&fields=System.Id,System.Title,System.WorkItemType&api-version=7.1";
            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) continue;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("value", out var value)) continue;

            foreach (var el in value.EnumerateArray())
            {
                var f = el.TryGetProperty("fields", out var fi) ? fi : default;
                if (f.ValueKind == JsonValueKind.Undefined) continue;
                var id = el.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
                results.Add((id, Str(f, "System.Title"), Str(f, "System.WorkItemType")));
            }
        }

        _summaryCache = results;
        return results;
    }

    public void InvalidateSummaryCache() => _summaryCache = null;

    /// <summary>
    /// Returns cached summaries immediately (empty list if cache is cold).
    /// Triggers a background refresh if the cache is missing or stale.
    /// Safe to call from autocomplete handlers where latency is &lt;3 s.
    /// </summary>
    public List<(int Id, string? Title, string? Type)> GetCachedSummaries()
    {
        if (_summaryCache == null) _ = RefreshSummariesAsync();
        return _summaryCache ?? [];
    }

    private Task? _refreshTask;
    private async Task RefreshSummariesAsync()
    {
        if (_refreshTask != null && !_refreshTask.IsCompleted) return;
        _refreshTask = GetWorkItemSummariesAsync();
        try { await _refreshTask; }
        catch (Exception ex) { _logger.LogWarning(ex, "[ADO API] Background summary refresh failed"); }
    }

    private List<string>? _areaPathCache;

    /// <summary>
    /// Returns all area paths in the project, flattened. Results are cached for the lifetime of the service.
    /// </summary>
    public async Task<List<string>> GetAreaPathsAsync(CancellationToken ct = default)
    {
        if (_areaPathCache != null) return _areaPathCache;

        var url = $"{_orgUrl}/{Uri.EscapeDataString(_project)}/_apis/wit/classificationnodes/areas?$depth=20&api-version=7.1";
        var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[ADO API] GetAreaPaths failed: {Status}", response.StatusCode);
            return [];
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var paths = new List<string>();
        FlattenAreaNode(doc.RootElement, "", paths);
        _areaPathCache = paths;
        return paths;
    }

    private static void FlattenAreaNode(JsonElement node, string prefix, List<string> results)
    {
        var name = node.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var path = string.IsNullOrEmpty(prefix) ? name : $"{prefix}\\{name}";
        if (!string.IsNullOrEmpty(path)) results.Add(path);

        if (node.TryGetProperty("children", out var children))
            foreach (var child in children.EnumerateArray())
                FlattenAreaNode(child, path, results);
    }

    public async Task<bool> PatchWorkItemAsync(int id, int? priority, string? severity, string? estimatedSize, CancellationToken ct = default)
    {
        var ops = new List<object>();
        if (priority.HasValue)
            ops.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Common.Priority", value = (object)priority.Value });
        if (!string.IsNullOrEmpty(severity))
            ops.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Common.Severity", value = (object)severity });
        if (!string.IsNullOrEmpty(estimatedSize))
            ops.Add(new { op = "add", path = "/fields/Custom.Estimatedsize", value = (object)estimatedSize });

        if (ops.Count == 0) return true;

        var url = $"{_orgUrl}/{Uri.EscapeDataString(_project)}/_apis/wit/workitems/{id}?api-version=7.1";
        var json = JsonSerializer.Serialize(ops);
        var request = new HttpRequestMessage(new HttpMethod("PATCH"), url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json-patch+json")
        };
        var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            _logger.LogWarning("[ADO API] PatchWorkItem #{Id} failed: {Status}", id, response.StatusCode);
        return response.IsSuccessStatusCode;
    }

    public string BuildWorkItemUrl(int id)
        => $"{_orgUrl}/{Uri.EscapeDataString(_project)}/_workitems/edit/{id}";

    private static string? Str(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ADO identity fields come back as objects: { "displayName": "...", "uniqueName": "email@..." }
    // Fall back to string format "Display Name <email@...>" for older API responses.
    private static string? Identity(JsonElement el, string key)
    {
        if (!el.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Object)
        {
            if (v.TryGetProperty("uniqueName", out var un) && un.ValueKind == JsonValueKind.String) return un.GetString();
            if (v.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String) return dn.GetString();
            return null;
        }
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static int? Num(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static double? NumDouble(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
