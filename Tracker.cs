using System;
using System.Collections.Generic;
using System.IO;

namespace Reencoder;

/// <summary>
/// Tracks already-encoded files in one flat text file (one absolute path per
/// line). Replaces the per-file ".enc" markers from the bash script.
/// Membership ("grep") is case-insensitive and based on the full path.
/// </summary>
public sealed class EncodedTracker
{
    private readonly HashSet<string> _set = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public string FilePath { get; }
    public int Count => _set.Count;

    public EncodedTracker(string trackFilePath)
    {
        FilePath = trackFilePath;
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var t = line.Trim();
                if (t.Length > 0)
                    _set.Add(Normalize(t));
            }
        }
        catch
        {
            // If the list is unreadable, start empty rather than crashing.
        }
    }

    public bool Contains(string file) => _set.Contains(Normalize(file));

    public void Add(string file)
    {
        string key = Normalize(file);
        lock (_lock)
        {
            if (!_set.Add(key)) return;
            try
            {
                File.AppendAllText(FilePath, file + Environment.NewLine);
            }
            catch
            {
                // best-effort persistence
            }
        }
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
