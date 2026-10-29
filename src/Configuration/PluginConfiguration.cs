using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Letterbox.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Master switch. When off, the scheduled task does nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Measure and report only; never write tags.</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Optional test filter: only files whose path contains one of these (one per line).</summary>
    public string OnlyPathsContaining { get; set; } = string.Empty;

    /// <summary>Seconds between sampled frames.</summary>
    public int SampleIntervalSeconds { get; set; } = 30;

    /// <summary>Frames decoded in parallel.</summary>
    public int Workers { get; set; } = 2;

    /// <summary>Use VideoToolbox (Apple) hardware decoding, falling back to software.</summary>
    public bool HardwareDecoding { get; set; } = true;

    /// <summary>Luma (0-255) above which a row counts as picture.</summary>
    public int PictureThreshold { get; set; } = 20;

    /// <summary>Frames darker than this average luma are ignored.</summary>
    public int DarkFrameThreshold { get; set; } = 18;

    /// <summary>Minimum total bar height (pixels at source resolution) before a file counts as having bars.</summary>
    public int MinimumBarPixels { get; set; } = 16;

    /// <summary>Optional folder to also write the results report (results.json) to.</summary>
    public string ReportFolder { get; set; } = string.Empty;

    /// <summary>Re-measure every file on the next run.</summary>
    public bool ForceRescan { get; set; } = false;

    /// <summary>Written by the task: summary of the last run, shown on the settings page.</summary>
    public string LastRunSummary { get; set; } = string.Empty;

    /// <summary>Written by the task: one line per measured file, shown on the settings page.</summary>
    public string LastReport { get; set; } = string.Empty;
}
