using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading;
using ImGuiNET;

namespace QuestTracker;

/// <summary>
/// ImGui UI for the Quest Tracker plugin.
/// Render runs on the game's render thread every frame, while status updates arrive from event
/// handlers and background threads, so shared state is replaced whole rather than edited in place.
/// </summary>
public class QuestTrackerUI
{
    // Colours carried over from the WinForms version
    private static readonly Vector4 Gold = Rgb(212, 175, 55);
    private static readonly Vector4 SteelBlue = Rgb(70, 130, 180);
    private static readonly Vector4 SeaGreen = Rgb(60, 179, 113);
    private static readonly Vector4 Red = Rgb(220, 80, 80);
    private static readonly Vector4 White = Rgb(255, 255, 255);
    private static readonly Vector4 NearBlack = Rgb(26, 26, 26);
    private static readonly Vector4 WindowBackground = Rgb(37, 40, 50);
    private static readonly Vector4 TextBoxBackground = Rgb(30, 33, 40);
    private static readonly Vector4 TextNormal = Rgb(224, 224, 224);
    private static readonly Vector4 TextDescription = Rgb(176, 176, 176);
    private static readonly Vector4 TextInstance = Rgb(180, 180, 180);
    private static readonly Vector4 TextInstructions = Rgb(150, 150, 150);
    private static readonly Vector4 TextHint = Rgb(140, 140, 140);
    private static readonly Vector4 TextVersion = Rgb(100, 100, 100);
    private static readonly Vector4 StatusOk = Rgb(100, 255, 100);
    private static readonly Vector4 StatusError = Rgb(255, 100, 100);

    // Anything after "##" is the window's id, which keeps other plugins from drawing into this window
    private const string WindowTitle = "Quest Tracker##QuestTracker-f006af62-5b8a-4298-9017-e4e588df4d7d";
    private static readonly Vector2 MinSize = new(450, 350);

    private volatile StatusLine _status = new("Waiting for quest data...", TextNormal);
    private volatile string _instanceText = "Current Area: Public Area";
    private volatile string _exportText = "Click 'Export CSV' to save your completed quests as a spreadsheet";
    private long _statusResetAtTicks;
    private volatile bool _exportInProgress;

    private readonly string _versionText;

    public QuestTrackerUI()
    {
        var version = typeof(Plugin).Assembly.GetName().Version;
        _versionText = $"v{version?.Major}.{version?.Minor}.{version?.Build}";
    }

    /// <summary>
    /// Draws the window. Returns false once the user has closed it.
    /// </summary>
    public bool Render()
    {
        var resetAt = Interlocked.Read(ref _statusResetAtTicks);
        if (resetAt != 0 && Environment.TickCount64 >= resetAt)
        {
            Interlocked.Exchange(ref _statusResetAtTicks, 0);
            RefreshQuestCount();
        }

        ImGui.SetNextWindowSize(MinSize, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(MinSize, new Vector2(float.MaxValue, float.MaxValue));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, WindowBackground);

        // The close (X) button clears this, which hands the window state back to Dungeon Helper
        var visible = true;
        var expanded = ImGui.Begin(WindowTitle, ref visible, ImGuiWindowFlags.NoCollapse);
        ImGui.PopStyleColor();
        if (!expanded)
        {
            ImGui.End();
            return visible;
        }
        ImGui.ClampWindowToBounds();

        // A null font keeps the current one and only changes its size
        ImGui.PushFont(new ImFontPtr(IntPtr.Zero), ImGui.GetStyle().FontSizeBase * 1.5f);
        CentredText("Quest Tracker for DDOQT", Gold);
        ImGui.PopFont();
        CentredText("Export your completed quests to qt.ddotools.xyz", TextDescription);
        ImGui.Spacing();

        var status = _status;
        CentredText(status.Text, status.Colour);
        CentredText("Sync buttons will open qt.ddotools.xyz in your browser", TextHint);
        ImGui.Spacing();

        DrawButtons();
        ImGui.Spacing();

        // The export box takes whatever height the footer lines and log switch leave
        var footerHeight = ImGui.GetTextLineHeightWithSpacing() * 4 + ImGui.GetFrameHeightWithSpacing();
        var boxHeight = Math.Max(60f, ImGui.GetContentRegionAvail().Y - footerHeight);
        var exportText = _exportText;
        ImGui.PushStyleColor(ImGuiCol.FrameBg, TextBoxBackground);
        ImGui.InputTextMultiline("##export", ref exportText, 4096, new Vector2(-1, boxHeight), ImGuiInputTextFlags.ReadOnly);
        ImGui.PopStyleColor();

        ImGui.PushStyleColor(ImGuiCol.Text, TextInstructions);
        ImGui.TextWrapped("Copy URL: generates a link you can share or bookmark to import your quests");
        ImGui.PopStyleColor();

        if (Plugin.Instance != null)
        {
            var loggingEnabled = Plugin.Instance.LoggingEnabled;
            if (ImGui.ToggleSwitch("##logging", "Save a log file (QuestTracker.log, for reporting problems)", ref loggingEnabled))
                Plugin.Instance.SetLoggingEnabled(loggingEnabled);
        }

        CentredText(_instanceText, TextInstance);

        // Version in the bottom-right corner
        var versionSize = ImGui.CalcTextSize(_versionText);
        ImGui.SetCursorPos(new Vector2(
            ImGui.GetWindowWidth() - versionSize.X - 6,
            ImGui.GetWindowHeight() - versionSize.Y - 4));
        ImGui.TextColored(TextVersion, _versionText);

        ImGui.End();
        return visible;
    }

    private void DrawButtons()
    {
        var buttons = new List<ButtonSpec>
        {
            new("Export CSV", Gold, NearBlack, OnExportCsvClick, _exportInProgress),
            new("Copy URL", SteelBlue, White, OnCopyUrlClick, false),
            new("Sync (Merge)", SeaGreen, White, () => SyncToDdoqt("merge"), false),
            new("Sync (Replace)", Red, White, () => SyncToDdoqt("replace"), false)
        };

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(15, 8));
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var framePadding = ImGui.GetStyle().FramePadding.X;
        var available = ImGui.GetContentRegionAvail().X;

        // Lay the buttons out in centred rows, wrapping when the window is too narrow for one row
        var rows = new List<List<ButtonSpec>> { new() };
        var rowWidth = 0f;
        foreach (var button in buttons)
        {
            button.Width = ImGui.CalcTextSize(button.Label).X + framePadding * 2;
            var widthWithButton = rowWidth == 0 ? button.Width : rowWidth + spacing + button.Width;
            if (widthWithButton > available && rows[^1].Count > 0)
            {
                rows.Add(new List<ButtonSpec>());
                widthWithButton = button.Width;
            }
            rows[^1].Add(button);
            rowWidth = widthWithButton;
        }

        foreach (var row in rows)
        {
            var width = 0f;
            foreach (var button in row)
                width += button.Width;
            width += spacing * (row.Count - 1);

            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (available - width) / 2));
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0) ImGui.SameLine();
                DrawButton(row[i]);
            }
        }

        ImGui.PopStyleVar();
    }

    private static void DrawButton(ButtonSpec button)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, button.Colour);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Lighten(button.Colour, 0.12f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Lighten(button.Colour, -0.12f));
        ImGui.PushStyleColor(ImGuiCol.Text, button.TextColour);
        ImGui.BeginDisabled(button.Disabled);

        if (ImGui.Button(button.Label))
            button.OnClick();

        ImGui.EndDisabled();
        ImGui.PopStyleColor(4);
    }

    private static void CentredText(string text, Vector4 colour)
    {
        var width = ImGui.CalcTextSize(text).X;
        var available = ImGui.GetContentRegionAvail().X;
        if (width < available)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (available - width) / 2);
        ImGui.TextColored(colour, text);
    }

    public void UpdateInstanceDisplay(string? questName)
    {
        var displayText = string.IsNullOrEmpty(questName) ? "Public Area" : questName;
        _instanceText = $"Current Area: {displayText}";
    }

    public void UpdateStatus(string message, bool isError = false)
    {
        _status = new StatusLine(message, isError ? StatusError : StatusOk);
    }

    public void ResetStatusAfterDelay(int delayMs)
    {
        Interlocked.Exchange(ref _statusResetAtTicks, Environment.TickCount64 + delayMs);
    }

    public void SetExportText(string text)
    {
        _exportText = text;
    }

    public void RefreshQuestCount()
    {
        if (Plugin.Instance == null) return;

        var count = Plugin.Instance.GetCompletedQuestCount();
        _status = new StatusLine($"Found {count} completed quests", TextNormal);
    }

    private void OnCopyUrlClick()
    {
        try
        {
            if (Plugin.Instance == null)
            {
                UpdateStatus("Plugin not initialized", isError: true);
                return;
            }

            var count = Plugin.Instance.GetCompletedQuestCount();
            if (count == 0)
            {
                UpdateStatus("No quest data - please log in to a character first", isError: true);
                return;
            }

            ImGui.SetClipboardText(Plugin.Instance.GetExportUrl());
            UpdateStatus("URL copied to clipboard!");
        }
        catch (Exception ex)
        {
            UpdateStatus($"Error: {ex.Message}", isError: true);
        }
    }

    private void OnExportCsvClick()
    {
        if (Plugin.Instance == null)
        {
            UpdateStatus("Plugin not initialized", isError: true);
            return;
        }

        _exportInProgress = true;
        UpdateStatus("Fetching quest data...");

        // Off the render thread: the save dialog is modal, and would otherwise freeze the game
        var thread = new Thread(ExportCsv) { IsBackground = true, Name = "QuestTracker.ExportCsv" };
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void ExportCsv()
    {
        try
        {
            var plugin = Plugin.Instance!;
            plugin.RefreshQuestData();

            var count = plugin.GetCompletedQuests().Count();
            if (count == 0)
            {
                UpdateStatus("No quest data - please log in to a character first", isError: true);
                SetExportText("Quest data loads when you log in to a character.");
                return;
            }

            var csv = plugin.GenerateCsv();

            var path = NativeSaveFileDialog.Show(
                "Export completed quests",
                "completed_quests.csv",
                "CSV files (*.csv)\0*.csv\0All files (*.*)\0*.*\0",
                "csv");

            if (path == null)
            {
                RefreshQuestCount();
                return;
            }

            // UTF-8 with BOM so Excel detects the encoding
            File.WriteAllText(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            SetExportText($"Saved {count} quests to:\n{path}");
            UpdateStatus($"Exported {count} quests to {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            UpdateStatus($"Error: {ex.Message}", isError: true);
        }
        finally
        {
            _exportInProgress = false;
        }
    }

    private void SyncToDdoqt(string action)
    {
        try
        {
            if (Plugin.Instance == null)
            {
                UpdateStatus("Plugin not initialized", isError: true);
                return;
            }

            var count = Plugin.Instance.GetCompletedQuestCount();
            if (count == 0)
            {
                UpdateStatus("No quest data - please log in to a character first", isError: true);
                return;
            }

            var url = Plugin.Instance.GetAutoSyncUrl(action);

            // Launching the browser can take a moment, which would stall the frame
            Task.Run(() =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error: {ex.Message}", isError: true);
                }
            });
            UpdateStatus($"Syncing to DDOQT ({action})...");
            ResetStatusAfterDelay(3000);
        }
        catch (Exception ex)
        {
            UpdateStatus($"Error: {ex.Message}", isError: true);
        }
    }

    private static Vector4 Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);

    private static Vector4 Lighten(Vector4 colour, float amount) => new(
        Math.Clamp(colour.X + amount, 0f, 1f),
        Math.Clamp(colour.Y + amount, 0f, 1f),
        Math.Clamp(colour.Z + amount, 0f, 1f),
        colour.W);

    private sealed class StatusLine
    {
        public StatusLine(string text, Vector4 colour)
        {
            Text = text;
            Colour = colour;
        }

        public string Text { get; }
        public Vector4 Colour { get; }
    }

    private sealed class ButtonSpec
    {
        public ButtonSpec(string label, Vector4 colour, Vector4 textColour, Action onClick, bool disabled)
        {
            Label = label;
            Colour = colour;
            TextColour = textColour;
            OnClick = onClick;
            Disabled = disabled;
        }

        public string Label { get; }
        public Vector4 Colour { get; }
        public Vector4 TextColour { get; }
        public Action OnClick { get; }
        public bool Disabled { get; }
        public float Width { get; set; }
    }
}
