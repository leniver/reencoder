# Reencoder (C# / WinForms)

A Windows GUI port of the bash re-encode script. Pick a folder, **Scan** to list
every `.mp4` that still needs encoding, review/uncheck as needed, then **Start**.
Each file is re-encoded to HEVC/x265 with ffmpeg and replaces the original in
place once the output is validated.

## Download
Grab the latest self-contained Windows build — no .NET install required:

**[Download the latest release](https://github.com/leniver/reencoder/releases/latest)**

1. Download `Reencoder.exe` from the [latest release](https://github.com/leniver/reencoder/releases/latest).
2. (Optional) Verify the download against `Reencoder.exe.sha256`:
   ```powershell
   $expected = (Get-Content Reencoder.exe.sha256).Split()[0]
   (Get-FileHash Reencoder.exe -Algorithm SHA256).Hash -eq $expected
   # -> True
   ```
3. Run `Reencoder.exe`. You still need `ffmpeg.exe` and `ffprobe.exe` on `PATH`
   (or browse to them in the UI) — see [Requirements](#requirements).

Prefer to build from source instead? See [Build & run](#build--run).

## Workflow
1. Choose a **Folder** (or a single **File**).
2. Click **Scan** — the list fills with every `.mp4` that needs encoding
   (not a temp/backup name, not already tracked, old enough). Each row shows a
   checkbox and a status column.
3. Uncheck anything you want to skip (or use Check all / Uncheck all).
4. Click **Start**. Status updates per file (Encoding → OK / Skipped / Error),
   with a per-file progress bar and an overall files-done bar.

## Encoding
Video is encoded with **`libx265` — the software (CPU) HEVC encoder** — driven by
`-crf` (quality), `-preset` (speed/efficiency), and `-threads`. Audio is copied
unchanged (`-c:a copy`). There is **no GPU/hardware acceleration** (no NVENC, QSV,
or AMF): the tool's goal is to shrink an archive in place, and CPU x265 gives the
best quality-per-byte for that. The trade-off is speed — encoding is CPU-bound and
runs at idle priority in the background, so it's slow by design but stays out of
your way.

## Tracking file
Encoded files are recorded in a single flat text file — one absolute path per
line — set in **Tracking file** (defaults to `reencoded.list` in the chosen
folder). The "does this need encoding?" check is a membership lookup against that
list. On success, the file's path is appended.

To force a re-encode, remove its line from the tracking file (or delete the file
entirely to start fresh).

## Behavior carried over from the script
- Single running instance only (named `Mutex`, like `flock`).
- Recursive `.mp4` scan.
- Skips files younger than the configured age (default 30 min).
- Validates input and output with `ffprobe` before replacing.
- **No backup by default** — the original is overwritten by the re-encoded file.
  Tick **Keep original** to instead write the HEVC output beside it as
  `<name>_hevc.mp4` and leave the source untouched (both are then recorded in
  the tracking file so neither is re-encoded).
- Same ffmpeg arguments (plus `-progress pipe:1 -nostats` for the progress bar).
- Optional timestamped log file, plus the live log in the window.

## Settings
The window's settings (last folder/file, tracking file, ffmpeg/ffprobe paths,
CRF, preset, min age, threads, log options) are saved to
`%AppData%\Reencoder\settings.json` on close and restored on the next
launch, so the last selected folder stays.

## Requirements
- .NET 8 SDK (and Visual Studio 2022 17.8+ for the IDE).
- `ffmpeg.exe` and `ffprobe.exe` on `PATH` (leave the fields as
  `ffmpeg` / `ffprobe`) or browse to the exact `.exe` paths. Their status
  (✓ detected / ✗ not found) is shown next to each field at launch and whenever
  the path changes; the path is verified to actually be ffmpeg/ffprobe via
  `-version`.
  Builds: https://www.gyan.dev/ffmpeg/builds/ or https://ffmpeg.org

## Build & run
```
dotnet build
dotnet run
```

Self-contained single exe:
```
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```
