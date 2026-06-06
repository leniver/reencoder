using System;
using System.IO;
using System.Text.Json;

namespace Reencoder;

/// <summary>
/// User-editable settings persisted between runs as JSON in
/// %AppData%\Reencoder\settings.json. Load/Save are best-effort: a
/// missing or unreadable file just yields defaults rather than crashing.
/// </summary>
public sealed class AppSettings
{
    public string Target { get; set; } = "";
    public string TrackFile { get; set; } = "";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public int Crf { get; set; } = 28;
    public string Preset { get; set; } = "medium";
    public int MinAgeMinutes { get; set; } = 30;
    public int Threads { get; set; } = 2;
    public bool KeepOriginal { get; set; }
    public bool WriteLog { get; set; } = true;
    public string LogPath { get; set; } = "";

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Reencoder", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            string path = SettingsPath;
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (s != null) return s;
            }
        }
        catch
        {
            // Fall back to defaults on any read/parse failure.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best-effort persistence
        }
    }
}
