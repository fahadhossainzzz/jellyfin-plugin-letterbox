# Letterbox for Jellyfin

Detects black bars baked into video files and tags each item `bars:<ratio>` (e.g. `bars:2.40`).
[Wholphin NoBars](https://github.com/fahadhossainzzz/Wholphin/tree/nobars)'s **Full screen (auto)** scale reads the tag and
zooms past the bars without stretching. Media files are only read, never modified.

- Runs as **Dashboard → Scheduled Tasks → Letterbox → Detect baked-in black bars**: after every library scan and nightly at 03:00.
- Samples one frame every 30 s (configurable) with Jellyfin's own ffmpeg (VideoToolbox hardware decoding when available).
- Uses the largest consistent picture area across bright frames, so it never cuts picture to remove a sliver of bar.
- Files whose picture size changes (IMAX scenes, logos or subtitles in the bars) are reported as `Variable` and not tagged.
- Starts in **dry run** mode: check the report on the plugin's settings page, then turn dry run off.

## Install

Dashboard → Plugins → Manage Repositories → **New Repository**:

```
https://github.com/fahadhossainzzz/jellyfin-plugin-letterbox/releases/download/repo/manifest.json
```

Then install **Letterbox** from the catalog and restart Jellyfin.

Built against Jellyfin 12.2 (`lib/` holds the server's reference assemblies, used for compiling only).
