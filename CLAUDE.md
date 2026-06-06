# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Windows GUI (WinForms, .NET 8) port of a bash re-encode script. It batch
re-encodes `.mp4` files to HEVC/x265 with ffmpeg and **overwrites each original
in place** once the output is validated. Targeted at MotionEye camera recordings.

## Commands

```
dotnet build
dotnet run
```

Self-contained single exe:
```
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

There is no test project and no linter configured. Requires `ffmpeg.exe` and
`ffprobe.exe` on `PATH` (or browse to explicit paths in the UI).

## Architecture

Five source files, all in namespace `Reencoder` (assembly/project: `Reencoder`):

- **Program.cs** — entry point. Enforces a single running instance via a named
  `Global\` Mutex (the equivalent of `flock` in the original script).
- **MainForm.cs** — the entire UI, built in code (no designer file). Collects
  settings into an `EncodeOptions`, runs scan/encode on background threads
  (`Task.Run`), and marshals updates back via `IProgress<ProgressUpdate>`.
- **Encoder.cs** — all the work. Contains `EncodeOptions`, `ProgressUpdate`,
  `ScanResult`, and the `Encoder` class.
- **Tracker.cs** — `EncodedTracker`, the encoded-files persistence layer.
- **AppSettings.cs** — `AppSettings`, the UI-settings persistence layer
  (separate from the tracker). JSON at
  `%AppData%\Reencoder\settings.json`; loaded in the `MainForm`
  constructor, saved in `OnFormClosing`. Both Load/Save swallow errors and fall
  back to defaults.

### The scan → encode flow

1. `Encoder.Scan` (static, pure file-system, no ffprobe so it's fast) walks the
   tree and returns files that: aren't excluded names, aren't in the tracker,
   and are old enough. `MainForm.OnScan` calls this and fills the `ListView`.
2. `Encoder.EncodeListAsync` iterates the checked files **sequentially**, one at
   a time (the `-threads` option controls ffmpeg's internal threads, not
   parallel files). Per file, `EncodeFileAsync`:
   re-checks all skip conditions → validates input with ffprobe →
   gets duration (for the progress %) → runs ffmpeg to a `.tmp.mp4` →
   validates output → `File.Move(..., overwrite: true)` → `tracker.Add`.

### Tracking (replaces per-file `.enc` markers)

`EncodedTracker` is a flat text file, one absolute path per line. Membership is a
case-insensitive `HashSet<string>` lookup on the full normalized path. On
success the path is appended. To force a re-encode, remove its line. Default
location: `reencoded.list` in the chosen folder. **Source of truth for "already
done"** — there is no other state.

### Conventions that matter

- **All ffmpeg/ffprobe args are passed via `ProcessStartInfo.ArgumentList`**
  (not a joined string) — preserve this when changing the command line. The
  HEVC encode args are hard-coded in `RunFfmpegAsync` (libx265, `-tag:v hvc1`,
  `-c:a copy`, `+faststart`, `-progress pipe:1 -nostats`).
- Progress comes from parsing ffmpeg's `out_time=` lines off stdout against the
  probed duration; `progress=end` means 100%.
- **No backups by default** — a failed/invalid output deletes the `.tmp.mp4` and
  leaves the original untouched, but a successful encode overwrites the source in
  place. The `KeepOriginal` option changes the destination to `<name>_hevc.mp4`
  (via `KeptOutputPath`) and leaves the source intact; in that mode both the
  source and the new output are added to the tracker so neither is re-encoded.
- ffmpeg child runs at `ProcessPriorityClass.Idle` (≈ `nice -n 19`); there is no
  clean Windows equivalent of `ionice`, so disk throttling is weaker than Linux.
- `Encoder.IsExecutableAvailableAsync(path, expectedName)` validates a tool by
  running `-version` and confirming the banner starts with `"<name> version"` —
  so a wrong/other exe is rejected, not just a missing one. `MainForm` calls it
  at launch, on path change/browse, and as a hard gate before Start.
- Cancellation is a `CancellationToken` that kills the ffmpeg process tree.
- Nullable reference types are enabled; `ImplicitUsings` is **disabled** (every
  file lists its `using`s explicitly).
- File-system and process error handling is deliberately swallow-and-continue
  (e.g. unreadable subfolders are skipped, not fatal).
