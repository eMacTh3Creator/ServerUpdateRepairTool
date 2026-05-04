using System.Drawing.Drawing2D;
using System.Text;

namespace WinUpdateRepairTool;

public partial class Form1 : Form
{
    private const int MaxVisibleLogCharacters = 250_000;
    private readonly RepairEngine _engine = new();
    private readonly Queue<string> _pendingOutput = new();
    private readonly System.Windows.Forms.Timer _outputFlushTimer = new() { Interval = 150 };
    private CancellationTokenSource? _operationCts;
    private LogSession? _session;

    public Form1()
    {
        InitializeComponent();
        _outputFlushTimer.Tick += (_, _) => FlushPendingOutput();
        SetIdleState();
    }

    private async void DiagnosticButton_Click(object? sender, EventArgs e)
    {
        await RunOperationAsync("Full diagnostic", (log, progress, token) => _engine.RunDiagnosticsAsync(log, progress, token));
    }

    private async void BootRiskButton_Click(object? sender, EventArgs e)
    {
        await RunOperationAsync("Boot risk check", (log, progress, token) => _engine.RunBootRiskCheckAsync(log, progress, token));
    }

    private async void RepairButton_Click(object? sender, EventArgs e)
    {
        var answer = MessageBox.Show(
            "Recommended repair will reset Windows Update caches, run DISM RestoreHealth, run SFC, and run an online disk scan. Take a VMware snapshot or backup first if this is a production server.",
            "Run recommended repair?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        await RunOperationAsync("Recommended repair", (log, progress, token) =>
            _engine.RunRecommendedRepairAsync(log, progress, sourceTextBox.Text, limitAccessCheckBox.Checked, token));
    }

    private async void ResetWuButton_Click(object? sender, EventArgs e)
    {
        var answer = MessageBox.Show(
            "This will stop Windows Update services and rename SoftwareDistribution and catroot2 so Windows rebuilds the update cache.",
            "Reset Windows Update cache?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        await RunOperationAsync("Windows Update reset", (log, progress, token) => _engine.ResetWindowsUpdateAsync(log, progress, token));
    }

    private async void BootCrashButton_Click(object? sender, EventArgs e)
    {
        await RunOperationAsync("Boot and crash evidence", (log, progress, token) => _engine.CollectBootCrashAsync(log, progress, token));
    }

    private void CancelButton_Click(object? sender, EventArgs e)
    {
        _operationCts?.Cancel();
        AppendOutput("Cancel requested. Waiting for the current command to stop...");
    }

    private void OpenLogButton_Click(object? sender, EventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{_session.RootPath}\"",
            UseShellExecute = true
        });
    }

    private void ZipButton_Click(object? sender, EventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            var zipPath = _session.CreateZip();
            AppendOutput("Created log bundle: " + zipPath);
            MessageBox.Show(zipPath, "Log bundle created", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Could not create log bundle", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RunOperationAsync(
        string name,
        Func<LogSession, IProgress<string>, CancellationToken, Task> operation)
    {
        if (_operationCts is not null)
        {
            return;
        }

        _session = new LogSession();
        _operationCts = new CancellationTokenSource();
        var progress = new Progress<string>(AppendOutput);
        SetBusyState(name);
        AppendOutput($"{name} started.");
        AppendOutput($"Session folder: {_session.RootPath}");

        try
        {
            await operation(_session, progress, _operationCts.Token);
            AppendOutput($"{name} completed.");
            if (!name.Equals("Boot risk check", StringComparison.OrdinalIgnoreCase))
            {
                statusLabel.Text = "Finished. Review transcript.log and exported logs before attempting another update.";
            }
        }
        catch (OperationCanceledException)
        {
            _session.WriteLine("Operation canceled by user.");
            AppendOutput($"{name} canceled.");
            statusLabel.Text = "Canceled.";
        }
        catch (Exception ex)
        {
            _session.WriteLine(ex.ToString());
            AppendOutput(ex.ToString());
            statusLabel.Text = "Failed. Check transcript.log for details.";
            MessageBox.Show(ex.ToString(), "Operation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _operationCts.Dispose();
            _operationCts = null;
            SetIdleState();
        }
    }

    private void AppendOutput(string message)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(AppendOutput), message);
            return;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _pendingOutput.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        if (!_outputFlushTimer.Enabled)
        {
            _outputFlushTimer.Start();
        }

        if (message.StartsWith("BOOT RISK SUMMARY:", StringComparison.OrdinalIgnoreCase))
        {
            ApplyBootRiskSummary(message);
        }
    }

    private void FlushPendingOutput()
    {
        if (_pendingOutput.Count == 0)
        {
            _outputFlushTimer.Stop();
            return;
        }

        var builder = new StringBuilder();
        while (_pendingOutput.Count > 0 && builder.Length < 32_000)
        {
            builder.Append(_pendingOutput.Dequeue());
        }

        outputTextBox.AppendText(builder.ToString());
        TrimVisibleLogIfNeeded();
    }

    private void TrimVisibleLogIfNeeded()
    {
        if (outputTextBox.TextLength <= MaxVisibleLogCharacters)
        {
            return;
        }

        var removeLength = outputTextBox.TextLength - (MaxVisibleLogCharacters / 2);
        outputTextBox.Select(0, removeLength);
        outputTextBox.SelectedText = "[Older UI output trimmed. Full output remains in transcript.log.]\r\n";
        outputTextBox.SelectionStart = outputTextBox.TextLength;
        outputTextBox.ScrollToCaret();
    }

    private void SetBusyState(string name)
    {
        statusLabel.Text = "Running: " + name;
        progressBar.Style = ProgressBarStyle.Marquee;
        statusLabel.ForeColor = SystemColors.ControlText;
        diagnosticButton.Enabled = false;
        bootRiskButton.Enabled = false;
        repairButton.Enabled = false;
        resetWuButton.Enabled = false;
        bootCrashButton.Enabled = false;
        sourceTextBox.Enabled = false;
        limitAccessCheckBox.Enabled = false;
        cancelButton.Enabled = true;
        openLogButton.Enabled = _session is not null;
        zipButton.Enabled = false;
    }

    private void SetIdleState()
    {
        progressBar.Style = ProgressBarStyle.Blocks;
        progressBar.Value = 0;
        diagnosticButton.Enabled = true;
        bootRiskButton.Enabled = true;
        repairButton.Enabled = true;
        resetWuButton.Enabled = true;
        bootCrashButton.Enabled = true;
        sourceTextBox.Enabled = true;
        limitAccessCheckBox.Enabled = true;
        cancelButton.Enabled = false;
        openLogButton.Enabled = _session is not null;
        zipButton.Enabled = _session is not null;
    }

    private void ApplyBootRiskSummary(string summary)
    {
        var failCount = ExtractSummaryCount(summary, "FAIL");
        var warnCount = ExtractSummaryCount(summary, "WARN");

        if (failCount > 0)
        {
            statusLabel.ForeColor = Color.FromArgb(185, 28, 28);
            statusLabel.Text = "Boot Risk Check found critical issues. Review BootRiskCheck.report.txt before updating or rebooting.";
        }
        else if (warnCount > 0)
        {
            statusLabel.ForeColor = Color.FromArgb(180, 83, 9);
            statusLabel.Text = "Boot Risk Check found warnings. Review the report before applying updates.";
        }
        else
        {
            statusLabel.ForeColor = Color.FromArgb(21, 128, 61);
            statusLabel.Text = "Boot Risk Check passed without critical findings.";
        }
    }

    private static int ExtractSummaryCount(string summary, string name)
    {
        var marker = name + "=";
        var index = summary.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return 0;
        }

        index += marker.Length;
        var end = index;
        while (end < summary.Length && char.IsDigit(summary[end]))
        {
            end++;
        }

        return int.TryParse(summary[index..end], out var value) ? value : 0;
    }

    private void LogoPanel_Paint(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(8, 8, logoPanel.Width - 16, logoPanel.Height - 16);

        using var shieldBrush = new LinearGradientBrush(bounds, Color.FromArgb(14, 116, 144), Color.FromArgb(37, 99, 235), 45F);
        using var darkBrush = new SolidBrush(Color.FromArgb(15, 23, 42));
        using var panelBrush = new SolidBrush(Color.FromArgb(239, 246, 255));
        using var okPen = new Pen(Color.FromArgb(34, 197, 94), 4F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        using var shield = new GraphicsPath();
        shield.AddPolygon(new[]
        {
            new Point(bounds.Left + bounds.Width / 2, bounds.Top),
            new Point(bounds.Right, bounds.Top + 10),
            new Point(bounds.Right - 4, bounds.Top + bounds.Height / 2),
            new Point(bounds.Left + bounds.Width / 2, bounds.Bottom),
            new Point(bounds.Left + 4, bounds.Top + bounds.Height / 2),
            new Point(bounds.Left, bounds.Top + 10)
        });
        e.Graphics.FillPath(shieldBrush, shield);

        var server = new Rectangle(bounds.Left + 15, bounds.Top + 18, bounds.Width - 30, bounds.Height - 26);
        using (var serverPath = RoundedRectangle(server, 6))
        {
            e.Graphics.FillPath(panelBrush, serverPath);
        }

        for (var i = 0; i < 3; i++)
        {
            var row = new Rectangle(server.Left + 8, server.Top + 8 + (i * 16), server.Width - 16, 9);
            using var rowPath = RoundedRectangle(row, 3);
            e.Graphics.FillPath(darkBrush, rowPath);
        }

        e.Graphics.DrawLines(okPen, new[]
        {
            new Point(bounds.Left + 23, bounds.Bottom - 18),
            new Point(bounds.Left + 32, bounds.Bottom - 9),
            new Point(bounds.Right - 18, bounds.Bottom - 28)
        });
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
