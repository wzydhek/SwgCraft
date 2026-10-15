using System.Globalization;
using System.Net;
using System.Text.Json;
using SwgCraft.Core;

namespace SwgCraft.Data;

public enum SyncState { Skipped, NotModified, Failed, Aborted, Completed }

public sealed record SyncOutcome(SyncState State, string Summary, SyncResult? Result, IReadOnlyList<string> Messages);

/// <summary>Downloads (or accepts) a Galaxy Harvester current-resources XML export and applies it to the database.</summary>
public sealed class FeedSyncService
{
    internal sealed record FeedMeta(string? ETag, string? LastModified);

    readonly ISpawnStore _store;
    readonly HttpClient _http;
    readonly Func<AppSettings> _settings;

    public FeedSyncService(ISpawnStore store, HttpClient http, Func<AppSettings> settings)
    {
        _store = store; _http = http; _settings = settings;
    }

    public async Task<SyncOutcome> SyncAsync(int galaxyId, ResourceTree tree, bool force, bool allowLargeDrop,
                                             CancellationToken ct = default)
    {
        var settings = _settings();
        var scoped = _store.ForGalaxy(galaxyId);
        var now = DateTimeOffset.UtcNow;

        if (!force)
        {
            var last = scoped.LastSuccessfulSync();
            if (last != null)
            {
                var wait = TimeSpan.FromMinutes(settings.MinSyncIntervalMinutes) - (now - last.Value);
                if (wait > TimeSpan.Zero)
                    return new(SyncState.Skipped,
                        $"Last successful sync was {(now - last.Value).TotalMinutes:0} min ago; the next one is allowed in {Math.Ceiling(wait.TotalMinutes):0} min. Use Force Sync to override.",
                        null, Array.Empty<string>());
            }
        }

        var url = settings.FeedUrlTemplate.Replace("{galaxyId}", galaxyId.ToString(CultureInfo.InvariantCulture));
        string xml;
        FeedMeta? newMeta;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var meta = force ? null : LoadMeta(galaxyId);
            if (meta?.ETag != null) req.Headers.TryAddWithoutValidation("If-None-Match", meta.ETag);
            if (meta?.LastModified != null &&
                DateTimeOffset.TryParse(meta.LastModified, CultureInfo.InvariantCulture, DateTimeStyles.None, out var lm))
                req.Headers.IfModifiedSince = lm;

            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.NotModified)
                return new(SyncState.NotModified, "The feed has not changed since the last download.", null, Array.Empty<string>());
            if (!resp.IsSuccessStatusCode)
            {
                var msg = $"Download failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} ({url})";
                scoped.LogSync("failed", msg);
                return new(SyncState.Failed, msg, null, Array.Empty<string>());
            }
            xml = await resp.Content.ReadAsStringAsync(ct);
            newMeta = new FeedMeta(resp.Headers.ETag?.ToString(),
                                   resp.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var msg = "Download failed: " + ex.Message;
            scoped.LogSync("failed", msg);
            return new(SyncState.Failed, msg, null, Array.Empty<string>());
        }

        SaveCache(galaxyId, xml, now);
        var outcome = await Task.Run(() => ApplyXml(galaxyId, xml, tree, now, allowLargeDrop), ct);
        if (outcome.State == SyncState.Completed) SaveMeta(galaxyId, newMeta);
        return outcome;
    }

    /// <summary>Applies feed XML that is already in hand (used by downloads, file import and tests).</summary>
    public SyncOutcome ApplyXml(int galaxyId, string xml, ResourceTree tree, DateTimeOffset now, bool allowLargeDrop)
    {
        var scoped = _store.ForGalaxy(galaxyId);
        var parsed = GhFeedParser.Parse(xml, galaxyId);
        if (parsed.FatalError != null)
        {
            scoped.LogSync("failed", parsed.FatalError);
            return new(SyncState.Failed, parsed.FatalError, null, parsed.Messages);
        }

        if (parsed.GalaxyName.Length > 0) _store.UpsertGalaxy(galaxyId, parsed.GalaxyName);
        else if (_store.Galaxies().All(g => g.Id != galaxyId)) _store.UpsertGalaxy(galaxyId, $"Galaxy {galaxyId}");

        var settings = _settings();
        var result = scoped.ApplyFeed(parsed.Spawns, parsed.RejectedNames, tree, now,
                                      new SyncOptions(settings.MinFeedPercentOfPrevious, allowLargeDrop));
        var messages = parsed.Messages.Concat(result.Messages).ToList();

        return result.Status switch
        {
            "ok" => new(SyncState.Completed,
                $"Sync complete: {result.Added} added, {result.Updated} updated, {result.Reopened} reopened, " +
                $"{result.Replaced} replaced, {result.Despawned} despawned, {result.Unmapped} unmapped, " +
                $"{result.Rejected} rejected, {result.OutOfRange} range warnings.", result, messages),
            "aborted" => new(SyncState.Aborted, result.Messages.FirstOrDefault() ?? "Sync aborted.", result, messages),
            _ => new(SyncState.Failed, result.Messages.FirstOrDefault() ?? "Sync failed.", result, messages),
        };
    }

    // ------------------------------------------------------------ cache + conditional GET metadata

    static string MetaPath(int galaxyId) => Path.Combine(AppPaths.FeedCacheFolder, $"current{galaxyId}.meta.json");

    static FeedMeta? LoadMeta(int galaxyId)
    {
        try { return File.Exists(MetaPath(galaxyId)) ? JsonSerializer.Deserialize<FeedMeta>(File.ReadAllText(MetaPath(galaxyId))) : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    static void SaveMeta(int galaxyId, FeedMeta? meta)
    {
        if (meta == null) return;
        try
        {
            Directory.CreateDirectory(AppPaths.FeedCacheFolder);
            File.WriteAllText(MetaPath(galaxyId), JsonSerializer.Serialize(meta));
        }
        catch (IOException) { }
    }

    /// <summary>Keeps the last 10 raw downloads per galaxy for debugging and replay.</summary>
    static void SaveCache(int galaxyId, string xml, DateTimeOffset now)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.FeedCacheFolder);
            File.WriteAllText(Path.Combine(AppPaths.FeedCacheFolder, $"current{galaxyId}-{now:yyyyMMdd-HHmmss}.xml"), xml);
            foreach (var old in Directory.GetFiles(AppPaths.FeedCacheFolder, $"current{galaxyId}-*.xml")
                                         .OrderByDescending(f => f).Skip(10))
                File.Delete(old);
        }
        catch (IOException) { }
    }
}
