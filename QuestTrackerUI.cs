using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using VoK.Sdk.Plugins;

namespace QuestTracker;

/// <summary>
/// WinForms-based UI for the Quest Tracker plugin
/// </summary>
public class QuestTrackerUI : IPluginUI
{
    private static Image? _toolbarImage;
    private QuestTrackerForm? _form;
    private Label? _statusLabel;
    private TextBox? _exportTextBox;
    private Label? _instanceLabel;
    private System.Windows.Forms.Timer? _statusResetTimer;

    // IPluginUI interface members
    public float? FocusedOpacity => 1.0f;
    public bool EnabledInCharacterSelection => true;
    public object? UserInterfaceForm => GetForm();
    public Tuple<int, int> MinSize => new Tuple<int, int>(450, 350);
    public Image? ToolbarImage => GetToolbarImage();

    private object GetForm()
    {
        if (_form == null || _form.IsDisposed)
        {
            _form = new QuestTrackerForm(this);
        }
        return _form;
    }

    private static Image GetToolbarImage()
    {
        if (_toolbarImage != null)
            return _toolbarImage;

        try
        {
            // Load QTLogo.png from embedded resources
            var assembly = typeof(QuestTrackerUI).Assembly;
            using (var stream = assembly.GetManifestResourceStream("QuestTracker.QTLogo.png"))
            {
                if (stream != null)
                {
                    var originalImage = Image.FromStream(stream);
                    // Resize to 36x36 for toolbar
                    var resized = new Bitmap(36, 36);
                    using (var g = Graphics.FromImage(resized))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.DrawImage(originalImage, 0, 0, 36, 36);
                    }
                    _toolbarImage = resized;
                    return _toolbarImage;
                }
            }
        }
        catch
        {
            // Fall through to generate default icon
        }

        // Fallback: Create a 36x36 icon with a "Q" for Quest
        var bitmap = new Bitmap(36, 36);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            using (var brush = new SolidBrush(Color.FromArgb(212, 175, 55)))
            {
                g.FillEllipse(brush, 2, 2, 32, 32);
            }

            using (var pen = new Pen(Color.FromArgb(139, 119, 42), 2))
            {
                g.DrawEllipse(pen, 2, 2, 32, 32);
            }

            using (var font = new Font(FontFamily.GenericSansSerif, 18, FontStyle.Bold))
            using (var brush = new SolidBrush(Color.FromArgb(26, 26, 26)))
            {
                var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString("Q", font, brush, new RectangleF(0, 0, 36, 36), sf);
            }
        }

        _toolbarImage = bitmap;
        return _toolbarImage;
    }

    public object GetUI()
    {
        return GetForm();
    }

    public void SetStatusLabel(Label label)
    {
        _statusLabel = label;
    }

    public void SetExportTextBox(TextBox textBox)
    {
        _exportTextBox = textBox;
    }

    public void SetInstanceLabel(Label label)
    {
        _instanceLabel = label;
    }

    public void UpdateInstanceDisplay(string? questName)
    {
        if (_instanceLabel == null) return;

        var displayText = string.IsNullOrEmpty(questName) ? "Public Area" : questName;
        
        if (_instanceLabel.InvokeRequired)
        {
            _instanceLabel.Invoke(() => _instanceLabel.Text = $"Current Area: {displayText}");
        }
        else
        {
            _instanceLabel.Text = $"Current Area: {displayText}";
        }
    }

    public void UpdateStatus(string message, bool isError = false)
    {
        if (_statusLabel == null) return;

        _statusLabel.Text = message;
        _statusLabel.ForeColor = isError
            ? Color.FromArgb(255, 100, 100)
            : Color.FromArgb(100, 255, 100);
    }

    public void ResetStatusAfterDelay(int delayMs)
    {
        _statusResetTimer?.Stop();
        _statusResetTimer?.Dispose();
        
        _statusResetTimer = new System.Windows.Forms.Timer();
        _statusResetTimer.Interval = delayMs;
        _statusResetTimer.Tick += (s, e) =>
        {
            _statusResetTimer?.Stop();
            RefreshQuestCount();
        };
        _statusResetTimer.Start();
    }

    public void SetExportText(string text)
    {
        if (_exportTextBox != null)
        {
            _exportTextBox.Text = text;
        }
    }

    public void RefreshQuestCount()
    {
        if (Plugin.Instance == null || _statusLabel == null) return;

        var count = Plugin.Instance.GetCompletedQuestCount();
        
        // Marshal to UI thread if needed
        if (_statusLabel.InvokeRequired)
        {
            _statusLabel.Invoke(() =>
            {
                _statusLabel.Text = $"Found {count} completed quests";
                _statusLabel.ForeColor = Color.FromArgb(224, 224, 224);
            });
        }
        else
        {
            _statusLabel.Text = $"Found {count} completed quests";
            _statusLabel.ForeColor = Color.FromArgb(224, 224, 224);
        }
    }
}

/// <summary>
/// WinForms Form for Quest Tracker
/// </summary>
public class QuestTrackerForm : Form
{
    private readonly QuestTrackerUI _ui;
    private Label? _statusLabel;
    private TextBox? _exportTextBox;

    public QuestTrackerForm(QuestTrackerUI ui)
    {
        _ui = ui;
        InitializeComponents();
        
        // Refresh the quest count when the form is shown
        this.Shown += (s, e) => _ui.RefreshQuestCount();
    }

    private void InitializeComponents()
    {
        // Form settings
        this.Text = "Quest Tracker";
        this.BackColor = Color.FromArgb(37, 40, 50);
        this.ForeColor = Color.FromArgb(224, 224, 224);
        this.MinimumSize = new Size(450, 350);
        this.Size = new Size(450, 350);
        this.FormBorderStyle = FormBorderStyle.None;
        this.Padding = new Padding(10);

        // Main panel
        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            BackColor = Color.Transparent
        };
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Title
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Description
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Status
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Hint
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Buttons
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // TextBox
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Instructions
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Instance

        // Title
        var titleLabel = new Label
        {
            Text = "Quest Tracker for DDOQT",
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            ForeColor = Color.FromArgb(212, 175, 55),
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 10)
        };
        mainPanel.Controls.Add(titleLabel, 0, 0);

        // Description
        var descLabel = new Label
        {
            Text = "Export your completed quests to qt.ddotools.xyz",
            Font = new Font("Segoe UI", 10),
            ForeColor = Color.FromArgb(176, 176, 176),
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 15)
        };
        mainPanel.Controls.Add(descLabel, 0, 1);

        // Status
        _statusLabel = new Label
        {
            Text = "Waiting for quest data...",
            Font = new Font("Segoe UI", 11),
            ForeColor = Color.FromArgb(224, 224, 224),
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 5)
        };
        mainPanel.Controls.Add(_statusLabel, 0, 2);
        _ui.SetStatusLabel(_statusLabel);

        // Hint label
        var hintLabel = new Label
        {
            Text = "Sync buttons will open qt.ddotools.xyz in your browser",
            Font = new Font("Segoe UI", 9, FontStyle.Italic),
            ForeColor = Color.FromArgb(140, 140, 140),
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 15)
        };
        mainPanel.Controls.Add(hintLabel, 0, 3);

        // Button panel
        var buttonPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Anchor = AnchorStyles.None,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 15),
            MaximumSize = new Size(420, 0)
        };

        // Generate button
        var generateButton = CreateButton("Generate Export", Color.FromArgb(212, 175, 55), Color.FromArgb(26, 26, 26));
        generateButton.Click += OnGenerateClick;
        buttonPanel.Controls.Add(generateButton);

        // Copy URL button
        var copyButton = CreateButton("Copy URL", Color.FromArgb(70, 130, 180), Color.White);
        copyButton.Click += OnCopyUrlClick;
        buttonPanel.Controls.Add(copyButton);

        // Sync to DDOQT (Merge) button
        var syncMergeButton = CreateButton("Sync (Merge)", Color.FromArgb(60, 179, 113), Color.White);
        syncMergeButton.Click += OnSyncMergeClick;
        buttonPanel.Controls.Add(syncMergeButton);
        
        // Sync to DDOQT (Replace) button
        var syncReplaceButton = CreateButton("Sync (Replace)", Color.FromArgb(220, 80, 80), Color.White);
        syncReplaceButton.Click += OnSyncReplaceClick;
        buttonPanel.Controls.Add(syncReplaceButton);

        mainPanel.Controls.Add(buttonPanel, 0, 4);

        // Export TextBox
        _exportTextBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(30, 33, 40),
            ForeColor = Color.FromArgb(200, 200, 200),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 10),
            Dock = DockStyle.Fill,
            Text = "Click 'Generate Export Hash' to create your DDOQT import data"
        };
        mainPanel.Controls.Add(_exportTextBox, 0, 5);
        _ui.SetExportTextBox(_exportTextBox);

        // Instructions
        var instructionLabel = new Label
        {
            Text = "Copy URL: generates a link you can share or bookmark to import your quests",
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(150, 150, 150),
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 10, 0, 0),
            MaximumSize = new Size(420, 0)
        };
        mainPanel.Controls.Add(instructionLabel, 0, 6);

        // Instance label
        var instanceLabel = new Label
        {
            Text = "Current Area: Public Area",
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(180, 180, 180),
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 5, 0, 0)
        };
        mainPanel.Controls.Add(instanceLabel, 0, 7);
        _ui.SetInstanceLabel(instanceLabel);

        this.Controls.Add(mainPanel);

        // Version label (bottom-right)
        var version = typeof(Plugin).Assembly.GetName().Version;
        var versionLabel = new Label
        {
            Text = $"v{version?.Major}.{version?.Minor}.{version?.Build}",
            Font = new Font("Segoe UI", 7.5f),
            ForeColor = Color.FromArgb(100, 100, 100),
            AutoSize = true,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            BackColor = Color.Transparent
        };
        this.Controls.Add(versionLabel);
        versionLabel.BringToFront();
        // Position after layout is computed
        this.Layout += (s, e) =>
        {
            versionLabel.Location = new Point(
                this.ClientSize.Width - versionLabel.Width - 6,
                this.ClientSize.Height - versionLabel.Height - 4);
        };
    }

    private Button CreateButton(string text, Color backColor, Color foreColor)
    {
        return new Button
        {
            Text = text,
            BackColor = backColor,
            ForeColor = foreColor,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Padding = new Padding(15, 8, 15, 8),
            Margin = new Padding(5),
            AutoSize = true,
            Cursor = Cursors.Hand
        };
    }

    private void OnGenerateClick(object? sender, EventArgs e)
    {
        try
        {
            if (Plugin.Instance == null)
            {
                _ui.UpdateStatus("Plugin not initialized", isError: true);
                return;
            }

            _ui.UpdateStatus("Fetching quest data...");
            Plugin.Instance.RefreshQuestData();

            var count = Plugin.Instance.GetCompletedQuestCount();
            if (count == 0)
            {
                _ui.UpdateStatus("No quest data - please log in to a character first", isError: true);
                _ui.SetExportText("Quest data loads when you log in to a character.");
                return;
            }
            
            var hash = Plugin.Instance.GenerateExportHash();

            _ui.SetExportText($"https://qt.ddotools.xyz/#{hash}");
            _ui.UpdateStatus($"Generated hash for {count} completed quests");
        }
        catch (Exception ex)
        {
            _ui.UpdateStatus($"Error: {ex.Message}", isError: true);
        }
    }

    private void OnCopyUrlClick(object? sender, EventArgs e)
    {
        try
        {
            if (Plugin.Instance == null)
            {
                _ui.UpdateStatus("Plugin not initialized", isError: true);
                return;
            }

            var count = Plugin.Instance.GetCompletedQuestCount();
            if (count == 0)
            {
                _ui.UpdateStatus("No quest data - please log in to a character first", isError: true);
                return;
            }

            var url = Plugin.Instance.GetExportUrl();
            
            // Clipboard requires STA thread
            var thread = new System.Threading.Thread(() => Clipboard.SetText(url));
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
            
            _ui.UpdateStatus("URL copied to clipboard!");
        }
        catch (Exception ex)
        {
            _ui.UpdateStatus($"Error: {ex.Message}", isError: true);
        }
    }

    private void OnSyncMergeClick(object? sender, EventArgs e)
    {
        SyncToDdoqt("merge");
    }
    
    private void OnSyncReplaceClick(object? sender, EventArgs e)
    {
        SyncToDdoqt("replace");
    }
    
    private void SyncToDdoqt(string action)
    {
        try
        {
            if (Plugin.Instance == null)
            {
                _ui.UpdateStatus("Plugin not initialized", isError: true);
                return;
            }

            var count = Plugin.Instance.GetCompletedQuestCount();
            if (count == 0)
            {
                _ui.UpdateStatus("No quest data - please log in to a character first", isError: true);
                return;
            }

            var url = Plugin.Instance.GetAutoSyncUrl(action);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            _ui.UpdateStatus($"Syncing to DDOQT ({action})...");
            _ui.ResetStatusAfterDelay(3000);
        }
        catch (Exception ex)
        {
            _ui.UpdateStatus($"Error: {ex.Message}", isError: true);
        }
    }
}
