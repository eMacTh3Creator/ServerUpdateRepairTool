namespace WinUpdateRepairTool;

partial class Form1
{
    private System.ComponentModel.IContainer components = null;
    private TableLayoutPanel rootLayout = null!;
    private FlowLayoutPanel buttonPanel = null!;
    private Button diagnosticButton = null!;
    private Button repairButton = null!;
    private Button resetWuButton = null!;
    private Button bootCrashButton = null!;
    private Button cancelButton = null!;
    private Button openLogButton = null!;
    private Button zipButton = null!;
    private TextBox outputTextBox = null!;
    private TextBox sourceTextBox = null!;
    private CheckBox limitAccessCheckBox = null!;
    private Panel headerPanel = null!;
    private Panel logoPanel = null!;
    private Label titleLabel = null!;
    private Label statusLabel = null!;
    private Label warningLabel = null!;
    private ProgressBar progressBar = null!;

    /// <summary>
    ///  Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        rootLayout = new TableLayoutPanel();
        buttonPanel = new FlowLayoutPanel();
        diagnosticButton = new Button();
        repairButton = new Button();
        resetWuButton = new Button();
        bootCrashButton = new Button();
        cancelButton = new Button();
        openLogButton = new Button();
        zipButton = new Button();
        outputTextBox = new TextBox();
        sourceTextBox = new TextBox();
        limitAccessCheckBox = new CheckBox();
        headerPanel = new Panel();
        logoPanel = new Panel();
        titleLabel = new Label();
        statusLabel = new Label();
        warningLabel = new Label();
        progressBar = new ProgressBar();
        rootLayout.SuspendLayout();
        buttonPanel.SuspendLayout();
        headerPanel.SuspendLayout();
        SuspendLayout();

        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1120, 720);
        MinimumSize = new Size(920, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Server Update Repair Tool";

        rootLayout.ColumnCount = 1;
        rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        rootLayout.Controls.Add(headerPanel, 0, 0);
        rootLayout.Controls.Add(warningLabel, 0, 1);
        rootLayout.Controls.Add(buttonPanel, 0, 2);
        rootLayout.Controls.Add(sourceTextBox, 0, 3);
        rootLayout.Controls.Add(limitAccessCheckBox, 0, 4);
        rootLayout.Controls.Add(progressBar, 0, 5);
        rootLayout.Controls.Add(outputTextBox, 0, 6);
        rootLayout.Controls.Add(statusLabel, 0, 7);
        rootLayout.Dock = DockStyle.Fill;
        rootLayout.Location = new Point(0, 0);
        rootLayout.Name = "rootLayout";
        rootLayout.Padding = new Padding(12);
        rootLayout.RowCount = 8;
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
        rootLayout.Size = new Size(1120, 720);
        rootLayout.TabIndex = 0;

        headerPanel.Dock = DockStyle.Fill;
        headerPanel.Name = "headerPanel";
        headerPanel.Controls.Add(titleLabel);
        headerPanel.Controls.Add(logoPanel);

        logoPanel.Dock = DockStyle.Left;
        logoPanel.Name = "logoPanel";
        logoPanel.Width = 68;
        logoPanel.Paint += LogoPanel_Paint;

        titleLabel.Dock = DockStyle.Fill;
        titleLabel.Font = new Font("Segoe UI Semibold", 20F, FontStyle.Bold);
        titleLabel.ForeColor = Color.FromArgb(15, 23, 42);
        titleLabel.Name = "titleLabel";
        titleLabel.Padding = new Padding(12, 0, 0, 0);
        titleLabel.Text = "Server Update Repair Tool";
        titleLabel.TextAlign = ContentAlignment.MiddleLeft;

        warningLabel.AutoSize = false;
        warningLabel.Dock = DockStyle.Fill;
        warningLabel.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        warningLabel.ForeColor = Color.FromArgb(142, 77, 0);
        warningLabel.Name = "warningLabel";
        warningLabel.Text = "Use this on the affected server as Administrator. For production VMs, take a VMware snapshot before repair actions. Diagnostics are read-mostly; repair actions reset update caches and run Windows servicing tools.";
        warningLabel.TextAlign = ContentAlignment.MiddleLeft;

        buttonPanel.AutoSize = false;
        buttonPanel.Dock = DockStyle.Fill;
        buttonPanel.FlowDirection = FlowDirection.LeftToRight;
        buttonPanel.Name = "buttonPanel";
        buttonPanel.Padding = new Padding(0, 4, 0, 0);
        buttonPanel.WrapContents = true;

        ConfigureButton(diagnosticButton, "Full Diagnostic", DiagnosticButton_Click, 126);
        ConfigureButton(repairButton, "Recommended Repair", RepairButton_Click, 152);
        ConfigureButton(resetWuButton, "Reset Update Cache", ResetWuButton_Click, 146);
        ConfigureButton(bootCrashButton, "Boot/Crash Logs", BootCrashButton_Click, 126);
        ConfigureButton(cancelButton, "Cancel", CancelButton_Click, 82);
        ConfigureButton(openLogButton, "Open Logs", OpenLogButton_Click, 92);
        ConfigureButton(zipButton, "Zip Bundle", ZipButton_Click, 92);
        buttonPanel.Controls.AddRange(new Control[]
        {
            diagnosticButton,
            repairButton,
            resetWuButton,
            bootCrashButton,
            cancelButton,
            openLogButton,
            zipButton
        });

        sourceTextBox.Dock = DockStyle.Fill;
        sourceTextBox.Name = "sourceTextBox";
        sourceTextBox.PlaceholderText = "Optional DISM repair source, for example D:\\sources\\sxs or WIM:D:\\sources\\install.wim:2";
        sourceTextBox.TabIndex = 7;

        limitAccessCheckBox.AutoSize = true;
        limitAccessCheckBox.Dock = DockStyle.Fill;
        limitAccessCheckBox.Name = "limitAccessCheckBox";
        limitAccessCheckBox.Text = "DISM /LimitAccess when using the optional repair source";
        limitAccessCheckBox.UseVisualStyleBackColor = true;

        progressBar.Dock = DockStyle.Fill;
        progressBar.Name = "progressBar";

        outputTextBox.BackColor = Color.FromArgb(18, 18, 18);
        outputTextBox.BorderStyle = BorderStyle.FixedSingle;
        outputTextBox.Dock = DockStyle.Fill;
        outputTextBox.Font = new Font("Consolas", 9.5F);
        outputTextBox.ForeColor = Color.FromArgb(232, 232, 232);
        outputTextBox.Multiline = true;
        outputTextBox.Name = "outputTextBox";
        outputTextBox.ReadOnly = true;
        outputTextBox.ScrollBars = ScrollBars.Both;
        outputTextBox.WordWrap = false;

        statusLabel.AutoSize = false;
        statusLabel.Dock = DockStyle.Fill;
        statusLabel.Name = "statusLabel";
        statusLabel.Text = "Ready.";
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;

        Controls.Add(rootLayout);
        rootLayout.ResumeLayout(false);
        rootLayout.PerformLayout();
        buttonPanel.ResumeLayout(false);
        headerPanel.ResumeLayout(false);
        ResumeLayout(false);
    }

    private static void ConfigureButton(Button button, string text, EventHandler handler, int width)
    {
        button.AutoSize = false;
        button.Height = 34;
        button.Margin = new Padding(0, 0, 8, 8);
        button.Text = text;
        button.UseVisualStyleBackColor = true;
        button.Width = width;
        button.Click += handler;
    }
}
