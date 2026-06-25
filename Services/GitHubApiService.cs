using System.Net.Http.Headers;
using System.Text.Json;

namespace GithubBot.Services;

public record GitHubPr(
    int Number,
    string? Title,
    bool Draft,
    string? State,
    string? HtmlUrl,
    string? UserLogin = null,
    string? Body = null,
    string? HeadRef = null,
    string? BaseRef = null,
    string? NodeId = null);

public record GitHubPrSummary(string Repo, int Number, string? Title, bool Draft, string? HtmlUrl);

public class GitHubApiService
{
    private readonly HttpClient _http;
    private readonly ILogger<GitHubApiService> _logger;

    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };
    private volatile List<GitHubPrSummary>? _summaryCache;

    public GitHubApiService(string pat, ILogger<GitHubApiService> logger)
    {
        _logger = logger;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GithubBot/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    private static GitHubPr ParsePr(JsonElement el)
    {
        string? userLogin = null;
        if (el.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object &&
            user.TryGetProperty("login", out var loginProp) && loginProp.ValueKind == JsonValueKind.String)
            userLogin = loginProp.GetString();

        string? headRef = null;
        if (el.TryGetProperty("head", out var head) && head.ValueKind == JsonValueKind.Object &&
            head.TryGetProperty("ref", out var hr) && hr.ValueKind == JsonValueKind.String)
            headRef = hr.GetString();

        string? baseRef = null;
        if (el.TryGetProperty("base", out var @base) && @base.ValueKind == JsonValueKind.Object &&
            @base.TryGetProperty("ref", out var br) && br.ValueKind == JsonValueKind.String)
            baseRef = br.GetString();

        return new GitHubPr(
            Number:    el.TryGetProperty("number", out var n) ? n.GetInt32() : 0,
            Title:     el.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
            Draft:     el.TryGetProperty("draft", out var d) && d.GetBoolean(),
            State:     el.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null,
            HtmlUrl:   el.TryGetProperty("html_url", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null,
            UserLogin: userLogin,
            Body:      el.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null,
            HeadRef:   headRef,
            BaseRef:   baseRef,
            NodeId:    el.TryGetProperty("node_id", out var nid) && nid.ValueKind == JsonValueKind.String ? nid.GetString() : null);
    }

    public async Task<GitHubPr?> GetPullRequestAsync(string repoFullName, int prNumber, CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{repoFullName}/pulls/{prNumber}";
        var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[GitHub API] GetPullRequest {Repo}#{Pr} failed: {Status}", repoFullName, prNumber, response.StatusCode);
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return ParsePr(doc.RootElement);
    }

    public async Task<List<GitHubPr>> GetOpenPullRequestsAsync(string repoFullName, CancellationToken ct = default)
    {
        var results = new List<GitHubPr>();
        var page = 1;
        while (true)
        {
            var url = $"https://api.github.com/repos/{repoFullName}/pulls?state=open&per_page=100&page={page}";
            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[GitHub API] GetOpenPRs {Repo} page {Page} failed: {Status}", repoFullName, page, response.StatusCode);
                break;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var arr = doc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) break;

            foreach (var el in arr.EnumerateArray())
                results.Add(ParsePr(el) with { State = "open" });

            if (arr.GetArrayLength() < 100) break;
            page++;
        }
        return results;
    }

    public async Task<List<GithubBot.Models.IssueComment>> GetPullRequestCommentsAsync(string repoFullName, int prNumber, CancellationToken ct = default)
    {
        var results = new List<GithubBot.Models.IssueComment>();
        var page = 1;
        while (true)
        {
            var url = $"https://api.github.com/repos/{repoFullName}/issues/{prNumber}/comments?per_page=100&page={page}";
            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) break;
            var arr = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) break;
            var batch = arr.Deserialize<List<GithubBot.Models.IssueComment>>(_json) ?? [];
            results.AddRange(batch);
            if (batch.Count < 100) break;
            page++;
        }
        return results;
    }

    public async Task<List<GithubBot.Models.Review>> GetPullRequestReviewsAsync(string repoFullName, int prNumber, CancellationToken ct = default)
    {
        var results = new List<GithubBot.Models.Review>();
        var page = 1;
        while (true)
        {
            var url = $"https://api.github.com/repos/{repoFullName}/pulls/{prNumber}/reviews?per_page=100&page={page}";
            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) break;
            var arr = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) break;
            var batch = arr.Deserialize<List<GithubBot.Models.Review>>(_json) ?? [];
            results.AddRange(batch);
            if (batch.Count < 100) break;
            page++;
        }
        return results;
    }

    // Returns cached summaries immediately; triggers background refresh if cache is empty.
    public List<GitHubPrSummary> GetCachedPrSummaries(IEnumerable<string> repos)
    {
        if (_summaryCache != null) return _summaryCache;
        var repoList = repos.Distinct().ToList();
        _ = Task.Run(() => RefreshPrSummariesAsync(repoList));
        return [];
    }

    public async Task RefreshPrSummariesAsync(IEnumerable<string> repos, CancellationToken ct = default)
    {
        var repoList = repos.Distinct().ToList();
        if (repoList.Count == 0) return;

        var result = new List<GitHubPrSummary>();
        foreach (var repo in repoList)
        {
            try
            {
                var prs = await GetOpenPullRequestsAsync(repo, ct);
                result.AddRange(prs.Select(p => new GitHubPrSummary(repo, p.Number, p.Title, p.Draft, p.HtmlUrl)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GitHub API] PR summary refresh failed for repo {Repo}", repo);
            }
        }
        _summaryCache = result;
        _logger.LogInformation("[GitHub API] PR summary cache: {Count} open PRs across {RepoCount} repo(s)", result.Count, repoList.Count);
    }

    public void InvalidatePrSummaryCache() => _summaryCache = null;
}
