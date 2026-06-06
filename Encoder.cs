using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Reencoder;

public sealed class EncodeOptions
{
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public int Crf { get; set; } = 28;
    public string Preset { get; set; } = "medium";
    public int MinAgeMinutes { get; set; } = 30;
    public int Threads { get; set; } = 2;

    /// <summary>
    /// When true, the source file is left untouched and the HEVC output is
    /// written alongside it as "&lt;name&gt;_hevc.mp4". When false (default), the
    /// original is overwritten in place.
    /// </summary>
    public bool KeepOriginal { get; set; }

    public string? LogFilePath { get; set; }
}

public sealed class ProgressUpdate
{
    public string? Message { get; init; }
    public int? FilesDone { get; init; }
    public int? FilesTotal { get; init; }
    public string? CurrentFile { get; init; }
    public double? FilePercent { get; init; }

    // Per-file status for the list view: "Encoding", "OK", "Skipped",
    // "Too recent", "Invalid", "Error", "Cancelled".
    public string? FileResultPath { get; init; }
    public string? FileResultStatus { get; init; }
}

/// <summary>Result of scanning a directory for work.</summary>
public sealed class ScanResult
{
    public List<string> ToEncode { get; } = new();
    public int AlreadyEncoded { get; set; }
    public int TooRecent { get; set; }
    public int TotalMp4 { get; set; }
}

public sealed class Encoder
{
    private static readonly object LogFileLock = new();
    private string? _lastFfmpegError;

    /// <summary>
    /// Lists every .mp4 under <paramref name="dir"/> that still needs encoding:
    /// not already in the tracker, and old enough.
    /// Pure file-system work (no ffprobe), so it stays fast on large trees.
    /// </summary>
    public static ScanResult Scan(string dir, EncodeOptions opt, EncodedTracker tracker)
    {
        var r = new ScanResult();
        foreach (var f in EnumerateMp4Safe(dir))
        {
            r.TotalMp4++;

            if (tracker.Contains(f)) { r.AlreadyEncoded++; continue; }
            if (!IsOldEnough(f, opt.MinAgeMinutes)) { r.TooRecent++; continue; }

            r.ToEncode.Add(f);
        }
        r.ToEncode.Sort(StringComparer.OrdinalIgnoreCase);
        return r;
    }

    /// <summary>
    /// Verifies an executable is really the expected tool by running it with
    /// "-version" and confirming the banner identifies it (e.g. ffmpeg's banner
    /// starts with "ffmpeg version ..."). Guards against the path pointing at
    /// some other program. Returns true only if the process started, exited 0,
    /// and its output names <paramref name="expectedName"/>. Handles "not found
    /// on PATH" (Win32Exception) and bad paths gracefully.
    /// </summary>
    public static async Task<bool> IsExecutableAvailableAsync(string exePath, string expectedName)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;

        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-version");

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            // Drain both streams so the child can't block on a full pipe buffer.
            string stdout = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            string stderr = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);
            if (p.ExitCode != 0) return false;

            // ffmpeg/ffprobe print "<name> version ..." as the first banner line.
            string banner = (stdout + stderr).TrimStart();
            return banner.StartsWith(expectedName + " version", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task EncodeListAsync(IReadOnlyList<string> files, EncodeOptions opt,
        EncodedTracker tracker, IProgress<ProgressUpdate> progress, CancellationToken ct)
    {
        Log(progress, opt, "==================================================");
        Log(progress, opt, "Start");

        int total = files.Count;
        int done = 0;
        progress.Report(new ProgressUpdate { FilesTotal = total, FilesDone = 0 });

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            await EncodeFileAsync(f, opt, tracker, progress, ct).ConfigureAwait(false);
            done++;
            progress.Report(new ProgressUpdate { FilesDone = done, FilesTotal = total });
        }

        Log(progress, opt, "End");
    }

    private async Task EncodeFileAsync(string file, EncodeOptions opt, EncodedTracker tracker,
        IProgress<ProgressUpdate> progress, CancellationToken ct)
    {
        string tmp = Path.ChangeExtension(file, null) + ".tmp.mp4";

        if (!File.Exists(file))
        {
            Status(progress, file, "Skipped");
            Log(progress, opt, $"SKIP missing file: {file}");
            return;
        }

        if (tracker.Contains(file))
        {
            Status(progress, file, "Skipped");
            Log(progress, opt, $"SKIP already encoded: {file}");
            return;
        }

        if (!IsOldEnough(file, opt.MinAgeMinutes))
        {
            Status(progress, file, "Too recent");
            Log(progress, opt, $"SKIP too recent: {file}");
            return;
        }

        progress.Report(new ProgressUpdate
        {
            CurrentFile = file,
            FilePercent = 0,
            FileResultPath = file,
            FileResultStatus = "Encoding",
        });

        if (!await IsValidMp4Async(file, opt, ct).ConfigureAwait(false))
        {
            Status(progress, file, "Invalid");
            Log(progress, opt, $"ERROR invalid mp4: {file}");
            return;
        }

        TryDelete(tmp);
        Log(progress, opt, $"Encoding: {file}");

        double duration = await GetDurationAsync(file, opt, ct).ConfigureAwait(false);

        int code;
        try
        {
            code = await RunFfmpegAsync(file, tmp, opt, duration, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tmp);
            Status(progress, file, "Cancelled");
            Log(progress, opt, $"CANCELLED: {file}");
            throw;
        }

        if (code != 0)
        {
            TryDelete(tmp);
            Status(progress, file, "Error");
            Log(progress, opt, $"ERROR ffmpeg failed: {file}");
            if (!string.IsNullOrWhiteSpace(_lastFfmpegError))
                Log(progress, opt, "ffmpeg: " + _lastFfmpegError!.Trim());
            return;
        }

        var tmpInfo = new FileInfo(tmp);
        if (!tmpInfo.Exists || tmpInfo.Length == 0)
        {
            Status(progress, file, "Error");
            Log(progress, opt, $"ERROR output empty: {tmp}");
            TryDelete(tmp);
            return;
        }

        if (!await IsValidMp4Async(tmp, opt, ct).ConfigureAwait(false))
        {
            Status(progress, file, "Error");
            Log(progress, opt, $"ERROR output invalid: {tmp}");
            TryDelete(tmp);
            return;
        }

        try
        {
            string dest = opt.KeepOriginal ? KeptOutputPath(file) : file;

            // Either replace the original in place, or write a separate _hevc file
            // and leave the source untouched, depending on KeepOriginal.
            File.Move(tmp, dest, overwrite: true);

            // Mark the source as done so it isn't picked up again. When keeping
            // the original, also mark the new output so it isn't re-encoded.
            tracker.Add(file);
            if (opt.KeepOriginal)
                tracker.Add(dest);

            Status(progress, file, "OK");
            Log(progress, opt, opt.KeepOriginal ? $"OK (kept original): {dest}" : $"OK: {file}");
            progress.Report(new ProgressUpdate { FilePercent = 100 });
        }
        catch (Exception ex)
        {
            Status(progress, file, "Error");
            Log(progress, opt, $"ERROR writing output: {file} ({ex.Message})");
            TryDelete(tmp);
        }
    }

    private async Task<int> RunFfmpegAsync(string file, string tmp, EncodeOptions opt,
        double durationSeconds, IProgress<ProgressUpdate> progress, CancellationToken ct)
    {
        _lastFfmpegError = null;

        var psi = new ProcessStartInfo(opt.FfmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var a in new[]
        {
            "-nostdin", "-hide_banner", "-y",
            "-threads", opt.Threads.ToString(CultureInfo.InvariantCulture),
            "-i", file,
            "-map", "0:v:0",
            "-map", "0:a?",
            "-c:v", "libx265",
            "-crf", opt.Crf.ToString(CultureInfo.InvariantCulture),
            "-preset", opt.Preset,
            "-tag:v", "hvc1",
            "-c:a", "copy",
            "-movflags", "+faststart",
            "-progress", "pipe:1",
            "-nostats",
            tmp,
        })
        {
            psi.ArgumentList.Add(a);
        }

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderr = new StringBuilder();

        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                lock (stderr) { stderr.AppendLine(e.Data); }
        };

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            string line = e.Data;

            // ffmpeg -progress emits "out_time=HH:MM:SS.microseconds".
            if (line.StartsWith("out_time=", StringComparison.Ordinal) && durationSeconds > 0)
            {
                string ts = line.Substring("out_time=".Length).Trim();
                if (TimeSpan.TryParse(ts, CultureInfo.InvariantCulture, out var t))
                {
                    double pct = t.TotalSeconds / durationSeconds * 100.0;
                    progress.Report(new ProgressUpdate { FilePercent = Clamp(pct) });
                }
            }
            else if (line.Equals("progress=end", StringComparison.Ordinal))
            {
                progress.Report(new ProgressUpdate { FilePercent = 100 });
            }
        };

        try
        {
            proc.Start();

            // Idle CPU priority (equivalent to nice -n 19). Windows has no direct,
            // per-child equivalent of ionice -c3; idle CPU priority is the closest.
            try { proc.PriorityClass = ProcessPriorityClass.Idle; } catch { }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using (ct.Register(() =>
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }))
            {
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            }

            int code = proc.ExitCode;
            if (code != 0)
                lock (stderr) { _lastFfmpegError = stderr.ToString(); }
            return code;
        }
        finally
        {
            proc.Dispose();
        }
    }

    private async Task<bool> IsValidMp4Async(string file, EncodeOptions opt, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(opt.FfprobePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var a in new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=codec_name,width,height",
            "-of", "csv=p=0",
            file,
        })
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var p = Process.Start(psi)!;
            string stdout = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            _ = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return p.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<double> GetDurationAsync(string file, EncodeOptions opt, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(opt.FfprobePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var a in new[]
        {
            "-v", "error",
            "-show_entries", "format=duration",
            "-of", "default=nw=1:nk=1",
            file,
        })
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var p = Process.Start(psi)!;
            string outp = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            _ = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            if (double.TryParse(outp.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return d;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // fall through
        }

        return 0;
    }

    private static IEnumerable<string> EnumerateMp4Safe(string root)
    {
        // Manual walk that skips folders we can't read instead of aborting.
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string cur = stack.Pop();

            string[] subdirs = Array.Empty<string>();
            try { subdirs = Directory.GetDirectories(cur); } catch { }
            foreach (var d in subdirs) stack.Push(d);

            string[] files = Array.Empty<string>();
            try { files = Directory.GetFiles(cur, "*.mp4"); } catch { }
            foreach (var f in files)
            {
                // Guard against the legacy 8.3 wildcard quirk (*.mp4 matching *.mp4x).
                if (string.Equals(Path.GetExtension(f), ".mp4", StringComparison.OrdinalIgnoreCase))
                    yield return f;
            }
        }
    }

    /// <summary>Output path used when keeping the original: "&lt;name&gt;_hevc.mp4".</summary>
    private static string KeptOutputPath(string file)
    {
        string dir = Path.GetDirectoryName(file) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(file);
        string ext = Path.GetExtension(file);
        return Path.Combine(dir, stem + "_hevc" + ext);
    }

    private static bool IsOldEnough(string file, int minutes)
    {
        try
        {
            return DateTime.Now - File.GetLastWriteTime(file) > TimeSpan.FromMinutes(minutes);
        }
        catch
        {
            return false;
        }
    }

    private static double Clamp(double v) => v < 0 ? 0 : (v > 100 ? 100 : v);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void Status(IProgress<ProgressUpdate> progress, string file, string status)
        => progress.Report(new ProgressUpdate { FileResultPath = file, FileResultStatus = status });

    private static void Log(IProgress<ProgressUpdate> progress, EncodeOptions opt, string text)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {text}";
        progress.Report(new ProgressUpdate { Message = line });

        if (!string.IsNullOrEmpty(opt.LogFilePath))
        {
            try
            {
                lock (LogFileLock)
                {
                    File.AppendAllText(opt.LogFilePath!, line + Environment.NewLine);
                }
            }
            catch
            {
                // best-effort logging
            }
        }
    }
}
