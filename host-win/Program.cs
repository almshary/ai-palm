using System.Diagnostics;
using Microsoft.Win32;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NawahHost;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length >= 2 && args[0] == "--self-test")
        {
            try
            {
                using var form = new HostForm();
                _ = form.Handle;
                File.WriteAllText(args[1], JsonSerializer.Serialize(new
                {
                    ok = true,
                    title = form.Text,
                    width = form.ClientSize.Width,
                    height = form.ClientSize.Height,
                    rootControls = form.Controls.Count,
                    gpuVendor = form.DetectedGpu.Vendor,
                    gpuName = form.DetectedGpu.Name,
                    vramGb = form.DetectedGpu.VramGb,
                    deviceId = form.StableDeviceId,
                }));
            }
            catch (Exception ex)
            {
                File.WriteAllText(args[1], JsonSerializer.Serialize(new { ok = false, error = ex.ToString() }));
                Environment.ExitCode = 1;
            }
            return;
        }
        Application.Run(new HostForm());
    }
}

internal sealed class HostSettings
{
    public string DeviceId { get; set; } = "";
    public string ServerUrl { get; set; } = "https://nawah.almshary.site";
    public string ApiUrl { get; set; } = "http://127.0.0.1:1234/v1";
    public string OllamaUrl { get; set; } = ""; // Migration from the first prototype.
    public string DeviceName { get; set; } = Environment.MachineName;
    public string Mode { get; set; } = "openai";
    public string Model { get; set; } = "";
    public string Language { get; set; } = "en";
    public string ProtectedPlatformKey { get; set; } = "";
    [JsonIgnore]
    public string PlatformKey { get; set; } = "";
    [JsonIgnore]
    public string ApiKey { get; set; } = "";
}

internal sealed record GpuInfo(string Vendor, string Name, double? VramGb);
internal sealed record LocalizedText(string Arabic, string English);

internal sealed class HostForm : Form
{
    private static readonly Color Background = Color.FromArgb(11, 14, 13);
    private static readonly Color Panel = Color.FromArgb(23, 28, 26);
    private static readonly Color Field = Color.FromArgb(32, 38, 34);
    private static readonly Color TextColor = Color.FromArgb(242, 245, 239);
    private static readonly Color Muted = Color.FromArgb(153, 162, 155);
    private static readonly Color Accent = Color.FromArgb(229, 194, 118);
    private static readonly Color Danger = Color.FromArgb(255, 118, 95);
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string settingsPath;
    private readonly string deviceId;
    private readonly GpuInfo gpuInfo;
    internal GpuInfo DetectedGpu => gpuInfo;
    internal string StableDeviceId => deviceId;

    private readonly TextBox deviceName = MakeTextBox();
    private readonly TextBox serverUrl = MakeTextBox();
    private readonly TextBox platformKey = MakeTextBox(true);
    private readonly ComboBox language = MakeComboBox(false);
    private readonly ComboBox mode = MakeComboBox(false);
    private readonly TextBox modelApiUrl = MakeTextBox();
    private readonly TextBox apiKey = MakeTextBox(true);
    private readonly ComboBox model = MakeComboBox(true);
    private readonly Button refreshModels = MakeSecondaryButton("تحديث النماذج");
    private readonly Button connect = MakePrimaryButton("اتصال وبدء المشاركة");
    private readonly Button stop = MakeSecondaryButton("إيقاف");
    private readonly Label statusTitle = MakeLabel("غير متصل", 12, true, TextColor);
    private readonly Label statusDetail = MakeLabel("اضبط الإعدادات ثم اضغط اتصال", 9, false, Muted);
    private readonly Label logText = MakeLabel("لا توجد مهام بعد", 9, false, TextColor);
    private readonly Panel statusDot = new() { Width = 12, Height = 12, BackColor = Color.FromArgb(102, 112, 105), Margin = new Padding(8, 8, 0, 0) };
    private readonly Panel modelApiPanel = new() { Dock = DockStyle.Top, AutoSize = true, BackColor = Panel };
    private readonly NotifyIcon trayIcon = new();
    private readonly ToolStripMenuItem trayOpen = new("فتح نخلة AI");
    private readonly ToolStripMenuItem trayStart = new("بدء المشاركة");
    private readonly ToolStripMenuItem trayStop = new("إيقاف المشاركة");
    private readonly ToolStripMenuItem trayExit = new("خروج");
    private CancellationTokenSource? sharingCancellation;
    private string? hostId;
    private string? hostToken;
    private HostSettings activeSettings = new();
    private bool exitRequested;
    private bool trayTipShown;
    private string statusArabicTitle = "غير متصل";
    private string statusEnglishTitle = "Offline";
    private string statusArabicDetail = "اضبط الإعدادات ثم اضغط اتصال";
    private string statusEnglishDetail = "Review the settings, then select Connect";
    private string logArabic = "لا توجد مهام بعد";
    private string logEnglish = "No tasks yet";

    public HostForm()
    {
        settingsPath = Environment.GetEnvironmentVariable("NAWAH_SETTINGS_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nawah Host", "settings.json");
        gpuInfo = DetectGpu();
        var saved = LoadSettings();
        if (string.IsNullOrWhiteSpace(saved.DeviceId))
        {
            saved.DeviceId = Guid.NewGuid().ToString("N");
            SaveSettings(saved);
        }
        deviceId = saved.DeviceId;

        language.Items.AddRange(["العربية", "English"]);
        language.SelectedIndex = saved.Language == "en" ? 1 : 0;
        Text = L("نخلة AI — مشاركة نموذجك المحلي", "AI Palm — Share your local model");
        ClientSize = new Size(760, 800);
        MinimumSize = new Size(690, 700);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 10);
        RightToLeft = IsEnglish ? RightToLeft.No : RightToLeft.Yes;
        RightToLeftLayout = !IsEnglish;
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? CreateTrayIcon();

        deviceName.Text = saved.DeviceName;
        serverUrl.Text = saved.ServerUrl;
        platformKey.Text = saved.PlatformKey;
        modelApiUrl.Text = !string.IsNullOrWhiteSpace(saved.ApiUrl)
            ? saved.ApiUrl
            : (!string.IsNullOrWhiteSpace(saved.OllamaUrl) ? saved.OllamaUrl.TrimEnd('/') + "/v1" : "http://127.0.0.1:1234/v1");
        model.Text = saved.Model;
        SetModeItems();
        mode.SelectedIndex = saved.Mode is "openai" or "ollama" ? 1 : 0;

        BuildInterface();
        language.SelectedIndexChanged += (_, _) =>
        {
            ApplyLanguage();
            SaveSettings(CurrentSettings());
        };
        mode.SelectedIndexChanged += (_, _) => UpdateModeVisibility();
        refreshModels.Click += async (_, _) => await RefreshModelsAsync();
        connect.Click += async (_, _) => await StartSharingAsync();
        stop.Click += async (_, _) => await StopSharingAsync();
        InitializeTray();
        Resize += OnResize;
        FormClosing += OnFormClosing;
        UpdateModeVisibility();
        SetConnectedControls(false);
        ApplyLanguage();
    }

    private void BuildInterface()
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Background, Padding = new Padding(38, 28, 38, 28) };
        Controls.Add(scroll);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            BackColor = Background,
            RightToLeft = RightToLeft.Yes,
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(content);

        var header = new Panel { Height = 66, Dock = DockStyle.Top, BackColor = Background, Margin = new Padding(0, 0, 0, 18) };
        var mark = MakeLabel("AI", 15, true, Color.FromArgb(9, 32, 22));
        mark.BackColor = Accent;
        mark.TextAlign = ContentAlignment.MiddleCenter;
        mark.Size = new Size(46, 46);
        mark.Location = new Point(header.Width - 46, 4);
        mark.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        var heading = Localize(MakeLabel("نخلة AI للمضيف", 18, true, TextColor), "نخلة AI للمضيف", "AI Palm Host");
        heading.AutoSize = true;
        heading.Location = new Point(header.Width - 230, 3);
        heading.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        var subheading = Localize(MakeLabel("شارك نموذجك عندما تريد — وسيبقى بجوار الساعة", 9, false, Muted), "شارك نموذجك عندما تريد — وسيبقى بجوار الساعة", "Share your model when you choose — AI Palm stays in the system tray");
        subheading.AutoSize = true;
        subheading.Location = new Point(header.Width - 230, 38);
        subheading.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        header.Controls.AddRange([mark, heading, subheading]);
        content.Controls.Add(header);

        var statusCard = Card(82);
        var dotWrap = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 30, BackColor = Panel, FlowDirection = FlowDirection.TopDown, Padding = new Padding(0, 17, 0, 0) };
        dotWrap.Controls.Add(statusDot);
        var statusCopy = new Panel { Dock = DockStyle.Fill, BackColor = Panel, Padding = new Padding(12, 15, 12, 8) };
        statusTitle.Dock = DockStyle.Top;
        statusTitle.Height = 28;
        statusDetail.Dock = DockStyle.Top;
        statusDetail.Height = 24;
        statusCopy.Controls.Add(statusDetail);
        statusCopy.Controls.Add(statusTitle);
        statusCard.Controls.Add(statusCopy);
        statusCard.Controls.Add(dotWrap);
        statusCard.Margin = new Padding(0, 0, 0, 16);
        content.Controls.Add(statusCard);

        var formCard = Card(0);
        formCard.AutoSize = true;
        formCard.Padding = new Padding(24, 16, 24, 22);
        var form = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, BackColor = Panel };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField(form, "اللغة", "Language", language);
        AddField(form, "اسم الجهاز", "Device name", deviceName);
        AddField(form, "رابط منصة نخلة AI", "AI Palm API URL", serverUrl);
        AddField(form, "مفتاح ربط المنصة", "Platform connection key", platformKey);
        AddField(form, "طريقة التنفيذ", "Execution method", mode);

        var apiFields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, BackColor = Panel };
        apiFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField(apiFields, "عنوان OpenAI-compatible API الأساسي", "OpenAI-compatible API base URL", modelApiUrl);
        AddField(apiFields, "API Key — اختياري ولا يُحفظ", "API key — optional and never saved", apiKey);
        apiFields.Controls.Add(Localize(MakeFieldLabel("النموذج المتاح"), "النموذج المتاح", "Available model"));
        var modelRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 45, ColumnCount = 2, BackColor = Panel, Margin = new Padding(0, 0, 0, 2) };
        modelRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        modelRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        model.Dock = DockStyle.Fill;
        refreshModels.Dock = DockStyle.Fill;
        refreshModels.Margin = new Padding(8, 0, 0, 0);
        modelRow.Controls.Add(model, 0, 0);
        modelRow.Controls.Add(refreshModels, 1, 0);
        apiFields.Controls.Add(modelRow);
        modelApiPanel.Controls.Add(apiFields);
        form.Controls.Add(modelApiPanel);

        var gpuCard = new Panel { Dock = DockStyle.Top, Height = 112, BackColor = Color.FromArgb(17, 21, 18), Margin = new Padding(0, 16, 0, 0), Padding = new Padding(15, 9, 15, 8) };
        var gpuCaption = Localize(MakeLabel("البطاقة المكتشفة", 8, false, Muted), "البطاقة المكتشفة", "Detected GPU");
        gpuCaption.Dock = DockStyle.Top;
        gpuCaption.Height = 21;
        var gpuVendor = Localize(MakeLabel($"النوع: {gpuInfo.Vendor}", 9, false, TextColor), $"النوع: {gpuInfo.Vendor}", $"Vendor: {gpuInfo.Vendor}");
        gpuVendor.Dock = DockStyle.Top;
        gpuVendor.Height = 23;
        var gpuNameValue = Localize(MakeLabel($"الاسم: {gpuInfo.Name}", 10, true, TextColor), $"الاسم: {gpuInfo.Name}", $"Name: {gpuInfo.Name}");
        gpuNameValue.Dock = DockStyle.Top;
        gpuNameValue.Height = 25;
        var gpuMemory = Localize(MakeLabel($"VRAM: {(gpuInfo.VramGb is null ? "غير متاح" : $"{gpuInfo.VramGb:0} GB")}", 9, false, TextColor), $"VRAM: {(gpuInfo.VramGb is null ? "غير متاح" : $"{gpuInfo.VramGb:0} GB")}", $"VRAM: {(gpuInfo.VramGb is null ? "Unavailable" : $"{gpuInfo.VramGb:0} GB")}");
        gpuMemory.Dock = DockStyle.Fill;
        gpuCard.Controls.Add(gpuMemory);
        gpuCard.Controls.Add(gpuNameValue);
        gpuCard.Controls.Add(gpuVendor);
        gpuCard.Controls.Add(gpuCaption);
        form.Controls.Add(gpuCard);
        formCard.Controls.Add(form);
        formCard.Margin = new Padding(0, 0, 0, 16);
        content.Controls.Add(formCard);

        var buttons = new TableLayoutPanel { Dock = DockStyle.Top, Height = 52, ColumnCount = 2, BackColor = Background, Margin = new Padding(0, 0, 0, 14) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        connect.Dock = DockStyle.Fill;
        stop.Dock = DockStyle.Fill;
        stop.Enabled = false;
        stop.Margin = new Padding(8, 0, 0, 0);
        buttons.Controls.Add(connect, 0, 0);
        buttons.Controls.Add(stop, 1, 0);
        content.Controls.Add(buttons);

        var logCaption = Localize(MakeLabel("آخر نشاط", 8, false, Muted), "آخر نشاط", "Latest activity");
        logCaption.Height = 22;
        logCaption.Dock = DockStyle.Top;
        logText.Height = 44;
        logText.Dock = DockStyle.Top;
        content.Controls.Add(logCaption);
        content.Controls.Add(logText);
    }

    private void InitializeTray()
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Panel,
            ForeColor = TextColor,
            RightToLeft = RightToLeft.Yes,
            ShowImageMargin = false,
            Font = new Font("Segoe UI", 9),
        };
        menu.Items.AddRange([trayOpen, new ToolStripSeparator(), trayStart, trayStop, new ToolStripSeparator(), trayExit]);
        foreach (ToolStripItem item in menu.Items)
        {
            item.Padding = new Padding(7, 4, 7, 4);
            if (item is ToolStripSeparator separator)
            {
                separator.Paint += (_, eventArgs) =>
                    eventArgs.Graphics.DrawLine(new Pen(Color.FromArgb(54, 62, 57)), 5, separator.Height / 2, separator.Width - 5, separator.Height / 2);
            }
        }

        trayIcon.Icon = Icon is null ? CreateTrayIcon() : (Icon)Icon.Clone();
        trayIcon.Text = "نخلة AI — المشاركة متوقفة";
        trayIcon.ContextMenuStrip = menu;
        trayIcon.Visible = true;
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        trayOpen.Click += (_, _) => RestoreFromTray();
        trayStart.Click += async (_, _) =>
        {
            RestoreFromTray();
            await StartSharingAsync();
        };
        trayStop.Click += async (_, _) => await StopSharingAsync();
        trayExit.Click += async (_, _) => await ExitApplicationAsync();
    }

    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var backgroundBrush = new SolidBrush(Accent);
        graphics.FillRoundedRectangle(backgroundBrush, new Rectangle(2, 2, 28, 28), new Size(8, 8));
        using var font = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.FromArgb(16, 21, 12));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString("ن", font, textBrush, new RectangleF(1, 0, 30, 30), format);
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    private void OnResize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized) HideToTray();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        if (trayTipShown) return;
        trayTipShown = true;
        trayIcon.BalloonTipTitle = L("نخلة AI ما زالت تعمل", "AI Palm is still running");
        trayIcon.BalloonTipText = sharingCancellation is null
            ? L("يمكنك بدء المشاركة أو فتح التطبيق من الأيقونة بجوار الساعة.", "Start sharing or reopen the app from the system tray icon.")
            : L("المشاركة مستمرة. يمكنك إيقافها من قائمة الأيقونة بجوار الساعة.", "Sharing is active. You can stop it from the system tray menu.");
        trayIcon.ShowBalloonTip(3500);
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private async Task ExitApplicationAsync()
    {
        if (exitRequested) return;
        exitRequested = true;
        if (sharingCancellation is not null) await StopSharingAsync();
        trayIcon.Visible = false;
        Close();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static Panel Card(int height) => new()
    {
        Dock = DockStyle.Top,
        Height = height,
        BackColor = Panel,
        Padding = new Padding(1),
    };

    private void AddField(TableLayoutPanel parent, string arabicTitle, string englishTitle, Control input)
    {
        parent.Controls.Add(Localize(MakeFieldLabel(arabicTitle), arabicTitle, englishTitle));
        input.Dock = DockStyle.Top;
        input.Height = 43;
        input.Margin = new Padding(0, 0, 0, 2);
        parent.Controls.Add(input);
    }

    private bool IsEnglish => language.SelectedIndex == 1;

    private string L(string arabic, string english) => IsEnglish ? english : arabic;

    private T Localize<T>(T control, string arabic, string english) where T : Control
    {
        control.Tag = new LocalizedText(arabic, english);
        control.Text = L(arabic, english);
        return control;
    }

    private void SetModeItems()
    {
        var selected = mode.SelectedIndex;
        mode.Items.Clear();
        mode.Items.AddRange(IsEnglish
            ? ["Demo — no model", "OpenAI-compatible API"]
            : ["تجريبي — بدون نموذج", "OpenAI-compatible API"]);
        mode.SelectedIndex = selected < 0 ? 0 : selected;
    }

    private void ApplyLanguage()
    {
        Text = L("نخلة AI — مشاركة نموذجك المحلي", "AI Palm — Share your local model");
        RightToLeft = IsEnglish ? RightToLeft.No : RightToLeft.Yes;
        RightToLeftLayout = !IsEnglish;
        ApplyLanguageToControls(Controls);
        var modeIndex = mode.SelectedIndex;
        SetModeItems();
        mode.SelectedIndex = modeIndex;
        refreshModels.Text = L("تحديث النماذج", "Refresh models");
        connect.Text = L("اتصال وبدء المشاركة", "Connect and start sharing");
        stop.Text = L("إيقاف", "Stop");
        statusTitle.Text = L(statusArabicTitle, statusEnglishTitle);
        statusDetail.Text = L(statusArabicDetail, statusEnglishDetail);
        logText.Text = L(logArabic, logEnglish);
        trayOpen.Text = L("فتح نخلة AI", "Open AI Palm");
        trayStart.Text = L("بدء المشاركة", "Start sharing");
        trayStop.Text = L("إيقاف المشاركة", "Stop sharing");
        trayExit.Text = L("خروج", "Exit");
        if (trayIcon.ContextMenuStrip is not null)
            trayIcon.ContextMenuStrip.RightToLeft = IsEnglish ? RightToLeft.No : RightToLeft.Yes;
        trayIcon.Text = L("نخلة AI — تطبيق المضيف", "AI Palm — Host app");
    }

    private void ApplyLanguageToControls(Control.ControlCollection controls)
    {
        foreach (Control control in controls)
        {
            control.RightToLeft = IsEnglish ? RightToLeft.No : RightToLeft.Yes;
            if (control.Tag is LocalizedText text) control.Text = L(text.Arabic, text.English);
            if (control is TextBox box) box.TextAlign = IsEnglish ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            else if (control is Label label && label.Text != "ن") label.TextAlign = IsEnglish ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleRight;
            if (control.HasChildren) ApplyLanguageToControls(control.Controls);
        }
    }

    private void SetLog(string arabic, string english)
    {
        logArabic = arabic;
        logEnglish = english;
        logText.Text = L(arabic, english);
    }

    private static Label MakeFieldLabel(string text)
    {
        var label = MakeLabel(text, 9, false, Muted);
        label.Dock = DockStyle.Top;
        label.Height = 32;
        label.Padding = new Padding(0, 10, 0, 0);
        return label;
    }

    private static TextBox MakeTextBox(bool password = false) => new()
    {
        BackColor = Field,
        ForeColor = TextColor,
        BorderStyle = BorderStyle.FixedSingle,
        TextAlign = HorizontalAlignment.Right,
        Font = new Font("Segoe UI", 10),
        UseSystemPasswordChar = password,
    };

    private static ComboBox MakeComboBox(bool editable) => new()
    {
        BackColor = Field,
        ForeColor = TextColor,
        FlatStyle = FlatStyle.Flat,
        DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList,
        Font = new Font("Segoe UI", 10),
    };

    private static Label MakeLabel(string text, float size, bool bold, Color color) => new()
    {
        Text = text,
        ForeColor = color,
        BackColor = Color.Transparent,
        TextAlign = ContentAlignment.MiddleRight,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        AutoEllipsis = true,
    };

    private static Button MakePrimaryButton(string text) => new()
    {
        Text = text,
        BackColor = Accent,
        ForeColor = Color.FromArgb(16, 21, 12),
        FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 10, FontStyle.Bold),
        Cursor = Cursors.Hand,
        UseVisualStyleBackColor = false,
    };

    private static Button MakeSecondaryButton(string text)
    {
        var button = new Button
        {
            Text = text,
            BackColor = Field,
            ForeColor = TextColor,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(55, 63, 58);
        return button;
    }

    private void UpdateModeVisibility() => modelApiPanel.Visible = mode.SelectedIndex == 1;

    private HostSettings CurrentSettings() => new()
    {
        DeviceId = deviceId,
        DeviceName = deviceName.Text.Trim(),
        ServerUrl = serverUrl.Text.Trim().TrimEnd('/'),
        PlatformKey = platformKey.Text,
        ApiUrl = modelApiUrl.Text.Trim().TrimEnd('/'),
        ApiKey = apiKey.Text,
        Language = IsEnglish ? "en" : "ar",
        Mode = mode.SelectedIndex == 1 ? "openai" : "demo",
        Model = mode.SelectedIndex == 1 ? model.Text.Trim() : "demo-model",
    };

    private async Task RefreshModelsAsync()
    {
        refreshModels.Enabled = false;
        refreshModels.Text = L("جاري البحث...", "Searching...");
        try
        {
            var root = await GetOpenAiJsonAsync(ApiEndpoint(modelApiUrl.Text, "models"), apiKey.Text, CancellationToken.None);
            var names = root["data"]?.AsArray()
                .Select(item => item?["id"]?.GetValue<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .ToArray() ?? [];
            model.Items.Clear();
            model.Items.AddRange(names);
            if (names.Length > 0 && !names.Contains(model.Text)) model.Text = names[0];
            SetLog($"عُثر على {names.Length} نموذج محلي", $"Found {names.Length} local model(s)");
        }
        catch
        {
            SetLog("تعذر قراءة النماذج. تحقق من عنوان API والمفتاح.", "Could not load models. Check the API URL and key.");
        }
        finally
        {
            refreshModels.Enabled = true;
            refreshModels.Text = L("تحديث النماذج", "Refresh models");
        }
    }

    private async Task StartSharingAsync()
    {
        activeSettings = CurrentSettings();
        if (string.IsNullOrWhiteSpace(activeSettings.ServerUrl) || string.IsNullOrWhiteSpace(activeSettings.DeviceName) || string.IsNullOrWhiteSpace(activeSettings.Model))
        {
            MessageBox.Show(this, L("أكمل رابط المنصة واسم الجهاز والنموذج.", "Complete the platform URL, device name, and model."), L("بيانات ناقصة", "Missing information"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (activeSettings.Mode == "openai" &&
            (!Uri.TryCreate(activeSettings.ApiUrl, UriKind.Absolute, out var modelUri) || (modelUri.Scheme != "http" && modelUri.Scheme != "https")))
        {
            MessageBox.Show(this, L("أدخل عنوان OpenAI-compatible API صحيحًا، مثل http://127.0.0.1:1234/v1", "Enter a valid OpenAI-compatible API URL, such as http://127.0.0.1:1234/v1"), L("رابط غير صحيح", "Invalid URL"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!Uri.TryCreate(activeSettings.ServerUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            MessageBox.Show(this, L("يجب أن يبدأ رابط المنصة بـ http:// أو https://", "The platform URL must start with http:// or https://"), L("رابط غير صحيح", "Invalid URL"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SaveSettings(activeSettings);
        SetConnectedControls(true);
        SetStatus("جاري الاتصال...", "Connecting...", "نتحقق من خادم المنصة", "Checking the platform server", Color.Goldenrod);
        sharingCancellation = new CancellationTokenSource();
        try
        {
            var registration = await PostJsonAsync(
                $"{activeSettings.ServerUrl}/api/hosts/register",
                new
                {
                    host_id = activeSettings.DeviceId,
                    name = activeSettings.DeviceName,
                    gpu = gpuInfo.Name,
                    gpu_vendor = gpuInfo.Vendor,
                    vram_gb = gpuInfo.VramGb,
                    mode = activeSettings.Mode,
                    model = activeSettings.Model,
                    capabilities = new[] { "chat" },
                    enabled = true,
                },
                null,
                sharingCancellation.Token,
                registrationKey: activeSettings.PlatformKey);
            hostId = registration["host_id"]?.GetValue<string>();
            hostToken = registration["token"]?.GetValue<string>();
            if (hostId is null || hostToken is null) throw new InvalidOperationException(L("لم يُرجع الخادم بيانات المضيف", "The server did not return host credentials"));
            SetStatus("متصل — المشاركة مفعلة", "Connected — sharing is active", $"النموذج: {activeSettings.Model}", $"Model: {activeSettings.Model}", Accent);
            SetLog($"تم الاتصال. معرّف الجهاز: {hostId}", $"Connected. Device ID: {hostId}");
            _ = SharingLoopAsync(sharingCancellation.Token);
        }
        catch (Exception ex)
        {
            SetStatus("تعذر الاتصال", "Connection failed", FriendlyError(ex), FriendlyError(ex), Danger);
            SetLog("تحقق من تشغيل خادم المنصة وصحة الرابط.", "Check that the platform server is running and the URL is correct.");
            SetConnectedControls(false);
            sharingCancellation?.Dispose();
            sharingCancellation = null;
        }
    }

    private async Task SharingLoopAsync(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await PostJsonAsync($"{activeSettings.ServerUrl}/api/hosts/{hostId}/heartbeat", new { enabled = true }, hostToken, cancellation);
                var response = await GetJsonAsync($"{activeSettings.ServerUrl}/api/hosts/{hostId}/jobs/next", hostToken, cancellation);
                var job = response["job"] as JsonObject;
                if (job is null)
                {
                    await Task.Delay(1500, cancellation);
                    continue;
                }
                var jobId = job["id"]?.GetValue<string>() ?? "unknown";
                var prompt = job["prompt"]?.GetValue<string>() ?? "";
                SafeUi(() =>
                {
                    SetStatus("يتم تنفيذ مهمة الآن", "Running a task", $"المهمة: {jobId}", $"Task: {jobId}", Color.Goldenrod);
                    SetLog($"بدأ تنفيذ المهمة {jobId}", $"Started task {jobId}");
                });
                try
                {
                    var result = await RunTaskWithHeartbeatAsync(prompt, jobId, cancellation);
                    await PostJsonAsync($"{activeSettings.ServerUrl}/api/jobs/{jobId}/complete", new { result }, hostToken, cancellation);
                    SafeUi(() =>
                    {
                        SetStatus("متصل — جاهز لمهمة جديدة", "Connected — ready for a new task", $"النموذج: {activeSettings.Model}", $"Model: {activeSettings.Model}", Accent);
                        SetLog($"اكتملت المهمة {jobId}", $"Completed task {jobId}");
                    });
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    try { await PostJsonAsync($"{activeSettings.ServerUrl}/api/jobs/{jobId}/fail", new { error = FriendlyError(ex) }, hostToken, cancellation); } catch { }
                    SafeUi(() => SetLog($"فشلت المهمة: {FriendlyError(ex)}", $"Task failed: {FriendlyError(ex)}"));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SafeUi(() =>
            {
                SetStatus("انقطع الاتصال", "Connection lost", FriendlyError(ex), FriendlyError(ex), Danger);
                SetLog("أوقف المشاركة ثم أعد الاتصال.", "Stop sharing, then reconnect.");
            });
        }
    }

    private async Task<string> RunOpenAiCompatibleAsync(string prompt, string jobId, CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint(activeSettings.ApiUrl, "chat/completions"));
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = activeSettings.Model,
                stream = true,
                messages = new[] { new { role = "user", content = prompt } },
            }), Encoding.UTF8, "application/json");
            if (!string.IsNullOrWhiteSpace(activeSettings.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", activeSettings.ApiKey);

            using var timeoutCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeoutCancellation.Token);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync(linked.Token);
                throw new HttpRequestException(ReadApiError(errorText));
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var payloadText = await response.Content.ReadAsStringAsync(linked.Token);
                var payload = JsonNode.Parse(payloadText)?.AsObject() ?? new JsonObject();
                var fullText = payload["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? L("لم يُرجع النموذج إجابة", "The model returned no answer");
                var usage = CreateUsage(payload["usage"] as JsonObject, prompt, fullText, 0);
                await PublishStreamAsync(jobId, fullText, usage, linked.Token);
                return fullText;
            }

            var fullResult = new StringBuilder();
            var pending = new StringBuilder();
            JsonObject? exactUsage = null;
            var timer = Stopwatch.StartNew();
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            using var reader = new StreamReader(stream);
            while (true)
            {
                var line = await reader.ReadLineAsync(linked.Token);
                if (line is null) break;
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                var data = line[5..].Trim();
                if (data.Length == 0 || data == "[DONE]") continue;
                JsonObject? chunk;
                try { chunk = JsonNode.Parse(data)?.AsObject(); }
                catch (JsonException) { continue; }
                var delta = chunk?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(delta))
                {
                    fullResult.Append(delta);
                    pending.Append(delta);
                }
                if (chunk?["usage"] is JsonObject returnedUsage) exactUsage = returnedUsage;

                if (pending.Length >= 18)
                {
                    var usage = CreateUsage(null, prompt, fullResult.ToString(), timer.ElapsedMilliseconds);
                    await PublishStreamAsync(jobId, pending.ToString(), usage, linked.Token);
                    pending.Clear();
                }
            }
            if (pending.Length > 0)
                await PublishStreamAsync(jobId, pending.ToString(), CreateUsage(null, prompt, fullResult.ToString(), timer.ElapsedMilliseconds), linked.Token);

            var finalText = fullResult.ToString();
            var finalUsage = CreateUsage(exactUsage, prompt, finalText, timer.ElapsedMilliseconds);
            await PublishStreamAsync(jobId, "", finalUsage, linked.Token);
            return string.IsNullOrEmpty(finalText) ? L("لم يُرجع النموذج إجابة", "The model returned no answer") : finalText;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException(L("انتهت مهلة انتظار خادم النموذج بعد 30 دقيقة", "The model server timed out after 30 minutes"));
        }
    }

    private async Task<string> RunTaskWithHeartbeatAsync(string prompt, string jobId, CancellationToken cancellation)
    {
        var inference = activeSettings.Mode == "openai"
            ? RunOpenAiCompatibleAsync(prompt, jobId, cancellation)
            : RunDemoAsync(prompt, jobId, cancellation);
        while (!inference.IsCompleted)
        {
            var completed = await Task.WhenAny(inference, Task.Delay(TimeSpan.FromSeconds(5), cancellation));
            if (completed == inference) break;
            await PostJsonAsync(
                $"{activeSettings.ServerUrl}/api/hosts/{hostId}/heartbeat",
                new { enabled = true },
                hostToken,
                cancellation,
                TimeSpan.FromSeconds(15));
        }
        return await inference;
    }

    private async Task<string> RunDemoAsync(string prompt, string jobId, CancellationToken cancellation)
    {
        var result = L($"إجابة تجريبية من تطبيق نخلة AI للمضيف.\n\nالطلب: {prompt}\n\nالنموذج المعلن: demo-model", $"Demo response from the AI Palm Host app.\n\nRequest: {prompt}\n\nPublished model: demo-model");
        var words = result.Split(' ');
        var accumulated = new StringBuilder();
        var timer = Stopwatch.StartNew();
        foreach (var word in words)
        {
            var delta = (accumulated.Length == 0 ? "" : " ") + word;
            accumulated.Append(delta);
            await PublishStreamAsync(jobId, delta, CreateUsage(null, prompt, accumulated.ToString(), timer.ElapsedMilliseconds), cancellation);
            await Task.Delay(70, cancellation);
        }
        return result;
    }

    private async Task PublishStreamAsync(string jobId, string delta, JsonObject usage, CancellationToken cancellation)
    {
        await PostJsonAsync(
            $"{activeSettings.ServerUrl}/api/jobs/{jobId}/stream",
            new { delta, usage },
            hostToken,
            cancellation,
            TimeSpan.FromSeconds(15));
    }

    private static JsonObject CreateUsage(JsonObject? exact, string prompt, string result, long elapsedMilliseconds)
    {
        var promptTokens = exact?["prompt_tokens"]?.GetValue<int?>() ?? EstimateTokens(prompt);
        var completionTokens = exact?["completion_tokens"]?.GetValue<int?>() ?? EstimateTokens(result);
        var totalTokens = exact?["total_tokens"]?.GetValue<int?>() ?? promptTokens + completionTokens;
        var seconds = Math.Max(elapsedMilliseconds / 1000d, 0.001);
        return new JsonObject
        {
            ["prompt_tokens"] = promptTokens,
            ["completion_tokens"] = completionTokens,
            ["total_tokens"] = totalTokens,
            ["tokens_per_second"] = elapsedMilliseconds > 0 ? Math.Round(completionTokens / seconds, 1) : null,
            ["estimated"] = exact is null,
        };
    }

    private static int EstimateTokens(string text) => string.IsNullOrWhiteSpace(text) ? 0 : Math.Max(1, (int)Math.Ceiling(text.Length / 3.2));

    private async Task StopSharingAsync()
    {
        SetStatus("جاري الإيقاف...", "Stopping...", "لن تُسند مهام جديدة إلى هذا الجهاز", "No new tasks will be assigned to this device", Color.Goldenrod);
        try
        {
            if (hostId is not null && hostToken is not null)
                await PostJsonAsync($"{activeSettings.ServerUrl}/api/hosts/{hostId}/heartbeat", new { enabled = false }, hostToken, CancellationToken.None);
        }
        catch { }
        sharingCancellation?.Cancel();
        sharingCancellation?.Dispose();
        sharingCancellation = null;
        hostId = null;
        hostToken = null;
        SetConnectedControls(false);
        SetStatus("غير متصل", "Offline", "تم إيقاف المشاركة", "Sharing has stopped", Color.FromArgb(102, 112, 105));
        SetLog("المشاركة متوقفة", "Sharing is stopped");
    }

    private async Task<JsonObject> GetJsonAsync(string url, string? token, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null) request.Headers.Add("X-Host-Token", token);
        using var timeoutCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeoutCancellation.Token);
        using var response = await http.SendAsync(request, linked.Token);
        var text = await response.Content.ReadAsStringAsync(linked.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(ReadApiError(text));
        return JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
    }

    private async Task<JsonObject> GetOpenAiJsonAsync(string url, string apiKeyValue, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(apiKeyValue)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKeyValue);
        using var timeoutCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeoutCancellation.Token);
        using var response = await http.SendAsync(request, linked.Token);
        var text = await response.Content.ReadAsStringAsync(linked.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(ReadApiError(text));
        return JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
    }

    private async Task<JsonObject> PostOpenAiJsonAsync(string url, object body, string apiKeyValue, CancellationToken cancellation, TimeSpan timeout)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(apiKeyValue)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKeyValue);
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeoutCancellation.Token);
        using var response = await http.SendAsync(request, linked.Token);
        var text = await response.Content.ReadAsStringAsync(linked.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(ReadApiError(text));
        return JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
    }

    private static string ApiEndpoint(string baseUrl, string endpoint) => $"{baseUrl.Trim().TrimEnd('/')}/{endpoint.TrimStart('/')}";

    private async Task<JsonObject> PostJsonAsync(
        string url,
        object body,
        string? token,
        CancellationToken cancellation,
        TimeSpan? timeout = null,
        string? registrationKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        if (token is not null) request.Headers.Add("X-Host-Token", token);
        if (!string.IsNullOrWhiteSpace(registrationKey)) request.Headers.Add("X-Registration-Key", registrationKey);
        using var timeoutCancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeoutCancellation.Token);
        using var response = await http.SendAsync(request, linked.Token);
        var text = await response.Content.ReadAsStringAsync(linked.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(ReadApiError(text));
        return JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
    }

    private string ReadApiError(string json)
    {
        try { return JsonNode.Parse(json)?["error"]?.GetValue<string>() ?? L("رفض الخادم الطلب", "The server rejected the request"); }
        catch { return L("رفض الخادم الطلب", "The server rejected the request"); }
    }

    private string FriendlyError(Exception ex) => ex switch
    {
        HttpRequestException => ex.Message,
        TaskCanceledException => L("انتهت مهلة الاتصال", "The connection timed out"),
        _ => ex.Message,
    };

    private void SetConnectedControls(bool connectedState)
    {
        connect.Enabled = !connectedState;
        stop.Enabled = connectedState;
        deviceName.Enabled = !connectedState;
        serverUrl.Enabled = !connectedState;
        platformKey.Enabled = !connectedState;
        mode.Enabled = !connectedState;
        modelApiUrl.Enabled = !connectedState;
        apiKey.Enabled = !connectedState;
        model.Enabled = !connectedState;
        refreshModels.Enabled = !connectedState;
        trayStart.Enabled = !connectedState;
        trayStop.Enabled = connectedState;
    }

    private void SetStatus(string arabicTitle, string englishTitle, string arabicDetail, string englishDetail, Color color)
    {
        statusArabicTitle = arabicTitle;
        statusEnglishTitle = englishTitle;
        statusArabicDetail = arabicDetail;
        statusEnglishDetail = englishDetail;
        var title = L(arabicTitle, englishTitle);
        statusTitle.Text = title;
        statusDetail.Text = L(arabicDetail, englishDetail);
        statusDot.BackColor = color;
        var trayText = $"{L("نخلة AI", "AI Palm")} — {title}";
        trayIcon.Text = trayText.Length > 63 ? trayText[..63] : trayText;
    }

    private void SafeUi(Action action)
    {
        if (!IsDisposed && IsHandleCreated) BeginInvoke(action);
    }

    private HostSettings LoadSettings()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(settingsPath)) ?? new HostSettings();
            settings.PlatformKey = UnprotectSecret(settings.ProtectedPlatformKey);
            return settings;
        }
        catch { return new HostSettings(); }
    }

    private void SaveSettings(HostSettings settings)
    {
        try
        {
            settings.ProtectedPlatformKey = ProtectSecret(settings.PlatformKey);
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static string ProtectSecret(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try
        {
            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        catch { return ""; }
    }

    private static string UnprotectSecret(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try
        {
            var decrypted = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch { return ""; }
    }

    private static GpuInfo DetectGpu()
    {
        var nvidia = DetectNvidiaGpu();
        var detected = nvidia ?? DetectGpuFromRegistry() ?? new GpuInfo("غير معروف", "لم يتم اكتشاف بطاقة رسومية", null);
        return detected with
        {
            VramGb = detected.VramGb is null ? null : Math.Round(detected.VramGb.Value, MidpointRounding.AwayFromZero),
        };
    }

    private static GpuInfo? DetectNvidiaGpu()
    {
        var executables = new[]
        {
            "nvidia-smi.exe",
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        };
        foreach (var executable in executables.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "--query-gpu=name,memory.total --format=csv,noheader,nounits",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (process is null) continue;
                var output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(5000))
                {
                    process.Kill(true);
                    continue;
                }
                var detected = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(ParseNvidiaLine)
                    .Where(item => item is not null)
                    .Cast<GpuInfo>()
                    .OrderByDescending(item => item.VramGb ?? 0)
                    .FirstOrDefault();
                if (detected is not null) return detected;
            }
            catch { }
        }
        return null;
    }

    private static GpuInfo? ParseNvidiaLine(string line)
    {
        var parts = line.Split(',');
        if (parts.Length < 2 || !double.TryParse(parts[^1].Trim(), out var memoryMb)) return null;
        return new GpuInfo("NVIDIA", string.Join(",", parts[..^1]).Trim(), Math.Round(memoryMb / 1024, 1));
    }

    private static GpuInfo? DetectGpuFromRegistry()
    {
        var detected = new List<GpuInfo>();
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var video = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video");
            if (video is null) return null;
            foreach (var adapterId in video.GetSubKeyNames())
            {
                using var adapter = video.OpenSubKey(adapterId);
                if (adapter is null) continue;
                foreach (var instanceName in adapter.GetSubKeyNames().Where(name => name.Length > 0 && name.All(char.IsDigit)))
                {
                    using var instance = adapter.OpenSubKey(instanceName);
                    if (instance is null) continue;
                    var name = ReadRegistryText(instance.GetValue("HardwareInformation.AdapterString"));
                    if (string.IsNullOrWhiteSpace(name) || name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase)) continue;
                    detected.Add(new GpuInfo(DetectVendor(name), name.Trim(), ReadRegistryVram(instance)));
                }
            }
        }
        catch { }
        return detected
            .OrderByDescending(item => item.VramGb ?? 0)
            .ThenBy(item => item.Vendor is "Intel" or "غير معروف" ? 1 : 0)
            .FirstOrDefault();
    }

    private static string ReadRegistryText(object? value)
    {
        var text = value switch
        {
            string plain => plain,
            byte[] bytes => Encoding.Unicode.GetString(bytes, 0, bytes.Length - (bytes.Length % 2)),
            _ => "",
        };
        return new string(text.Where(character => !char.IsControl(character) && character != '\uFFFD').ToArray()).Trim();
    }

    private static double? ReadRegistryVram(RegistryKey key)
    {
        foreach (var name in new[] { "HardwareInformation.qwMemorySize", "HardwareInformation.MemorySize" })
        {
            var value = key.GetValue(name);
            ulong bytes = value switch
            {
                long number when number > 0 => (ulong)number,
                int number when number != 0 => (uint)number,
                byte[] raw when raw.Length >= 8 => BitConverter.ToUInt64(raw, 0),
                byte[] raw when raw.Length >= 4 => BitConverter.ToUInt32(raw, 0),
                _ => 0,
            };
            if (bytes > 0) return Math.Round(bytes / 1024d / 1024d / 1024d, 1);
        }
        return null;
    }

    private static string DetectVendor(string name)
    {
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) || name.Contains("Quadro", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) return "AMD";
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase) || name.Contains("Arc", StringComparison.OrdinalIgnoreCase) || name.Contains("Iris", StringComparison.OrdinalIgnoreCase)) return "Intel";
        return "غير معروف";
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (exitRequested)
        {
            trayIcon.Visible = false;
            return;
        }
        if (e.CloseReason == CloseReason.WindowsShutDown)
        {
            exitRequested = true;
            trayIcon.Visible = false;
            return;
        }
        e.Cancel = true;
        HideToTray();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) trayIcon.Dispose();
        base.Dispose(disposing);
    }
}
