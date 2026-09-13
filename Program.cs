using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("pihole", client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<ReviewStore>();
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<PiholeSessionStore>();

var app = builder.Build();

// Load configuration before the review store migrates its legacy combined file.
app.Services.GetRequiredService<ConfigStore>();
app.Services.GetRequiredService<ReviewStore>();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/review-state", (ReviewStore store) => Results.Ok(store.Snapshot()));
app.MapGet("/api/config", (ConfigStore store) => Results.Ok(store.Snapshot()));

app.MapPost("/api/review-state/known", async (DomainRequest request, ReviewStore store) =>
{
    if (!DomainRules.TryNormalize(request.Domain, out var domain))
    {
        return Results.BadRequest(new { error = "Enter a valid domain name." });
    }

    await store.MarkKnownAsync(domain);
    return Results.Ok(store.Snapshot());
});

app.MapPost("/api/review-state/known/bulk", async (DomainsRequest request, ReviewStore store) =>
{
    var domains = request.Domains
        .Where(domain => DomainRules.TryNormalize(domain, out _))
        .Select(DomainRules.Normalize)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    await store.MarkKnownAsync(domains);
    return Results.Ok(store.Snapshot());
});

app.MapDelete("/api/review-state/known/{domain}", async (string domain, ReviewStore store) =>
{
    if (!DomainRules.TryNormalize(domain, out var normalized))
    {
        return Results.BadRequest(new { error = "Enter a valid domain name." });
    }

    await store.UnmarkKnownAsync(normalized);
    return Results.Ok(store.Snapshot());
});

app.MapPost("/api/review-state/investigations", async (InvestigationRequest request, ReviewStore store) =>
{
    if (!DomainRules.TryNormalize(request.Domain, out var domain))
    {
        return Results.BadRequest(new { error = "Enter a valid domain name." });
    }

    var notes = request.Notes?.Trim();
    if (string.IsNullOrWhiteSpace(notes))
    {
        return Results.BadRequest(new { error = "Add a note before marking a domain for investigation." });
    }
    if (notes.Length > 2000)
    {
        return Results.BadRequest(new { error = "Investigation notes must be 2,000 characters or fewer." });
    }

    await store.SaveInvestigationAsync(domain, notes);
    return Results.Ok(store.Snapshot());
});

app.MapDelete("/api/review-state/investigations/{domain}", async (string domain, ReviewStore store) =>
{
    if (!DomainRules.TryNormalize(domain, out var normalized))
    {
        return Results.BadRequest(new { error = "Enter a valid domain name." });
    }

    await store.RemoveInvestigationAsync(normalized);
    return Results.Ok(store.Snapshot());
});

app.MapPost("/api/review-state/blocked", async (DomainRequest request, ReviewStore store) =>
{
    if (!DomainRules.TryNormalize(request.Domain, out var domain))
    {
        return Results.BadRequest(new { error = "Enter a valid domain name." });
    }

    await store.AddBlockedAsync(domain);
    return Results.Ok(store.Snapshot());
});

app.MapDelete("/api/review-state/blocked/{domain}", async (string domain, ReviewStore store) =>
{
    if (!DomainRules.TryNormalize(domain, out var normalized))
    {
        return Results.BadRequest(new { error = "Enter a valid domain name." });
    }

    await store.RemoveBlockedAsync(normalized);
    return Results.Ok(store.Snapshot());
});

app.MapPost("/api/config", async (ReviewConfigRequest request, ConfigStore store) =>
{
    await store.UpdateConfigAsync(request);
    return Results.Ok(store.Snapshot());
});

app.MapPost("/api/pihole/connect", async (
    ConnectRequest request,
    IHttpClientFactory clients,
    PiholeSessionStore sessions,
    ConfigStore configStore,
    CancellationToken cancellationToken) =>
{
    if (!PiholeUrl.TryNormalize(request.Url, out var baseUrl))
    {
        return Results.BadRequest(new { error = "Enter a valid Pi-hole URL, such as http://pi.hole." });
    }

    try
    {
        var client = clients.CreateClient("pihole");
        if (string.IsNullOrWhiteSpace(request.Password))
        {
            using var probe = await client.GetAsync($"{baseUrl}/api/info/version", cancellationToken);
            if (!probe.IsSuccessStatusCode)
            {
                return Results.BadRequest(new { error = "Pi-hole could not be verified without a password." });
            }

            var unauthenticatedConnectionId = sessions.Add(baseUrl, null, null);
            await configStore.SaveConnectionUrlAsync(baseUrl);
            return Results.Ok(new { connectionId = unauthenticatedConnectionId, url = baseUrl });
        }

        var authPayload = new Dictionary<string, object?> { ["password"] = request.Password };
        if (!string.IsNullOrWhiteSpace(request.Totp))
        {
            authPayload["totp"] = request.Totp;
        }

        using var response = await client.PostAsJsonAsync($"{baseUrl}/api/auth", authPayload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var json = ParseJson(body);
        var sid = json?["session"]?["sid"]?.GetValue<string>() ?? json?["sid"]?.GetValue<string>();
        var csrf = json?["session"]?["csrf"]?.GetValue<string>() ?? json?["csrf"]?.GetValue<string>();

        if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(sid))
        {
            return Results.BadRequest(new { error = ExtractApiError(json, body, "Pi-hole rejected the connection.") });
        }

        var connectionId = sessions.Add(baseUrl, sid, csrf);
        await configStore.SaveConnectionUrlAsync(baseUrl);
        return Results.Ok(new { connectionId, url = baseUrl });
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
    {
        return Results.BadRequest(new { error = "Pi-hole could not be reached. Check the URL and network access." });
    }
});

app.MapPost("/api/pihole/disconnect", (ConnectionRequest request, PiholeSessionStore sessions) =>
{
    if (string.IsNullOrWhiteSpace(request.ConnectionId))
    {
        return Results.Ok();
    }

    sessions.Remove(request.ConnectionId);
    return Results.Ok();
});

app.MapGet("/api/pihole/domains", async (
    HttpRequest request,
    IHttpClientFactory clients,
    PiholeSessionStore sessions,
    CancellationToken cancellationToken) =>
{
    if (!TryGetSession(request, sessions, out var session))
    {
        return Results.Unauthorized();
    }

    var query = request.Query;
    var from = ParseUnix(query["from"]);
    var until = ParseUnix(query["until"]);
    var disk = !string.Equals(query["disk"], "false", StringComparison.OrdinalIgnoreCase);

    try
    {
        var result = await FetchAndAggregateAsync(
            clients.CreateClient("pihole"),
            session,
            from,
            until,
            disk,
            cancellationToken);

        return Results.Ok(result);
    }
    catch (PiholeApiException exception)
    {
        if (exception.StatusCode is 401 or 403)
        {
            sessions.Remove(request.Headers["X-Connection-Id"]!);
            return Results.Unauthorized();
        }

        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
    {
        return Results.BadRequest(new { error = "Pi-hole could not be reached while loading the query log." });
    }
});

app.MapGet("/api/pihole/block-list", async (
    HttpRequest request,
    IHttpClientFactory clients,
    PiholeSessionStore sessions,
    CancellationToken cancellationToken) =>
{
    if (!TryGetSession(request, sessions, out var session))
    {
        return Results.Unauthorized();
    }

    try
    {
        var domains = await FetchExactDenyListAsync(clients.CreateClient("pihole"), session, cancellationToken);
        return Results.Ok(new { domains });
    }
    catch (PiholeApiException exception)
    {
        if (exception.StatusCode is 401 or 403)
        {
            sessions.Remove(request.Headers["X-Connection-Id"]!);
            return Results.Unauthorized();
        }

        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
    {
        return Results.BadRequest(new { error = "Pi-hole could not be reached while reading its block list." });
    }
});

app.MapPost("/api/pihole/block-list/sync", async (
    HttpRequest request,
    IHttpClientFactory clients,
    PiholeSessionStore sessions,
    ReviewStore store,
    CancellationToken cancellationToken) =>
{
    if (!TryGetSession(request, sessions, out var session))
    {
        return Results.Unauthorized();
    }

    var localDomains = (store.Snapshot().BlockedDomains ?? Array.Empty<string>())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    try
    {
        var client = clients.CreateClient("pihole");
        var existing = (await FetchExactDenyListAsync(client, session, cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var alreadyPresent = localDomains.Where(existing.Contains).ToArray();
        var pending = localDomains.Where(domain => !existing.Contains(domain)).ToArray();
        var added = new List<string>();
        var errors = new List<object>();

        if (pending.Length > 0)
        {
            var blockedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
            using var message = new HttpRequestMessage(HttpMethod.Post, $"{session.BaseUrl}/api/domains/deny/exact");
            AddSessionHeaders(message, session);
            message.Content = JsonContent.Create(new
            {
                domain = pending,
                groups = new[] { 0 },
                enabled = true,
                comment = $"Blocked by Domain Review app on {blockedAt}"
            });

            using var response = await client.SendAsync(message, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var json = ParseJson(body);
            if (!response.IsSuccessStatusCode)
            {
                throw new PiholeApiException((int)response.StatusCode, ExtractApiError(json, body, "Pi-hole rejected the block-list update."));
            }

            var processed = json?["processed"] as JsonObject;
            if (processed?["success"] is JsonArray successes)
            {
                added.AddRange(successes
                    .Select(item => item?["item"]?.GetValue<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!));
            }
            if (processed?["errors"] is JsonArray failures)
            {
                errors.AddRange(failures
                    .Select(item => new
                    {
                        domain = item?["item"]?.GetValue<string>() ?? "unknown",
                        error = item?["error"]?.GetValue<string>() ?? "Pi-hole could not add this domain."
                    }));
            }
        }

        await store.MarkPiholeSyncedAsync(added.ToArray());
        var savedState = store.Snapshot();
        return Results.Ok(new
        {
            requested = localDomains.Length,
            alreadyPresent,
            added,
            errors,
            syncedDomains = savedState.PiholeSyncedDomains
        });
    }
    catch (PiholeApiException exception)
    {
        if (exception.StatusCode is 401 or 403)
        {
            sessions.Remove(request.Headers["X-Connection-Id"]!);
            return Results.Unauthorized();
        }

        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
    {
        return Results.BadRequest(new { error = "Pi-hole could not be reached while updating its block list." });
    }
});

app.MapFallbackToFile("index.html");

app.Run();

static bool TryGetSession(HttpRequest request, PiholeSessionStore sessions, out PiholeSession session)
{
    var connectionId = request.Headers["X-Connection-Id"].FirstOrDefault();
    return sessions.TryGet(connectionId, out session!);
}

static async Task<DomainImportResult> FetchAndAggregateAsync(
    HttpClient client,
    PiholeSession session,
    long? from,
    long? until,
    bool disk,
    CancellationToken cancellationToken)
{
    var domains = new Dictionary<string, DomainAggregate>(StringComparer.OrdinalIgnoreCase);
    string? cursor = null;
    var pages = 0;
    var recordsRead = 0;
    var truncated = false;
    const int pageLength = 1000;
    const int maxPages = 100;

    while (pages < maxPages)
    {
        var parameters = new List<string> { $"length={pageLength}" };
        if (from is not null) parameters.Add($"from={from.Value}");
        if (until is not null) parameters.Add($"until={until.Value}");
        if (disk) parameters.Add("disk=true");
        if (!string.IsNullOrWhiteSpace(cursor)) parameters.Add($"cursor={Uri.EscapeDataString(cursor)}");

        using var message = new HttpRequestMessage(HttpMethod.Get, $"{session.BaseUrl}/api/queries?{string.Join('&', parameters)}");
        AddSessionHeaders(message, session);
        using var response = await client.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var json = ParseJson(body);

        if (!response.IsSuccessStatusCode)
        {
            throw new PiholeApiException((int)response.StatusCode, ExtractApiError(json, body, "Pi-hole rejected the query request."));
        }

        var queries = json?["queries"]?.AsArray();
        if (queries is null || queries.Count == 0)
        {
            break;
        }

        pages++;
        foreach (var query in queries)
        {
            if (query is null) continue;

            var domain = query["domain"]?.GetValue<string>();
            if (!DomainRules.TryNormalize(domain, out var normalized)) continue;

            var status = query["status"]?.GetValue<string>() ?? "UNKNOWN";
            var timestamp = query["time"]?.GetValue<double>() ?? 0;
            var clientName = query["client"]?["name"]?.GetValue<string>();
            var clientIp = query["client"]?["ip"]?.GetValue<string>();
            var clientLabel = string.IsNullOrWhiteSpace(clientName) ? clientIp : clientName;

            if (!domains.TryGetValue(normalized, out var aggregate))
            {
                aggregate = new DomainAggregate(normalized);
                domains[normalized] = aggregate;
            }

            aggregate.Add(status, timestamp, clientLabel, clientIp);
            recordsRead++;
        }

        var nextCursor = json?["cursor"]?.ToString();
        if (string.IsNullOrWhiteSpace(nextCursor) || string.Equals(nextCursor, cursor, StringComparison.Ordinal))
        {
            break;
        }

        cursor = nextCursor;
    }

    if (pages >= maxPages && !string.IsNullOrWhiteSpace(cursor))
    {
        truncated = true;
    }

    return new DomainImportResult(
        "pihole",
        DateTimeOffset.UtcNow,
        pages,
        recordsRead,
        domains.Values
            .OrderByDescending(item => item.QueryCount)
            .ThenBy(item => item.Domain, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.ToResult())
            .ToArray(),
        truncated);
}

static async Task<IReadOnlyCollection<string>> FetchExactDenyListAsync(
    HttpClient client,
    PiholeSession session,
    CancellationToken cancellationToken)
{
    using var message = new HttpRequestMessage(HttpMethod.Get, $"{session.BaseUrl}/api/domains/deny/exact");
    AddSessionHeaders(message, session);
    using var response = await client.SendAsync(message, cancellationToken);
    var body = await response.Content.ReadAsStringAsync(cancellationToken);
    var json = ParseJson(body);
    if (!response.IsSuccessStatusCode)
    {
        throw new PiholeApiException((int)response.StatusCode, ExtractApiError(json, body, "Pi-hole rejected the block-list request."));
    }

    if (json?["domains"] is not JsonArray domains)
    {
        return Array.Empty<string>();
    }

    return domains
        .Select(item => item?["domain"]?.GetValue<string>())
        .Where(value => DomainRules.TryNormalize(value, out _))
        .Select(value => DomainRules.Normalize(value!))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static JsonObject? ParseJson(string body)
{
    try
    {
        return JsonNode.Parse(body)?.AsObject();
    }
    catch (JsonException)
    {
        return null;
    }
}

static string ExtractApiError(JsonObject? json, string body, string fallback)
{
    return json?["error"]?["message"]?.GetValue<string>()
        ?? json?["error"]?.GetValue<string>()
        ?? json?["message"]?.GetValue<string>()
        ?? (string.IsNullOrWhiteSpace(body) ? fallback : fallback);
}

static long? ParseUnix(string? value)
{
    return long.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
}

static void AddSessionHeaders(HttpRequestMessage message, PiholeSession session)
{
    if (!string.IsNullOrWhiteSpace(session.Sid))
    {
        message.Headers.Add("X-FTL-SID", session.Sid);
    }
    if (!string.IsNullOrWhiteSpace(session.Csrf))
    {
        message.Headers.Add("X-FTL-CSRF", session.Csrf);
    }
}

public sealed record DomainRequest(string Domain);
public sealed record InvestigationRequest(string Domain, string? Notes);
public sealed record DomainsRequest(IReadOnlyCollection<string> Domains);
public sealed record ConnectRequest(string Url, string Password, string? Totp);
public sealed record ConnectionRequest(string? ConnectionId);
public sealed record PiholeSession(string BaseUrl, string? Sid, string? Csrf);
public sealed record DomainImportResult(
    string Source,
    DateTimeOffset ImportedAt,
    int Pages,
    int RecordsRead,
    IReadOnlyCollection<DomainResult> Domains,
    bool Truncated);
public sealed record DomainResult(
    string Domain,
    int QueryCount,
    int BlockedCount,
    DateTimeOffset? FirstSeen,
    DateTimeOffset? LastSeen,
    IReadOnlyCollection<string> Clients,
    string PrimaryStatus,
    IReadOnlyCollection<string> ClientIps);

public sealed class DomainAggregate(string domain)
{
    private readonly HashSet<string> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _clientIps = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _firstSeen;
    private DateTimeOffset? _lastSeen;
    private string _primaryStatus = "UNKNOWN";

    public string Domain { get; } = domain;
    public int QueryCount { get; private set; }
    public int BlockedCount { get; private set; }

    public void Add(string status, double timestamp, string? client, string? clientIp)
    {
        QueryCount++;
        if (status.Contains("BLOCK", StringComparison.OrdinalIgnoreCase)
            || status is "GRAVITY" or "REGEX" or "DENYLIST")
        {
            BlockedCount++;
        }

        if (!string.IsNullOrWhiteSpace(client)) _clients.Add(client);
        if (!string.IsNullOrWhiteSpace(clientIp)) _clientIps.Add(clientIp);
        if (timestamp <= 0) return;

        var seen = DateTimeOffset.FromUnixTimeSeconds((long)timestamp);
        if (_firstSeen is null || seen < _firstSeen) _firstSeen = seen;
        if (_lastSeen is null || seen > _lastSeen) _lastSeen = seen;

        if (_primaryStatus == "UNKNOWN" || status is "GRAVITY" or "DENYLIST" or "REGEX")
        {
            _primaryStatus = status;
        }
    }

    public DomainResult ToResult() => new(
        Domain,
        QueryCount,
        BlockedCount,
        _firstSeen,
        _lastSeen,
        _clients.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
        _primaryStatus,
        _clientIps.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
}

public sealed class ReviewStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Investigation> _investigations = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _piholeSynced = new(StringComparer.OrdinalIgnoreCase);

    public ReviewStore(IHostEnvironment environment)
    {
        _path = Path.Combine(AppPaths.GetDataDirectory(environment), "review-state.json");
        Load();
    }

    public ReviewState Snapshot()
    {
        lock (_gate)
        {
            return new ReviewState(
                _known.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                _blocked.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                _piholeSynced.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                _investigations.Values.OrderBy(value => value.Domain, StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }

    public Task MarkKnownAsync(params string[] domains)
    {
        lock (_gate)
        {
            foreach (var domain in domains) _known.Add(domain);
            Save();
        }

        return Task.CompletedTask;
    }

    public Task UnmarkKnownAsync(string domain)
    {
        lock (_gate)
        {
            _known.Remove(domain);
            Save();
        }

        return Task.CompletedTask;
    }

    public Task AddBlockedAsync(string domain)
    {
        lock (_gate)
        {
            _blocked.Add(domain);
            Save();
        }

        return Task.CompletedTask;
    }

    public Task RemoveBlockedAsync(string domain)
    {
        lock (_gate)
        {
            _blocked.Remove(domain);
            Save();
        }

        return Task.CompletedTask;
    }

    public Task SaveInvestigationAsync(string domain, string notes)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var createdAt = _investigations.TryGetValue(domain, out var existing)
                ? existing.CreatedAt
                : now;
            _investigations[domain] = new Investigation(domain, notes, createdAt, now);
            Save();
        }

        return Task.CompletedTask;
    }

    public Task RemoveInvestigationAsync(string domain)
    {
        lock (_gate)
        {
            _investigations.Remove(domain);
            Save();
        }

        return Task.CompletedTask;
    }

    public Task MarkPiholeSyncedAsync(params string[] domains)
    {
        lock (_gate)
        {
            foreach (var domain in domains)
            {
                if (DomainRules.TryNormalize(domain, out var normalized)) _piholeSynced.Add(normalized);
            }
            Save();
        }

        return Task.CompletedTask;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var state = JsonSerializer.Deserialize<PersistedReviewState>(File.ReadAllText(_path));
            if (state is null) return;

            foreach (var domain in (state.KnownDomains ?? Array.Empty<string>()).Where(value => DomainRules.TryNormalize(value, out _)).Select(DomainRules.Normalize))
            {
                _known.Add(domain);
            }

            foreach (var domain in (state.BlockedDomains ?? Array.Empty<string>()).Where(value => DomainRules.TryNormalize(value, out _)).Select(DomainRules.Normalize))
            {
                _blocked.Add(domain);
            }

            foreach (var investigation in state.Investigations ?? Array.Empty<Investigation>())
            {
                if (!DomainRules.TryNormalize(investigation.Domain, out var domain) || string.IsNullOrWhiteSpace(investigation.Notes)) continue;
                var notes = investigation.Notes.Trim();
                if (notes.Length > 2000) notes = notes[..2000];
                _investigations[domain] = investigation with { Domain = domain, Notes = notes };
            }

            foreach (var domain in (state.PiholeSyncedDomains ?? Array.Empty<string>()).Where(value => DomainRules.TryNormalize(value, out _)).Select(DomainRules.Normalize))
            {
                _piholeSynced.Add(domain);
            }

            // Rewrite the old combined file after the separate ConfigStore has migrated its config.
            if (state.Config is not null || state.CachedImport is not null)
            {
                Save();
            }
        }
        catch (IOException)
        {
            // A missing or temporarily unreadable local state file should not prevent the app from starting.
        }
        catch (JsonException)
        {
            // Keep a safe empty state if a manually edited file is malformed.
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.Serialize(
            new ReviewState(
                _known.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                _blocked.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                _piholeSynced.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                _investigations.Values.OrderBy(value => value.Domain, StringComparer.OrdinalIgnoreCase).ToArray()),
            new JsonSerializerOptions { WriteIndented = true });
        var temporaryPath = $"{_path}.tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _path, true);
    }

}

public sealed class ConfigStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _legacyReviewStatePath;
    private ReviewConfig _config = ReviewConfig.Default;

    public ConfigStore(IHostEnvironment environment)
    {
        var dataPath = AppPaths.GetDataDirectory(environment);
        _path = Path.Combine(dataPath, "config.json");
        _legacyReviewStatePath = Path.Combine(dataPath, "review-state.json");
        Load();
    }

    public ReviewConfig Snapshot()
    {
        lock (_gate)
        {
            return _config;
        }
    }

    public Task UpdateConfigAsync(ReviewConfigRequest request)
    {
        lock (_gate)
        {
            _config = NormalizeConfig(request, _config);
            Save();
        }

        return Task.CompletedTask;
    }

    public Task SaveConnectionUrlAsync(string baseUrl)
    {
        lock (_gate)
        {
            _config = _config with { PiholeUrl = baseUrl };
            Save();
        }

        return Task.CompletedTask;
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var saved = JsonSerializer.Deserialize<ReviewConfig>(File.ReadAllText(_path));
                if (saved is not null)
                {
                    _config = NormalizeConfig(saved, ReviewConfig.Default);
                    return;
                }
            }

            // Migrate the configuration once from the old combined review-state file.
            if (File.Exists(_legacyReviewStatePath))
            {
                var legacy = JsonSerializer.Deserialize<PersistedReviewState>(File.ReadAllText(_legacyReviewStatePath));
                if (legacy?.Config is not null)
                {
                    _config = NormalizeConfig(legacy.Config, ReviewConfig.Default);
                    Save();
                    return;
                }
            }

            Save();
        }
        catch (IOException)
        {
            // A missing or temporarily unreadable config file should not prevent the app from starting.
        }
        catch (JsonException)
        {
            // Keep safe defaults if a manually edited config file is malformed.
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
        var temporaryPath = $"{_path}.tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _path, true);
    }

    private static ReviewConfig NormalizeConfig(ReviewConfig? config, ReviewConfig fallback)
    {
        if (config is null) return fallback;
        return NormalizeConfig(
            new ReviewConfigRequest(config.PiholeUrl, config.QueryRange, config.IncludeDisk, config.HidePiholeBlocked, config.Filter, config.Sort, config.IpFilter),
            fallback);
    }

    private static ReviewConfig NormalizeConfig(ReviewConfigRequest request, ReviewConfig fallback)
    {
        var url = fallback.PiholeUrl;
        if (string.IsNullOrWhiteSpace(request.PiholeUrl))
        {
            url = null;
        }
        else if (PiholeUrl.TryNormalize(request.PiholeUrl, out var normalizedUrl))
        {
            url = normalizedUrl;
        }

        var range = request.QueryRange is "86400" or "604800" or "2592000" or "all"
            ? request.QueryRange
            : fallback.QueryRange;
        var filter = request.Filter is "needs-review" or "all" or "known" or "investigate" or "blocked"
            ? request.Filter
            : fallback.Filter;
        var sort = request.Sort is "queries" or "recent" or "alpha"
            ? request.Sort
            : fallback.Sort;
        var ipFilter = string.IsNullOrWhiteSpace(request.IpFilter)
            ? fallback.IpFilter
            : request.IpFilter.Trim();
        return new ReviewConfig(url, range!, request.IncludeDisk ?? fallback.IncludeDisk, request.HidePiholeBlocked ?? fallback.HidePiholeBlocked, filter!, sort!, ipFilter!);
    }
}

public static class AppPaths
{
    public static string GetDataDirectory(IHostEnvironment environment)
    {
        var configuredPath = Environment.GetEnvironmentVariable("PIHOLE_REVIEW_DATA_DIR");
        return string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, "data")
            : Path.GetFullPath(configuredPath);
    }
}

public sealed record ReviewState(
    IReadOnlyCollection<string>? KnownDomains,
    IReadOnlyCollection<string>? BlockedDomains,
    IReadOnlyCollection<string>? PiholeSyncedDomains,
    IReadOnlyCollection<Investigation>? Investigations);

public sealed record PersistedReviewState(
    IReadOnlyCollection<string>? KnownDomains,
    IReadOnlyCollection<string>? BlockedDomains,
    IReadOnlyCollection<string>? PiholeSyncedDomains,
    IReadOnlyCollection<Investigation>? Investigations,
    ReviewConfig? Config = null,
    DomainImportResult? CachedImport = null);
public sealed record Investigation(string Domain, string Notes, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record ReviewConfig(
    string? PiholeUrl,
    string QueryRange,
    bool IncludeDisk,
    bool HidePiholeBlocked,
    string Filter,
    string Sort,
    string IpFilter)
{
    public static ReviewConfig Default => new(null, "2592000", true, true, "needs-review", "queries", "all");
}
public sealed record ReviewConfigRequest(
    string? PiholeUrl,
    string? QueryRange,
    bool? IncludeDisk,
    bool? HidePiholeBlocked,
    string? Filter,
    string? Sort,
    string? IpFilter);

public sealed class PiholeSessionStore
{
    private readonly ConcurrentDictionary<string, PiholeSession> _sessions = new();

    public string Add(string baseUrl, string? sid, string? csrf)
    {
        var id = Guid.NewGuid().ToString("N");
        _sessions[id] = new PiholeSession(baseUrl, sid, csrf);
        return id;
    }

    public bool TryGet(string? id, out PiholeSession session)
    {
        session = default!;
        return !string.IsNullOrWhiteSpace(id) && _sessions.TryGetValue(id, out session!);
    }

    public void Remove(string id) => _sessions.TryRemove(id, out _);
}

public static class DomainRules
{
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = Normalize(value ?? string.Empty);
        if (normalized.Length < 3 || normalized.Length > 253 || normalized.StartsWith('.') || normalized.EndsWith('.'))
        {
            normalized = string.Empty;
            return false;
        }

        var labels = normalized.Split('.');
        return labels.Length >= 2
            && labels.All(label => label.Length is >= 1 and <= 63
                && label[0] != '-'
                && label[^1] != '-'
                && label.All(character => char.IsLetterOrDigit(character) || character is '-' or '_'));
    }

    public static string Normalize(string value) => value.Trim().TrimEnd('.').ToLowerInvariant();
}

public static class PiholeUrl
{
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/admin", StringComparison.OrdinalIgnoreCase)) path = path[..^6];
        if (path.EndsWith("/api", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        normalized = new UriBuilder(uri) { Path = path, Query = string.Empty, Fragment = string.Empty }
            .Uri
            .ToString()
            .TrimEnd('/');
        return true;
    }
}

public sealed class PiholeApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
