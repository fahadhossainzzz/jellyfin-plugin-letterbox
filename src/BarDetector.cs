using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Letterbox.Configuration;

namespace Jellyfin.Plugin.Letterbox;

/// <summary>Result of measuring one file.</summary>
public class ScanResult
{
    public string Path { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public long FileModifiedTicks { get; set; }
    public DateTime ScannedUtc { get; set; }
    public string Verdict { get; set; } = string.Empty; // Bars, NoBars, Variable, Failed
    public double? Ratio { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int PictureTop { get; set; }
    public int PictureBottom { get; set; }
    public int Samples { get; set; }
    public int BrightSamples { get; set; }
    public int OutlierSamples { get; set; }
    public double Seconds { get; set; }
    public string? Note { get; set; }

    /// <summary>The tag to put on the item, or null for none.</summary>
    public string? Tag => Verdict == "Bars" && Ratio.HasValue
        ? "bars:" + Ratio.Value.ToString("0.00", CultureInfo.InvariantCulture)
        : null;
}

/// <summary>
/// Samples frames across a video with ffmpeg and finds where the real picture starts and ends.
/// Read-only: only ever reads the media file.
/// </summary>
public class BarDetector
{
    private const int Columns = 192; // horizontal downscale; averages noise, keeps every row
    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly PluginConfiguration _config;

    public BarDetector(string ffmpegPath, string ffprobePath, PluginConfiguration config)
    {
        _ffmpeg = ffmpegPath;
        _ffprobe = ffprobePath;
        _config = config;
    }

    private sealed record Sample(double Time, double Mean, int Top, int Bottom);

    public async Task<ScanResult> ScanAsync(string path, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var info = new FileInfo(path);
        var result = new ScanResult
        {
            Path = path,
            FileSize = info.Exists ? info.Length : 0,
            FileModifiedTicks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0,
            ScannedUtc = DateTime.UtcNow,
        };

        var (width, height, duration) = await ProbeAsync(path, ct).ConfigureAwait(false);
        result.Width = width;
        result.Height = height;
        if (width <= 0 || height <= 0 || duration <= 0)
        {
            result.Verdict = "Failed";
            result.Note = "ffprobe could not read size/duration";
            return result;
        }

        var interval = Math.Max(5, _config.SampleIntervalSeconds);
        var times = new List<double>();
        // Skip the first and last minute (logos, credits); short videos still get samples
        double start = Math.Min(60, duration * 0.05), end = duration - Math.Min(60, duration * 0.05);
        for (var t = start; t < end; t += interval)
        {
            times.Add(t);
        }

        var samples = new List<Sample>();
        using var gate = new SemaphoreSlim(Math.Clamp(_config.Workers, 1, 8));
        var tasks = times.Select(async t =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var frame = await GrabAsync(path, t, height, ct).ConfigureAwait(false);
                return frame is null ? null : Measure(t, frame, height);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        foreach (var s in await Task.WhenAll(tasks).ConfigureAwait(false))
        {
            if (s is not null)
            {
                samples.Add(s);
            }
        }

        result.Samples = samples.Count;
        var bright = samples.Where(s => s.Mean >= _config.DarkFrameThreshold).ToList();
        result.BrightSamples = bright.Count;
        result.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
        if (bright.Count < 10)
        {
            result.Verdict = "Failed";
            result.Note = $"only {bright.Count} usable frames";
            return result;
        }

        var modeTop = bright.GroupBy(s => s.Top).OrderByDescending(g => g.Count()).First().Key;
        var modeBottom = bright.GroupBy(s => s.Bottom).OrderByDescending(g => g.Count()).First().Key;
        // Frames whose picture clearly reaches past the usual edges: aspect changes (IMAX), logos or subtitles in the bar
        var beyond = bright.Where(s => s.Top < modeTop - 6 || s.Bottom > modeBottom + 6).ToList();
        var cluster = bright.Except(beyond).ToList();
        // Largest picture in the normal cluster: never cut picture to remove a sliver of bar
        var top = cluster.Min(s => s.Top);
        var bottom = cluster.Max(s => s.Bottom);
        result.PictureTop = top;
        result.PictureBottom = bottom;
        result.OutlierSamples = beyond.Count;

        var bars = top + (height - 1 - bottom);
        var active = bottom - top + 1;
        if (bars < _config.MinimumBarPixels)
        {
            result.Verdict = "NoBars";
        }
        else if (beyond.Count > Math.Max(2, 0.02 * bright.Count))
        {
            result.Verdict = "Variable";
            result.Note = "picture size changes during the video; not tagged";
        }
        else
        {
            result.Verdict = "Bars";
            result.Ratio = Math.Round((double)width / active, 2);
        }

        return result;
    }

    private Sample? Measure(double t, byte[] frame, int height)
    {
        long sum = 0;
        int top = -1, bottom = -1;
        for (var r = 0; r < height; r++)
        {
            int max = 0;
            var offset = r * Columns;
            for (var c = 0; c < Columns; c++)
            {
                int v = frame[offset + c];
                sum += v;
                if (v > max)
                {
                    max = v;
                }
            }

            if (max > _config.PictureThreshold)
            {
                if (top < 0)
                {
                    top = r;
                }

                bottom = r;
            }
        }

        return top < 0 ? null : new Sample(t, (double)sum / frame.Length, top, bottom);
    }

    private async Task<byte[]?> GrabAsync(string path, double t, int height, CancellationToken ct)
    {
        var expected = Columns * height;
        if (_config.HardwareDecoding)
        {
            var hw = await RunAsync(_ffmpeg, FrameArgs(path, t, height, true), ct).ConfigureAwait(false);
            if (hw is not null && hw.Length == expected)
            {
                return hw;
            }
        }

        var sw = await RunAsync(_ffmpeg, FrameArgs(path, t, height, false), ct).ConfigureAwait(false);
        return sw is not null && sw.Length == expected ? sw : null;
    }

    private static List<string> FrameArgs(string path, double t, int height, bool hw)
    {
        var args = new List<string> { "-v", "error", "-nostdin" };
        if (hw)
        {
            args.AddRange(["-hwaccel", "videotoolbox"]);
        }

        args.AddRange(
        [
            "-ss", t.ToString("0.00", CultureInfo.InvariantCulture),
            "-i", path,
            "-frames:v", "1", "-an", "-sn", "-dn",
            "-vf", $"scale={Columns}:{height}:flags=area,format=gray",
            "-f", "rawvideo", "-",
        ]);
        return args;
    }

    private async Task<(int Width, int Height, double Duration)> ProbeAsync(string path, CancellationToken ct)
    {
        var json = await RunAsync(
            _ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height:format=duration", "-of", "json", path],
            ct).ConfigureAwait(false);
        if (json is null)
        {
            return (0, 0, 0);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var stream = doc.RootElement.GetProperty("streams")[0];
            var w = stream.GetProperty("width").GetInt32();
            var h = stream.GetProperty("height").GetInt32();
            var d = double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString() ?? "0", CultureInfo.InvariantCulture);
            return (w, h, d);
        }
        catch (Exception)
        {
            return (0, 0, 0);
        }
    }

    private static async Task<byte[]?> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start())
            {
                return null;
            }

            try
            {
                proc.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception)
            {
                // not fatal
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            using var ms = new MemoryStream();
            var errTask = proc.StandardError.ReadToEndAsync(timeout.Token);
            await proc.StandardOutput.BaseStream.CopyToAsync(ms, timeout.Token).ConfigureAwait(false);
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await errTask.ConfigureAwait(false);
            return proc.ExitCode == 0 ? ms.ToArray() : null;
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(true);
            }
            catch (Exception)
            {
                // already gone
            }

            ct.ThrowIfCancellationRequested();
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
