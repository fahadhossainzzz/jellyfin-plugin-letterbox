using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Letterbox;

/// <summary>Measures library items and keeps their "bars:" tags in sync with the measurements.</summary>
public class LetterboxService
{
    public const string TagPrefix = "bars:";
    private static readonly SemaphoreSlim RunLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ILibraryManager _library;
    private readonly IMediaEncoder _encoder;
    private readonly ILogger _logger;

    public LetterboxService(ILibraryManager library, IMediaEncoder encoder, ILogger logger)
    {
        _library = library;
        _encoder = encoder;
        _logger = logger;
    }

    public async Task RunAsync(bool scanNewFiles, IProgress<double> progress, CancellationToken ct)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        var config = plugin.Configuration;
        if (!config.Enabled)
        {
            _logger.LogInformation("Letterbox is disabled");
            return;
        }

        if (!await RunLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Letterbox is already running");
            return;
        }

        try
        {
            var results = LoadResults(plugin.DataFolderPath);
            var filters = config.OnlyPathsContaining
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
            var items = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                IsVirtualItem = false,
                Recursive = true,
            })
                .Where(i => !string.IsNullOrEmpty(i.Path) && File.Exists(i.Path))
                .Where(i => filters.Length == 0 || filters.Any(f => i.Path.Contains(f, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(i => i.Path, StringComparer.Ordinal)
                .ToList();

            var dryRun = config.DryRun;
            var detector = new BarDetector(_encoder.EncoderPath, _encoder.ProbePath, config);
            int scanned = 0, tagged = 0, untagged = 0, done = 0;
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var path = item.Path;
                var info = new FileInfo(path);
                results.TryGetValue(path, out var existing);
                var stale = existing is null
                    || existing.FileSize != info.Length
                    || existing.FileModifiedTicks != info.LastWriteTimeUtc.Ticks
                    || existing.Verdict == "Failed"
                    || config.ForceRescan;

                if (stale && scanNewFiles)
                {
                    _logger.LogInformation("Letterbox measuring {Path}", path);
                    existing = await detector.ScanAsync(path, ct).ConfigureAwait(false);
                    results[path] = existing;
                    scanned++;
                    SaveResults(plugin.DataFolderPath, config.ReportFolder, results);
                    _logger.LogInformation(
                        "Letterbox {Verdict} {Ratio} ({Seconds}s, {Bright}/{Samples} frames) {Path}",
                        existing.Verdict,
                        existing.Ratio,
                        existing.Seconds,
                        existing.BrightSamples,
                        existing.Samples,
                        path);
                }

                if (existing is not null && (!stale || scanNewFiles))
                {
                    var change = await ApplyTagAsync(item, existing.Tag, dryRun, ct).ConfigureAwait(false);
                    if (change > 0)
                    {
                        tagged++;
                    }
                    else if (change < 0)
                    {
                        untagged++;
                    }
                }

                done++;
                progress.Report(100.0 * done / Math.Max(1, items.Count));
            }

            // Settings may have been saved from the dashboard while this ran: update the current copy
            var forced = config.ForceRescan;
            config = plugin.Configuration;
            if (forced && scanNewFiles)
            {
                config.ForceRescan = false;
            }

            var known = items.Select(i => i.Path).Where(results.ContainsKey).Select(p => results[p]).ToList();
            config.LastRunSummary = string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm} UTC: {1} items, {2} measured this run, {3} with bars, {4} without, {5} variable (not tagged), {6} failed. Tags {7}: +{8} / -{9}.",
                DateTime.UtcNow,
                items.Count,
                scanned,
                known.Count(r => r.Verdict == "Bars"),
                known.Count(r => r.Verdict == "NoBars"),
                known.Count(r => r.Verdict == "Variable"),
                known.Count(r => r.Verdict == "Failed"),
                dryRun ? "NOT written (dry run)" : "written",
                tagged,
                untagged);
            config.LastReport = BuildReport(known);
            plugin.SaveConfiguration();
            _logger.LogInformation("Letterbox: {Summary}", config.LastRunSummary);
        }
        finally
        {
            RunLock.Release();
        }
    }

    /// <summary>Returns +1 if a tag was added/changed, -1 if removed, 0 if nothing changed.</summary>
    private async Task<int> ApplyTagAsync(BaseItem item, string? desired, bool dryRun, CancellationToken ct)
    {
        var tags = item.Tags ?? [];
        var current = tags.FirstOrDefault(t => t.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(current, desired, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (dryRun)
        {
            return desired is null ? -1 : 1;
        }

        var updated = tags.Where(t => !t.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (desired is not null)
        {
            updated.Add(desired);
        }

        item.Tags = updated.ToArray();
        await _library.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
        return desired is null ? -1 : 1;
    }

    private static string BuildReport(IEnumerable<ScanResult> results)
    {
        var sb = new StringBuilder();
        foreach (var r in results.OrderBy(r => r.Verdict == "Bars" ? 1 : 0).ThenBy(r => r.Path, StringComparer.Ordinal))
        {
            sb.Append(r.Verdict)
                .Append(" | ")
                .Append(r.Tag ?? "-")
                .Append(" | ")
                .Append(r.Width).Append('x').Append(r.Height)
                .Append(" picture rows ").Append(r.PictureTop).Append('-').Append(r.PictureBottom)
                .Append(" | ").Append(r.BrightSamples).Append('/').Append(r.Samples).Append(" frames")
                .Append(", ").Append(r.OutlierSamples).Append(" outliers")
                .Append(", ").Append(r.Seconds.ToString("0.0", CultureInfo.InvariantCulture)).Append("s | ")
                .Append(System.IO.Path.GetFileName(r.Path));
            if (!string.IsNullOrEmpty(r.Note))
            {
                sb.Append(" (").Append(r.Note).Append(')');
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static Dictionary<string, ScanResult> LoadResults(string folder)
    {
        try
        {
            var file = System.IO.Path.Combine(folder, "results.json");
            if (File.Exists(file))
            {
                var list = JsonSerializer.Deserialize<List<ScanResult>>(File.ReadAllText(file)) ?? [];
                return list.GroupBy(r => r.Path).ToDictionary(g => g.Key, g => g.Last());
            }
        }
        catch (Exception)
        {
            // start fresh
        }

        return new Dictionary<string, ScanResult>();
    }

    private void SaveResults(string folder, string reportFolder, Dictionary<string, ScanResult> results)
    {
        var json = JsonSerializer.Serialize(results.Values.OrderBy(r => r.Path, StringComparer.Ordinal).ToList(), JsonOptions);
        foreach (var dir in new[] { folder, reportFolder }.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            try
            {
                Directory.CreateDirectory(dir);
                var file = System.IO.Path.Combine(dir, "results.json");
                File.WriteAllText(file + ".tmp", json);
                File.Move(file + ".tmp", file, true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Letterbox could not write results to {Dir}", dir);
            }
        }
    }
}
