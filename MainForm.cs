using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Reencoder;

public sealed class MainForm : Form
{
    private readonly TextBox _txtTarget = new() { Dock = DockStyle.Fill };
    private readonly TextBox _txtFfmpeg = new() { Dock = DockStyle.Fill, Text = "ffmpeg" };
    private readonly TextBox _txtFfprobe = new() { Dock = DockStyle.Fill, Text = "ffprobe" };
    private readonly Label _lblFfmpegStatus = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 7, 3, 3) };
    private readonly Label _lblFfprobeStatus = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 7, 3, 3) };
    private readonly TextBox _txtTrackFile = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _numCrf = new() { Minimum = 0, Maximum = 51, Value = 28, Width = 60 };
    private readonly ComboBox _cmbPreset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly NumericUpDown _numMinAge = new() { Minimum = 0, Maximum = 1_000_000, Value = 30, Width = 80 };
    private readonly NumericUpDown _numThreads = new() { Minimum = 0, Maximum = 256, Value = 2, Width = 60 };
    private readonly CheckBox _chkKeepOriginal = new() { Text = "Keep original", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly CheckBox _chkLog = new() { Text = "Write log file", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly TextBox _txtLogPath = new()
    {
        Dock = DockStyle.Fill,
        Text = Path.Combine(Path.GetTempPath(), "reencoded.log"),
    };

    private readonly Button _btnScan = new() { Text = "Scan", AutoSize = true };
    private readonly Button _btnCheckAll = new() { Text = "Check all", AutoSize = true };
    private readonly Button _btnUncheckAll = new() { Text = "Uncheck all", AutoSize = true };
    private readonly Label _lblCount = new() { Text = "Not scanned", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 8, 3, 3) };

    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        HideSelection = false,
        GridLines = true,
    };
    private ColumnHeader _colFile = null!;
    private ColumnHeader _colStatus = null!;

    private readonly Button _btnStart = new() { Text = "Start", Width = 100, Height = 34, Enabled = false };
    private readonly Button _btnStop = new() { Text = "Stop", Width = 100, Height = 34, Enabled = false };

    private readonly Label _lblCurrent = new()
    {
        Text = "Idle",
        AutoEllipsis = true,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
    };
    private readonly Label _lblOverall = new() { Text = "0 / 0", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ProgressBar _progFile = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
    private readonly ProgressBar _progOverall = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };

    private readonly TextBox _txtLog = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 9f),
        BackColor = Color.White,
    };

    private readonly Encoder _encoder = new();
    private readonly Dictionary<string, ListViewItem> _itemByPath = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private int _total;

    public MainForm()
    {
        Text = "Reencoder — HEVC / x265";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(820, 720);
        MinimumSize = new Size(680, 580);

        foreach (var p in new[] { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" })
            _cmbPreset.Items.Add(p);
        _cmbPreset.SelectedItem = "medium";

        BuildUi();

        _btnScan.Click += OnScan;
        _btnStart.Click += OnStart;
        _btnStop.Click += OnStop;
        _btnCheckAll.Click += (_, _) => SetAllChecked(true);
        _btnUncheckAll.Click += (_, _) => SetAllChecked(false);
        _chkLog.CheckedChanged += (_, _) => _txtLogPath.Enabled = _chkLog.Checked;
        _chkLog.Checked = true;
        _list.SizeChanged += (_, _) => ResizeColumns();

        // Re-detect when a path is edited by hand (the Browse buttons detect too).
        _txtFfmpeg.Leave += async (_, _) => await DetectToolsAsync();
        _txtFfprobe.Leave += async (_, _) => await DetectToolsAsync();

        LoadSettings();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await DetectToolsAsync();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(BuildSettings(), 0, 0);
        root.Controls.Add(BuildCenter(), 0, 1);
        root.Controls.Add(BuildBottom(), 0, 2);

        Controls.Add(root);
    }

    private Control BuildSettings()
    {
        var g = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var btnFolder = new Button { Text = "Folder…", AutoSize = true };
        var btnFile = new Button { Text = "File…", AutoSize = true };
        btnFolder.Click += (_, _) => PickFolder();
        btnFile.Click += (_, _) => PickFile();
        g.Controls.Add(MakeLabel("Folder or file:"), 0, 0);
        g.Controls.Add(_txtTarget, 1, 0);
        g.Controls.Add(btnFolder, 2, 0);
        g.Controls.Add(btnFile, 3, 0);

        var btnTrack = new Button { Text = "Browse…", AutoSize = true };
        btnTrack.Click += (_, _) => PickTrackFile();
        g.Controls.Add(MakeLabel("Tracking file:"), 0, 1);
        g.Controls.Add(_txtTrackFile, 1, 1);
        g.Controls.Add(btnTrack, 2, 1);
        g.SetColumnSpan(btnTrack, 2);

        var btnFf = new Button { Text = "Browse…", AutoSize = true };
        btnFf.Click += async (_, _) => { if (PickExe(_txtFfmpeg)) await DetectToolsAsync(); };
        g.Controls.Add(MakeLabel("ffmpeg path:"), 0, 2);
        g.Controls.Add(_txtFfmpeg, 1, 2);
        g.Controls.Add(btnFf, 2, 2);
        g.Controls.Add(_lblFfmpegStatus, 3, 2);

        var btnFp = new Button { Text = "Browse…", AutoSize = true };
        btnFp.Click += async (_, _) => { if (PickExe(_txtFfprobe)) await DetectToolsAsync(); };
        g.Controls.Add(MakeLabel("ffprobe path:"), 0, 3);
        g.Controls.Add(_txtFfprobe, 1, 3);
        g.Controls.Add(btnFp, 2, 3);
        g.Controls.Add(_lblFfprobeStatus, 3, 3);

        var opts = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 0, 4),
        };
        opts.Controls.Add(MakeLabel("CRF:", 0));
        opts.Controls.Add(_numCrf);
        opts.Controls.Add(MakeLabel("Preset:", 14));
        opts.Controls.Add(_cmbPreset);
        opts.Controls.Add(MakeLabel("Min age (min):", 14));
        opts.Controls.Add(_numMinAge);
        opts.Controls.Add(MakeLabel("Threads:", 14));
        opts.Controls.Add(_numThreads);
        _chkKeepOriginal.Margin = new Padding(14, 6, 3, 3);
        opts.Controls.Add(_chkKeepOriginal);
        g.Controls.Add(opts, 0, 4);
        g.SetColumnSpan(opts, 4);

        var btnLog = new Button { Text = "Browse…", AutoSize = true };
        btnLog.Click += (_, _) => PickLogPath();
        g.Controls.Add(_chkLog, 0, 5);
        g.Controls.Add(_txtLogPath, 1, 5);
        g.Controls.Add(btnLog, 2, 5);
        g.SetColumnSpan(btnLog, 2);

        var box = new GroupBox
        {
            Text = "Settings",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(8),
        };
        box.Controls.Add(g);
        return box;
    }

    private Control BuildCenter()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 320,
        };

        // Top: file list + small toolbar
        _colFile = _list.Columns.Add("File", 520);
        _colStatus = _list.Columns.Add("Status", 110);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        bar.Controls.Add(_btnScan);
        bar.Controls.Add(_btnCheckAll);
        bar.Controls.Add(_btnUncheckAll);
        bar.Controls.Add(_lblCount);

        var listPanel = new Panel { Dock = DockStyle.Fill };
        listPanel.Controls.Add(_list);
        listPanel.Controls.Add(bar);

        var listBox = new GroupBox { Text = "Files to encode", Dock = DockStyle.Fill, Padding = new Padding(8) };
        listBox.Controls.Add(listPanel);
        split.Panel1.Controls.Add(listBox);

        // Bottom: log
        var logBox = new GroupBox { Text = "Log", Dock = DockStyle.Fill, Padding = new Padding(8) };
        logBox.Controls.Add(_txtLog);
        split.Panel2.Controls.Add(logBox);

        return split;
    }

    private Control BuildBottom()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        t.Controls.Add(_lblCurrent, 0, 0);
        t.Controls.Add(_progFile, 0, 1);

        var overall = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        overall.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        overall.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        overall.Controls.Add(MakeLabel("Overall:"), 0, 0);
        overall.Controls.Add(_progOverall, 1, 0);
        overall.Controls.Add(_lblOverall, 0, 1);
        overall.SetColumnSpan(_lblOverall, 2);
        t.Controls.Add(overall, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Margin = new Padding(12, 0, 0, 0),
        };
        buttons.Controls.Add(_btnStart);
        buttons.Controls.Add(_btnStop);
        t.Controls.Add(buttons, 1, 0);
        t.SetRowSpan(buttons, 3);

        return t;
    }

    private static Label MakeLabel(string text, int leftMargin = 3) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(leftMargin, 7, 3, 3),
    };

    private void ResizeColumns()
    {
        int w = _list.ClientSize.Width - _colStatus.Width - 4;
        if (w > 100) _colFile.Width = w;
    }

    private void PickFolder()
    {
        using var d = new FolderBrowserDialog();
        if (Directory.Exists(_txtTarget.Text))
            d.SelectedPath = _txtTarget.Text;
        if (d.ShowDialog(this) == DialogResult.OK)
        {
            _txtTarget.Text = d.SelectedPath;
            if (string.IsNullOrWhiteSpace(_txtTrackFile.Text))
                _txtTrackFile.Text = Path.Combine(d.SelectedPath, "reencoded.list");
        }
    }

    private void PickFile()
    {
        using var d = new OpenFileDialog { Filter = "MP4 files (*.mp4)|*.mp4|All files (*.*)|*.*" };
        if (d.ShowDialog(this) == DialogResult.OK)
        {
            _txtTarget.Text = d.FileName;
            if (string.IsNullOrWhiteSpace(_txtTrackFile.Text))
                _txtTrackFile.Text = Path.Combine(Path.GetDirectoryName(d.FileName) ?? ".", "reencoded.list");
        }
    }

    private bool PickExe(TextBox target)
    {
        using var d = new OpenFileDialog { Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*" };
        if (d.ShowDialog(this) != DialogResult.OK) return false;
        target.Text = d.FileName;
        return true;
    }

    private void PickTrackFile()
    {
        using var d = new SaveFileDialog
        {
            Filter = "List file (*.list)|*.list|Text file (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = "reencoded.list",
            OverwritePrompt = false,
        };
        if (d.ShowDialog(this) == DialogResult.OK)
            _txtTrackFile.Text = d.FileName;
    }

    private void PickLogPath()
    {
        using var d = new SaveFileDialog
        {
            Filter = "Log file (*.log)|*.log|All files (*.*)|*.*",
            FileName = "reencoded.log",
            OverwritePrompt = false,
        };
        if (d.ShowDialog(this) == DialogResult.OK)
            _txtLogPath.Text = d.FileName;
    }

    private string ResolveTrackFile(string target)
    {
        string t = _txtTrackFile.Text.Trim();
        if (!string.IsNullOrEmpty(t)) return t;
        string baseDir = Directory.Exists(target) ? target : (Path.GetDirectoryName(target) ?? ".");
        return Path.Combine(baseDir, "reencoded.list");
    }

    private EncodeOptions BuildOptions() => new()
    {
        FfmpegPath = string.IsNullOrWhiteSpace(_txtFfmpeg.Text) ? "ffmpeg" : _txtFfmpeg.Text.Trim(),
        FfprobePath = string.IsNullOrWhiteSpace(_txtFfprobe.Text) ? "ffprobe" : _txtFfprobe.Text.Trim(),
        Crf = (int)_numCrf.Value,
        Preset = (string)(_cmbPreset.SelectedItem ?? "medium"),
        MinAgeMinutes = (int)_numMinAge.Value,
        Threads = (int)_numThreads.Value,
        KeepOriginal = _chkKeepOriginal.Checked,
        LogFilePath = _chkLog.Checked ? _txtLogPath.Text.Trim() : null,
    };

    private async void OnScan(object? sender, EventArgs e)
    {
        string target = _txtTarget.Text.Trim();
        bool isDir = Directory.Exists(target);
        bool isFile = File.Exists(target);
        if (!isDir && !isFile)
        {
            MessageBox.Show(this, "Please choose an existing folder or .mp4 file.", "Reencoder",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var opt = BuildOptions();
        string trackPath = ResolveTrackFile(target);

        EnableInputs(false);
        _btnScan.Enabled = false;
        _btnStart.Enabled = false;
        _lblCount.Text = "Scanning…";

        try
        {
            ScanResult result = await Task.Run(() =>
            {
                var tracker = new EncodedTracker(trackPath);
                if (isDir)
                    return Encoder.Scan(target, opt, tracker);

                // Single file: include only if it qualifies.
                var r = new ScanResult();
                r.TotalMp4 = 1;
                if (tracker.Contains(target)) r.AlreadyEncoded = 1;
                else r.ToEncode.Add(target);
                return r;
            });

            PopulateList(result.ToEncode, isDir ? target : null);
            _lblCount.Text =
                $"{result.ToEncode.Count} to encode  ·  {result.AlreadyEncoded} already done  ·  {result.TooRecent} too recent  ·  {result.TotalMp4} total";
            _btnStart.Enabled = result.ToEncode.Count > 0;
        }
        catch (Exception ex)
        {
            _lblCount.Text = "Scan failed";
            AppendLog($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Scan error: {ex.Message}");
        }
        finally
        {
            EnableInputs(true);
            _btnScan.Enabled = true;
        }
    }

    private void PopulateList(IReadOnlyList<string> files, string? rootForRelative)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        _itemByPath.Clear();

        foreach (var f in files)
        {
            string display = rootForRelative != null ? Path.GetRelativePath(rootForRelative, f) : f;
            var item = new ListViewItem(display) { Tag = f, Checked = true };
            item.SubItems.Add("Pending");
            _list.Items.Add(item);
            _itemByPath[FullKey(f)] = item;
        }

        _list.EndUpdate();
        ResizeColumns();
    }

    private void SetAllChecked(bool value)
    {
        _list.BeginUpdate();
        foreach (ListViewItem i in _list.Items)
            i.Checked = value;
        _list.EndUpdate();
    }

    private async void OnStart(object? sender, EventArgs e)
    {
        var files = _list.Items.Cast<ListViewItem>()
            .Where(i => i.Checked)
            .Select(i => (string)i.Tag!)
            .ToList();

        if (files.Count == 0)
        {
            MessageBox.Show(this, "Nothing checked. Click Scan, then check the files to encode.",
                "Reencoder", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var opt = BuildOptions();

        if (!await EnsureToolsAvailableAsync(opt))
            return;

        string trackPath = ResolveTrackFile(_txtTarget.Text.Trim());
        var tracker = new EncodedTracker(trackPath);

        _cts = new CancellationTokenSource();
        SetRunning(true);
        _progFile.Value = 0;
        _progOverall.Value = 0;
        _total = 0;

        var progress = new Progress<ProgressUpdate>(OnProgress);

        try
        {
            await Task.Run(() => _encoder.EncodeListAsync(files, opt, tracker, progress, _cts.Token), _cts.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLog($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Stopped by user");
        }
        catch (Exception ex)
        {
            AppendLog($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - FATAL: {ex.Message}");
        }
        finally
        {
            SetRunning(false);
            _lblCurrent.Text = "Idle";
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Confirms ffmpeg and ffprobe can be launched before starting work, so the
    /// run fails fast with one clear message instead of erroring per file.
    /// </summary>
    private async Task<bool> EnsureToolsAvailableAsync(EncodeOptions opt)
    {
        _lblCurrent.Text = "Checking ffmpeg / ffprobe…";

        Task<bool> ffmpegTask = Encoder.IsExecutableAvailableAsync(opt.FfmpegPath, "ffmpeg");
        Task<bool> ffprobeTask = Encoder.IsExecutableAvailableAsync(opt.FfprobePath, "ffprobe");
        await Task.WhenAll(ffmpegTask, ffprobeTask);

        SetToolStatus(_lblFfmpegStatus, ffmpegTask.Result);
        SetToolStatus(_lblFfprobeStatus, ffprobeTask.Result);
        _lblCurrent.Text = "Idle";

        var missing = new List<string>();
        if (!ffmpegTask.Result) missing.Add($"ffmpeg  ({opt.FfmpegPath})");
        if (!ffprobeTask.Result) missing.Add($"ffprobe  ({opt.FfprobePath})");
        if (missing.Count == 0) return true;

        string list = string.Join(Environment.NewLine, missing);
        AppendLog($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Not a valid ffmpeg/ffprobe: {string.Join(", ", missing)}");
        MessageBox.Show(this,
            "These do not appear to be valid ffmpeg/ffprobe executables:" +
            Environment.NewLine + Environment.NewLine + list +
            Environment.NewLine + Environment.NewLine +
            "Make sure ffmpeg/ffprobe are on PATH (leave the fields as \"ffmpeg\" / " +
            "\"ffprobe\") or browse to the exact .exe paths.",
            "Reencoder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    /// <summary>
    /// Probes the configured ffmpeg/ffprobe paths and reflects the result in the
    /// status labels next to each field. Runs at launch and whenever a path
    /// changes. Re-entrant calls are harmless (the labels just update again).
    /// </summary>
    private async Task DetectToolsAsync()
    {
        var opt = BuildOptions();
        _lblFfmpegStatus.Text = "checking…";
        _lblFfmpegStatus.ForeColor = SystemColors.GrayText;
        _lblFfprobeStatus.Text = "checking…";
        _lblFfprobeStatus.ForeColor = SystemColors.GrayText;

        Task<bool> ffmpegTask = Encoder.IsExecutableAvailableAsync(opt.FfmpegPath, "ffmpeg");
        Task<bool> ffprobeTask = Encoder.IsExecutableAvailableAsync(opt.FfprobePath, "ffprobe");
        await Task.WhenAll(ffmpegTask, ffprobeTask);

        SetToolStatus(_lblFfmpegStatus, ffmpegTask.Result);
        SetToolStatus(_lblFfprobeStatus, ffprobeTask.Result);
    }

    private static void SetToolStatus(Label label, bool ok)
    {
        label.Text = ok ? "✓ detected" : "✗ not found";
        label.ForeColor = ok ? Color.Green : Color.Firebrick;
    }

    private void LoadSettings()
    {
        var s = AppSettings.Load();
        _txtTarget.Text = s.Target;
        _txtTrackFile.Text = s.TrackFile;
        _txtFfmpeg.Text = string.IsNullOrWhiteSpace(s.FfmpegPath) ? "ffmpeg" : s.FfmpegPath;
        _txtFfprobe.Text = string.IsNullOrWhiteSpace(s.FfprobePath) ? "ffprobe" : s.FfprobePath;
        _numCrf.Value = ClampDec(s.Crf, _numCrf.Minimum, _numCrf.Maximum);
        if (_cmbPreset.Items.Contains(s.Preset)) _cmbPreset.SelectedItem = s.Preset;
        _numMinAge.Value = ClampDec(s.MinAgeMinutes, _numMinAge.Minimum, _numMinAge.Maximum);
        _numThreads.Value = ClampDec(s.Threads, _numThreads.Minimum, _numThreads.Maximum);
        _chkKeepOriginal.Checked = s.KeepOriginal;
        _chkLog.Checked = s.WriteLog;
        if (!string.IsNullOrWhiteSpace(s.LogPath)) _txtLogPath.Text = s.LogPath;
    }

    private void SaveSettings()
    {
        new AppSettings
        {
            Target = _txtTarget.Text.Trim(),
            TrackFile = _txtTrackFile.Text.Trim(),
            FfmpegPath = _txtFfmpeg.Text.Trim(),
            FfprobePath = _txtFfprobe.Text.Trim(),
            Crf = (int)_numCrf.Value,
            Preset = (string)(_cmbPreset.SelectedItem ?? "medium"),
            MinAgeMinutes = (int)_numMinAge.Value,
            Threads = (int)_numThreads.Value,
            KeepOriginal = _chkKeepOriginal.Checked,
            WriteLog = _chkLog.Checked,
            LogPath = _txtLogPath.Text.Trim(),
        }.Save();
    }

    private static decimal ClampDec(decimal v, decimal min, decimal max)
        => v < min ? min : (v > max ? max : v);

    private void OnStop(object? sender, EventArgs e)
    {
        _btnStop.Enabled = false;
        _cts?.Cancel();
    }

    private void OnProgress(ProgressUpdate u)
    {
        if (u.Message != null)
            AppendLog(u.Message);

        if (u.CurrentFile != null)
            _lblCurrent.Text = u.CurrentFile;

        if (u.FilePercent.HasValue)
            _progFile.Value = ClampToBar(u.FilePercent.Value);

        if (u.FilesTotal.HasValue)
            _total = u.FilesTotal.Value;

        if (u.FilesDone.HasValue)
        {
            _lblOverall.Text = $"{u.FilesDone.Value} / {_total}";
            _progOverall.Value = _total > 0 ? ClampToBar(100.0 * u.FilesDone.Value / _total) : 0;
        }

        if (u.FileResultPath != null && u.FileResultStatus != null
            && _itemByPath.TryGetValue(FullKey(u.FileResultPath), out var item)
            && item.SubItems.Count > 1)
        {
            item.SubItems[1].Text = u.FileResultStatus;
        }
    }

    private static int ClampToBar(double v)
    {
        int i = (int)Math.Round(v);
        return i < 0 ? 0 : (i > 100 ? 100 : i);
    }

    private static string FullKey(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private void AppendLog(string line) => _txtLog.AppendText(line + Environment.NewLine);

    private void EnableInputs(bool enabled)
    {
        _txtTarget.Enabled = enabled;
        _txtTrackFile.Enabled = enabled;
        _txtFfmpeg.Enabled = enabled;
        _txtFfprobe.Enabled = enabled;
        _numCrf.Enabled = enabled;
        _cmbPreset.Enabled = enabled;
        _numMinAge.Enabled = enabled;
        _numThreads.Enabled = enabled;
        _chkKeepOriginal.Enabled = enabled;
        _chkLog.Enabled = enabled;
        _txtLogPath.Enabled = enabled && _chkLog.Checked;
        _btnCheckAll.Enabled = enabled;
        _btnUncheckAll.Enabled = enabled;
    }

    private void SetRunning(bool running)
    {
        EnableInputs(!running);
        _btnScan.Enabled = !running;
        _btnStart.Enabled = !running && _list.Items.Count > 0;
        _btnStop.Enabled = running;
        _list.Enabled = !running;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts?.Cancel();
        SaveSettings();
        base.OnFormClosing(e);
    }
}
