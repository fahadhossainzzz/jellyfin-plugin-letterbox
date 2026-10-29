using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Letterbox;

/// <summary>Scheduled task: measures new/changed files and keeps tags in sync. Shows under Dashboard → Scheduled Tasks.</summary>
public class DetectBarsTask : IScheduledTask
{
    private readonly LetterboxService _service;

    public DetectBarsTask(ILibraryManager library, IMediaEncoder encoder, ILogger<DetectBarsTask> logger)
    {
        _service = new LetterboxService(library, encoder, logger);
    }

    public string Name => "Detect baked-in black bars";

    public string Key => "LetterboxDetectBars";

    public string Description => "Measures black bars baked into videos (read-only) and tags items bars:<ratio> for Wholphin NoBars.";

    public string Category => "Letterbox";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _service.RunAsync(true, progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Nightly safety net; new content is also picked up right after each library scan (see LibraryScanHook)
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
        };
    }
}

/// <summary>After every library scan: queue the measuring task so new files get handled without waiting for the night.</summary>
public class LibraryScanHook : ILibraryPostScanTask
{
    private readonly ITaskManager _taskManager;

    public LibraryScanHook(ITaskManager taskManager)
    {
        _taskManager = taskManager;
    }

    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.Enabled == true)
        {
            _taskManager.QueueScheduledTask<DetectBarsTask>();
        }

        progress.Report(100);
        return Task.CompletedTask;
    }
}
