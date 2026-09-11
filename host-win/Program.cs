using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NawahHost;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length >= 3 && args[0] == "--detect-engine")
        {
            try
            {
                using var form = new HostForm();
                var detection = Task.Run(() => form.DetectEngineForSelfTestAsync(args[1])).GetAwaiter().GetResult();
                File.WriteAllText(args[2], JsonSerializer.Serialize(new
                {
                    kind = detection.Kind.ToString(),
                    detection.Name,
                    detection.Version,
                    detection.ServerRoot,
                }));
            }
            catch (Exception ex)
            {
                File.WriteAllText(args[2], JsonSerializer.Serialize(new { error = ex.ToString() }));
                Environment.ExitCode = 1;
            }
            return;
        }
        if (args.Length >= 2 && args[0] == "--self-test")
        {
            try
            {
                using var form = new HostForm();
                _ = form.Handle;
                var languageArgument = Array.IndexOf(args, "--language");
                if (languageArgument >= 0 && languageArgument + 1 < args.Length) form.SetSelfTestLanguage(args[languageArgument + 1]);
                if (Array.IndexOf(args, "--connected") >= 0) form.SetSelfTestConnected(true);
                var pageArgument = Array.IndexOf(args, "--page");
                if (pageArgument >= 0 && pageArgument + 1 < args.Length) form.ShowSelfTestPage(args[pageArgument + 1]);
                if (Array.IndexOf(args, "--sample-chat") >= 0) form.PopulateSelfTestChat();
                if (Array.IndexOf(args, "--sample-arabic-chat") >= 0) form.PopulateSelfTestArabicChat();
                form.Show();
                Application.DoEvents();
                if (args.Length >= 4 && args[2] == "--screenshot")
                {
                    for (var frame = 0; frame < 12; frame++)
                    {
                        Thread.Sleep(50);
                        Application.DoEvents();
                    }
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    bitmap.Save(args[3]);
                }
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
                    layout = form.GetSelfTestLayout(),
                    rtlAudit = form.GetSelfTestRtlIssues(),
                    quickAction = form.GetSelfTestQuickAction(),
                    chatLayout = form.GetSelfTestChatLayout(),
                    usageOnlyChunkSafe = HostForm.IsUsageOnlyChunkHandledForSelfTest(),
                    reasoningStreamSafe = HostForm.IsReasoningStreamHandledForSelfTest(),
                    manualScrollLockSafe = form.IsManualScrollLockHandledForSelfTest(),
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

internal enum LocalEngineKind
{
    Unknown,
    Ollama,
    LmStudioV1,
    LmStudioLegacy,
    LlamaCpp,
}

internal sealed record EngineDetection(LocalEngineKind Kind, string Name, string Version, string ServerRoot)
{
    internal static EngineDetection Unknown(string serverRoot) => new(LocalEngineKind.Unknown, "OpenAI-compatible", "", serverRoot);
}

internal sealed class ReasoningContentAccumulator
{
    private static readonly (string Open, string Close)[] Tags =
    [
        ("<think>", "</think>"),
        ("<analysis>", "</analysis>"),
        ("<reasoning>", "</reasoning>"),
    ];

    private readonly StringBuilder raw = new();
    private int emittedAnswer;
    private int emittedThinking;

    internal (string Answer, string Thinking) Append(string value, bool flush = false)
    {
        raw.Append(value);
        var split = Split(raw.ToString(), flush);
        var answer = split.Answer.Length > emittedAnswer ? split.Answer[emittedAnswer..] : "";
        var thinking = split.Thinking.Length > emittedThinking ? split.Thinking[emittedThinking..] : "";
        emittedAnswer = split.Answer.Length;
        emittedThinking = split.Thinking.Length;
        return (answer, thinking);
    }

    internal (string Answer, string Thinking) Flush() => Append("", true);

    private static (string Answer, string Thinking) Split(string value, bool flush)
    {
        var start = 0;
        while (start < value.Length && char.IsWhiteSpace(value[start])) start++;
        var remainder = value[start..];
        var tag = Tags.FirstOrDefault(candidate => remainder.StartsWith(candidate.Open, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(tag.Open))
        {
            if (!flush && Tags.Any(candidate => candidate.Open.StartsWith(remainder, StringComparison.OrdinalIgnoreCase)))
                return ("", "");
            return (value, "");
        }

        var thinkingStart = start + tag.Open.Length;
        var close = value.IndexOf(tag.Close, thinkingStart, StringComparison.OrdinalIgnoreCase);
        if (close >= 0)
            return (value[(close + tag.Close.Length)..].TrimStart(), value[thinkingStart..close].Trim());

        var thinking = value[thinkingStart..];
        if (!flush)
        {
            var held = LongestTagPrefixSuffix(thinking, tag.Close);
            if (held > 0) thinking = thinking[..^held];
        }
        return ("", thinking.TrimStart());
    }

    private static int LongestTagPrefixSuffix(string value, string tag)
    {
        for (var length = Math.Min(value.Length, tag.Length - 1); length > 0; length--)
            if (value.EndsWith(tag[..length], StringComparison.OrdinalIgnoreCase)) return length;
        return 0;
    }
}

internal sealed class HostForm : Form
{
    private const int WmSetRedraw = 0x000B;
    private const int WsExLayoutRtl = 0x00400000;
    private const int WsExRtlReading = 0x00002000;
    private const int WsExNoInheritLayout = 0x00100000;
    private static readonly Color Background = Color.FromArgb(11, 14, 13);
    private static readonly Color Panel = Color.FromArgb(23, 28, 26);
    private static readonly Color Field = Color.FromArgb(32, 38, 34);
    private static readonly Color TextColor = Color.FromArgb(242, 245, 239);
    private static readonly Color Muted = Color.FromArgb(153, 162, 155);
    private static readonly Color Accent = Color.FromArgb(229, 194, 118);
    private static readonly Color Danger = Color.FromArgb(255, 118, 95);
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle &= ~(WsExLayoutRtl | WsExRtlReading);
            parameters.ExStyle |= WsExNoInheritLayout;
            return parameters;
        }
    }
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
    private readonly Label engineDetectionStatus = MakeLabel("لم يتم فحص المحرك بعد", 9, true, Muted);
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
    private readonly Panel pageHost = new() { Dock = DockStyle.Fill, BackColor = Background };
    private readonly FlowLayoutPanel hostsFlow = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = true,
        BackColor = Background,
        Padding = new Padding(0, 4, 8, 12),
    };
    private readonly Label connectedMetric = MakeLabel("0", 22, true, TextColor);
    private readonly Label modelsMetric = MakeLabel("0", 22, true, TextColor);
    private readonly Label registeredMetric = MakeLabel("0", 22, true, TextColor);
    private readonly Label exploreState = MakeLabel("جاري قراءة الشبكة...", 9, false, Muted);
    private readonly Button navExplore = MakeNavButton("استكشاف", "Explore");
    private readonly Button navChat = MakeNavButton("المحادثة", "Chat");
    private readonly Button navShare = MakeNavButton("مشاركة جهازي", "Share my device");
    private readonly Button refreshNetwork = MakeSecondaryButton("تحديث الشبكة");
    private readonly TextBox networkSearch = MakeTextBox();
    private readonly CheckBox quickShareToggle = new()
    {
        Appearance = Appearance.Button,
        AutoCheck = true,
        Size = new Size(72, 34),
        FlatStyle = FlatStyle.Flat,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("Segoe UI", 8, FontStyle.Bold),
        Cursor = Cursors.Hand,
    };
    private readonly Label railShareState = MakeLabel("Offline", 9, true, Muted);
    private readonly Label railModel = MakeLabel("—", 10, true, TextColor);
    private readonly Label railActivity = MakeLabel("Idle", 10, true, TextColor);
    private readonly System.Windows.Forms.Timer networkTimer = new() { Interval = 8000 };
    private Panel? explorePage;
    private Panel? chatPage;
    private Panel? sharePage;
    private Panel? rootShell;
    private Panel? navigationSidebar;
    private Panel? exploreCenter;
    private Panel? exploreDeviceRail;
    private Panel? exploreRefreshWrap;
    private TableLayoutPanel? exploreMetrics;
    private Label? railShareCaption;
    private TableLayoutPanel? modelSelectionRow;
    private TableLayoutPanel? shareButtonsRow;
    private bool networkRefreshInProgress;
    private bool chatAutoScroll = true;
    private readonly FlowLayoutPanel chatMessages = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        BackColor = Color.FromArgb(13, 18, 16),
        Padding = new Padding(14),
    };
    private readonly TextBox chatPrompt = new()
    {
        Name = "AutoDirection",
        Multiline = true,
        AcceptsReturn = true,
        ScrollBars = ScrollBars.Vertical,
        BackColor = Field,
        ForeColor = TextColor,
        BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Segoe UI", 11),
    };
    private readonly Button sendChat = MakePrimaryButton("Send");
    private readonly Label selectedChatDevice = MakeLabel("Choose a device from Explore", 10, true, TextColor);
    private readonly Label chatState = MakeLabel("Ready", 8, false, Muted);
    private string? selectedChatHostId;
    private string? selectedChatModel;
    private string? selectedChatHostName;
    private bool chatRunning;
    private bool suppressQuickToggle;
    private JsonObject[] networkHosts = [];

    private sealed class ChatMessageView
    {
        internal required TableLayoutPanel Container { get; init; }
        internal required FlowLayoutPanel Thinking { get; init; }
        internal required FlowLayoutPanel ThinkingBody { get; init; }
        internal required FlowLayoutPanel Body { get; init; }
        internal required FlowLayoutPanel Actions { get; init; }
        internal string Text { get; set; } = "";
        internal string ThinkingText { get; set; } = "";
        internal bool IsAssistant { get; init; }
    }
    private CancellationTokenSource? sharingCancellation;
    private string? hostId;
    private string? hostToken;
    private HostSettings activeSettings = new();
    private EngineDetection activeEngine = EngineDetection.Unknown("");
    private long? activeContextLength;
    private bool exitRequested;
    private Action? webShellRestore;
    private Action? webShellExit;
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
        serverUrl.Name = "TechnicalLtr";
        platformKey.Name = "TechnicalLtr";
        modelApiUrl.Name = "TechnicalLtr";
        apiKey.Name = "TechnicalLtr";
        Text = L("نخلة AI", "AI Palm");
        ClientSize = new Size(1240, 780);
        MinimumSize = new Size(1020, 680);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 10);
        RightToLeft = RightToLeft.No;
        RightToLeftLayout = false;
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
        modelApiUrl.TextChanged += (_, _) =>
        {
            activeEngine = EngineDetection.Unknown(NormalizeEngineRoot(modelApiUrl.Text));
            UpdateEngineDetectionStatus(false);
        };
        connect.Click += async (_, _) => await StartSharingAsync();
        stop.Click += async (_, _) => await StopSharingAsync();
        refreshNetwork.Click += async (_, _) => await RefreshNetworkAsync();
        networkSearch.TextChanged += (_, _) => RenderNetworkHosts();
        quickShareToggle.CheckedChanged += async (_, _) =>
        {
            if (suppressQuickToggle) return;
            if (quickShareToggle.Checked) await StartSharingAsync();
            else await StopSharingAsync();
        };
        networkTimer.Tick += async (_, _) => await RefreshNetworkAsync();
        Shown += async (_, _) =>
        {
            networkTimer.Start();
            await RefreshNetworkAsync();
        };
        InitializeTray();
        Resize += OnResize;
        FormClosing += OnFormClosing;
        UpdateModeVisibility();
        SetConnectedControls(false);
        ApplyLanguage();
    }

    internal void EnableWebShellMode(Action restore, Action exit)
    {
        webShellRestore = restore;
        webShellExit = exit;
        ShowInTaskbar = false;
    }

    internal object GetWebShellState()
    {
        var settings = CurrentSettings();
        return new
        {
            settings = new
            {
                deviceName = settings.DeviceName,
                serverUrl = settings.ServerUrl,
                apiUrl = settings.ApiUrl,
                mode = settings.Mode,
                model = settings.Model,
                language = settings.Language,
                hasPlatformKey = !string.IsNullOrWhiteSpace(settings.PlatformKey),
                hasApiKey = !string.IsNullOrWhiteSpace(settings.ApiKey),
            },
            gpu = new { vendor = gpuInfo.Vendor, name = gpuInfo.Name, vramGb = gpuInfo.VramGb },
            sharing = sharingCancellation is not null,
            connected = hostId is not null,
            status = IsEnglish ? statusEnglishTitle : statusArabicTitle,
            detail = IsEnglish ? statusEnglishDetail : statusArabicDetail,
            activity = IsEnglish ? logEnglish : logArabic,
            engine = new { kind = activeEngine.Kind.ToString(), name = activeEngine.Name, version = activeEngine.Version, contextLength = activeContextLength },
            models = model.Items.Cast<object>().Select(item => item.ToString()).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray(),
        };
    }

    internal void ApplyWebShellSettings(JsonObject payload)
    {
        if (payload["deviceName"] is not null) deviceName.Text = ReadString(payload["deviceName"]);
        if (payload["serverUrl"] is not null) serverUrl.Text = ReadString(payload["serverUrl"]);
        if (payload["apiUrl"] is not null) modelApiUrl.Text = ReadString(payload["apiUrl"]);
        if (payload["model"] is not null) model.Text = ReadString(payload["model"]);
        if (payload["language"] is not null) language.SelectedIndex = ReadString(payload["language"]) == "ar" ? 0 : 1;
        if (payload["mode"] is not null) mode.SelectedIndex = ReadString(payload["mode"]) == "demo" ? 0 : 1;
        var platformValue = ReadString(payload["platformKey"]);
        if (!string.IsNullOrWhiteSpace(platformValue)) platformKey.Text = platformValue;
        var apiValue = ReadString(payload["apiKey"]);
        if (!string.IsNullOrWhiteSpace(apiValue)) apiKey.Text = apiValue;
        SaveSettings(CurrentSettings());
    }

    internal async Task<object> RefreshModelsForWebShellAsync()
    {
        await RefreshModelsAsync();
        return GetWebShellState();
    }

    internal async Task<object> StartSharingForWebShellAsync()
    {
        await StartSharingAsync();
        return GetWebShellState();
    }

    internal async Task<object> StopSharingForWebShellAsync()
    {
        await StopSharingAsync();
        return GetWebShellState();
    }

    internal Task<JsonObject> WebShellApiGetAsync(string path)
    {
        if (!path.StartsWith("/api/", StringComparison.Ordinal)) throw new InvalidOperationException("Only AI Palm API paths are allowed.");
        return GetJsonAsync($"{serverUrl.Text.Trim().TrimEnd('/')}{path}", null, CancellationToken.None);
    }

    internal Task<JsonObject> WebShellApiPostAsync(string path, JsonNode? body)
    {
        if (!path.StartsWith("/api/", StringComparison.Ordinal)) throw new InvalidOperationException("Only AI Palm API paths are allowed.");
        return PostJsonAsync($"{serverUrl.Text.Trim().TrimEnd('/')}{path}", body ?? new JsonObject(), null, CancellationToken.None);
    }

    private void BuildInterface()
    {
        rootShell = new Panel { Dock = DockStyle.Fill, BackColor = Background, Margin = Padding.Empty, Padding = Padding.Empty, RightToLeft = RightToLeft.No };
        Controls.Add(rootShell);

        navigationSidebar = BuildSidebar();
        navigationSidebar.Dock = DockStyle.Left;
        navigationSidebar.Width = 220;
        pageHost.Dock = DockStyle.Fill;
        rootShell.Controls.Add(pageHost);
        rootShell.Controls.Add(navigationSidebar);

        explorePage = BuildExplorePage();
        chatPage = BuildChatPage();
        sharePage = BuildSharePage();
        pageHost.Controls.Add(sharePage);
        pageHost.Controls.Add(chatPage);
        pageHost.Controls.Add(explorePage);
        navExplore.Click += (_, _) => ShowPage("explore");
        navChat.Click += (_, _) => ShowPage("chat");
        navShare.Click += (_, _) => ShowPage("share");
        sendChat.Click += async (_, _) => await SendChatAsync();
        chatPrompt.KeyDown += async (_, eventArgs) =>
        {
            if (eventArgs.KeyCode != Keys.Enter || eventArgs.Shift) return;
            eventArgs.SuppressKeyPress = true;
            await SendChatAsync();
        };
        ShowPage("explore");
    }

    private Panel BuildSidebar()
    {
        var sidebar = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(9, 27, 20), Padding = new Padding(16, 18, 16, 18) };
        var brand = new Panel { Dock = DockStyle.Top, Height = 178, BackColor = Color.Transparent };
        var logo = new PictureBox
        {
            Size = new Size(108, 108),
            Location = new Point(40, 0),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = LoadBrandImage() ?? Icon?.ToBitmap(),
            BackColor = Color.Transparent,
        };
        var brandName = MakeLabel("AI Palm", 15, true, TextColor);
        brandName.Location = new Point(4, 108);
        brandName.Size = new Size(180, 30);
        brandName.TextAlign = ContentAlignment.MiddleCenter;
        var brandMeta = Localize(MakeLabel("شبكة تطوعية", 8, false, Accent), "شبكة تطوعية", "Volunteer network");
        brandMeta.Location = new Point(4, 137);
        brandMeta.Size = new Size(180, 24);
        brandMeta.TextAlign = ContentAlignment.MiddleCenter;
        brand.Controls.AddRange([logo, brandName, brandMeta]);
        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 200,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 6, 0, 0),
        };
        navExplore.Width = 188;
        navChat.Width = 188;
        nav.Controls.Add(navExplore);
        nav.Controls.Add(navChat);
        sidebar.Controls.Add(nav);
        sidebar.Controls.Add(brand);
        return sidebar;
    }

    private Panel BuildExplorePage()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Background, Padding = new Padding(32, 24, 32, 24) };
        var center = new Panel { Dock = DockStyle.Fill, BackColor = Background, Padding = new Padding(0, 0, 16, 0) };
        exploreCenter = center;
        var header = new Panel { Dock = DockStyle.Top, Height = 86, BackColor = Background };
        var title = Localize(MakeLabel("استكشف", 25, true, TextColor), "استكشف", "Explore");
        title.Dock = DockStyle.Top;
        title.Height = 43;
        var subtitle = Localize(MakeLabel("اكتشف نماذج الذكاء الاصطناعي المحلية التي يشاركها المجتمع الآن.", 10, false, Muted), "اكتشف نماذج الذكاء الاصطناعي المحلية التي يشاركها المجتمع الآن.", "Discover local AI models shared by the community right now.");
        subtitle.Dock = DockStyle.Top;
        subtitle.Height = 31;
        var headerCopy = new Panel { Dock = DockStyle.Fill, BackColor = Background };
        headerCopy.Controls.Add(subtitle);
        headerCopy.Controls.Add(title);
        var refreshWrap = new Panel { Dock = DockStyle.Right, Width = 158, BackColor = Background, Padding = new Padding(10, 6, 0, 38) };
        exploreRefreshWrap = refreshWrap;
        refreshNetwork.Dock = DockStyle.Fill;
        refreshWrap.Controls.Add(refreshNetwork);
        header.Controls.Add(headerCopy);
        header.Controls.Add(refreshWrap);

        var searchWrap = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = Background, Padding = new Padding(0, 6, 0, 9) };
        networkSearch.Dock = DockStyle.Fill;
        networkSearch.PlaceholderText = L("ابحث باسم الجهاز أو البطاقة أو النموذج...", "Search models, GPUs, or devices...");
        searchWrap.Controls.Add(networkSearch);

        var metrics = new TableLayoutPanel { Dock = DockStyle.Top, Height = 112, ColumnCount = 3, BackColor = Background, Padding = new Padding(0, 4, 0, 12) };
        exploreMetrics = metrics;
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.334f));
        metrics.Controls.Add(MetricCard(connectedMetric, "الأجهزة المتصلة", "Online devices"), 0, 0);
        metrics.Controls.Add(MetricCard(modelsMetric, "النماذج المتاحة", "Available models"), 1, 0);
        metrics.Controls.Add(MetricCard(registeredMetric, "الأجهزة المسجلة", "Registered devices"), 2, 0);

        var listHeader = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = Background };
        var listTitle = Localize(MakeLabel("متاح الآن", 17, true, TextColor), "متاح الآن", "Available now");
        listTitle.Dock = DockStyle.Top;
        listTitle.Height = 34;
        exploreState.Dock = DockStyle.Top;
        exploreState.Height = 24;
        listHeader.Controls.Add(exploreState);
        listHeader.Controls.Add(listTitle);
        hostsFlow.SizeChanged += (_, _) => ResizeHostCards();
        center.Controls.Add(hostsFlow);
        center.Controls.Add(listHeader);
        center.Controls.Add(metrics);
        center.Controls.Add(searchWrap);
        center.Controls.Add(header);
        exploreDeviceRail = BuildDeviceRail();
        page.Controls.Add(center);
        page.Controls.Add(exploreDeviceRail);
        return page;
    }

    private Panel BuildDeviceRail()
    {
        var rail = new Panel { Dock = DockStyle.Right, Width = 286, BackColor = Background, Padding = new Padding(10, 0, 0, 0) };
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(10, 35, 26), Padding = new Padding(20) };
        rail.Controls.Add(card);

        var title = Localize(MakeLabel("جهازي", 17, true, TextColor), "جهازي", "My device");
        title.Location = new Point(20, 17);
        title.Size = new Size(170, 34);
        var dividerOne = new Panel { Location = new Point(20, 59), Size = new Size(226, 1), BackColor = Color.FromArgb(38, 76, 60) };
        var shareCaption = Localize(MakeLabel("مشاركة النموذج", 10, true, TextColor), "مشاركة النموذج", "Share model");
        railShareCaption = shareCaption;
        shareCaption.Location = new Point(20, 77);
        shareCaption.Size = new Size(135, 28);
        railShareState.Location = new Point(20, 106);
        railShareState.Size = new Size(135, 24);
        quickShareToggle.Location = new Point(174, 79);
        quickShareToggle.FlatAppearance.BorderColor = Color.FromArgb(52, 104, 80);

        var dividerTwo = new Panel { Location = new Point(20, 147), Size = new Size(226, 1), BackColor = Color.FromArgb(38, 76, 60) };
        var modelCaption = Localize(MakeLabel("النموذج المحلي المحدد", 8, false, Muted), "النموذج المحلي المحدد", "Selected local model");
        modelCaption.Location = new Point(20, 165);
        modelCaption.Size = new Size(226, 25);
        railModel.Name = "TechnicalLtr";
        railModel.Location = new Point(20, 193);
        railModel.Size = new Size(226, 35);
        railModel.Text = string.IsNullOrWhiteSpace(model.Text) ? "—" : model.Text;

        var dividerThree = new Panel { Location = new Point(20, 244), Size = new Size(226, 1), BackColor = Color.FromArgb(38, 76, 60) };
        var gpuCaption = Localize(MakeLabel("حالة البطاقة", 8, false, Muted), "حالة البطاقة", "GPU status");
        gpuCaption.Location = new Point(20, 262);
        gpuCaption.Size = new Size(226, 25);
        var gpuName = MakeLabel(gpuInfo.Name, 11, true, TextColor);
        gpuName.Name = "TechnicalLtr";
        gpuName.Location = new Point(20, 291);
        gpuName.Size = new Size(226, 51);
        var vram = MakeLabel(gpuInfo.VramGb is null ? "VRAM —" : $"{gpuInfo.VramGb:0} GB VRAM", 9, false, Accent);
        vram.Name = "TechnicalLtr";
        vram.Location = new Point(20, 343);
        vram.Size = new Size(226, 25);

        var dividerFour = new Panel { Location = new Point(20, 385), Size = new Size(226, 1), BackColor = Color.FromArgb(38, 76, 60) };
        var activityCaption = Localize(MakeLabel("النشاط الحالي", 8, false, Muted), "النشاط الحالي", "Current activity");
        activityCaption.Location = new Point(20, 403);
        activityCaption.Size = new Size(226, 25);
        railActivity.Location = new Point(20, 431);
        railActivity.Size = new Size(226, 34);

        var openSettings = Localize(MakeSecondaryButton(L("إعدادات المشاركة", "Share settings")), "إعدادات المشاركة", "Share settings");
        openSettings.Location = new Point(20, 486);
        openSettings.Size = new Size(226, 42);
        openSettings.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        openSettings.Click += (_, _) => ShowPage("share");
        card.Controls.AddRange([title, dividerOne, shareCaption, railShareState, quickShareToggle, dividerTwo, modelCaption, railModel, dividerThree, gpuCaption, gpuName, vram, dividerFour, activityCaption, railActivity, openSettings]);
        UpdateQuickShareVisual();
        return rail;
    }

    private Panel BuildChatPage()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Background, Padding = new Padding(32, 24, 32, 24) };
        var header = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Background };
        var title = Localize(MakeLabel("المحادثة", 25, true, TextColor), "المحادثة", "Chat");
        title.Dock = DockStyle.Top;
        title.Height = 42;
        selectedChatDevice.Dock = DockStyle.Top;
        selectedChatDevice.Height = 30;
        header.Controls.Add(selectedChatDevice);
        header.Controls.Add(title);

        var composer = new Panel { Dock = DockStyle.Bottom, Height = 128, BackColor = Background, Padding = new Padding(0, 14, 0, 0) };
        sendChat.Dock = DockStyle.Right;
        sendChat.Width = 142;
        sendChat.Margin = new Padding(10, 0, 0, 0);
        sendChat.Enabled = false;
        var inputWrap = new Panel { Dock = DockStyle.Fill, BackColor = Background, Padding = new Padding(0, 0, 12, 0) };
        chatPrompt.Dock = DockStyle.Fill;
        inputWrap.Controls.Add(chatPrompt);
        chatState.Dock = DockStyle.Bottom;
        chatState.Height = 25;
        composer.Controls.Add(inputWrap);
        composer.Controls.Add(sendChat);
        composer.Controls.Add(chatState);

        var transcriptCard = new Panel { Dock = DockStyle.Fill, BackColor = Panel, Padding = new Padding(1) };
        transcriptCard.Controls.Add(chatMessages);
        chatMessages.SizeChanged += (_, _) => ResizeChatMessages();
        chatMessages.MouseWheel += (_, eventArgs) =>
        {
            if (eventArgs.Delta > 0) chatAutoScroll = false;
            BeginInvoke(UpdateChatAutoScrollPreference);
        };
        chatMessages.MouseDown += (_, eventArgs) =>
        {
            var scrollbarWidth = SystemInformation.VerticalScrollBarWidth + 4;
            if (eventArgs.X >= chatMessages.ClientSize.Width - scrollbarWidth || eventArgs.X <= scrollbarWidth)
                chatAutoScroll = false;
        };
        chatMessages.MouseUp += (_, _) => BeginInvoke(UpdateChatAutoScrollPreference);
        chatPrompt.TextChanged += (_, _) =>
        {
            var rtl = ContainsRtl(chatPrompt.Text);
            chatPrompt.RightToLeft = rtl ? RightToLeft.Yes : RightToLeft.No;
            chatPrompt.TextAlign = HorizontalAlignment.Left;
        };
        page.Controls.Add(transcriptCard);
        page.Controls.Add(composer);
        page.Controls.Add(header);
        return page;
    }

    private Panel BuildSharePage()
    {
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Background, Padding = new Padding(32, 24, 32, 24) };
        var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, BackColor = Background };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.Controls.Add(content);

        var heading = Localize(MakeLabel("مشاركة جهازي", 25, true, TextColor), "مشاركة جهازي", "Share my device");
        heading.Dock = DockStyle.Top;
        heading.Height = 46;
        var subheading = Localize(MakeLabel("اختر نموذجك المحلي وشارك قدرته عندما يناسبك.", 10, false, Muted), "اختر نموذجك المحلي وشارك قدرته عندما يناسبك.", "Choose a local model and share its compute when it suits you.");
        subheading.Dock = DockStyle.Top;
        subheading.Height = 42;
        content.Controls.Add(heading);
        content.Controls.Add(subheading);

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
        formCard.MaximumSize = new Size(820, 0);
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
        apiFields.Controls.Add(Localize(MakeFieldLabel("المحرك المكتشف"), "المحرك المكتشف", "Detected engine"));
        engineDetectionStatus.Dock = DockStyle.Top;
        engineDetectionStatus.Height = 34;
        engineDetectionStatus.Padding = new Padding(10, 0, 10, 0);
        engineDetectionStatus.BackColor = Color.FromArgb(17, 21, 18);
        apiFields.Controls.Add(engineDetectionStatus);
        apiFields.Controls.Add(Localize(MakeFieldLabel("النموذج المتاح"), "النموذج المتاح", "Available model"));
        var modelRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 45, ColumnCount = 2, BackColor = Panel, Margin = new Padding(0, 0, 0, 2) };
        modelSelectionRow = modelRow;
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
        shareButtonsRow = buttons;
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
        return page;
    }

    private Panel MetricCard(Label value, string arabicLabel, string englishLabel)
    {
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Panel, Margin = new Padding(0, 0, 12, 0), Padding = new Padding(18, 13, 18, 10) };
        value.Dock = DockStyle.Top;
        value.Height = 40;
        var label = Localize(MakeLabel(arabicLabel, 9, false, Muted), arabicLabel, englishLabel);
        label.Dock = DockStyle.Fill;
        card.Controls.Add(label);
        card.Controls.Add(value);
        return card;
    }

    private void ShowPage(string page)
    {
        if (explorePage is null || chatPage is null || sharePage is null) return;
        explorePage.Visible = page == "explore";
        chatPage.Visible = page == "chat";
        sharePage.Visible = page == "share";
        explorePage.BringToFront();
        if (page == "chat") chatPage.BringToFront();
        if (page == "share") sharePage.BringToFront();
        SetNavSelected(navExplore, page == "explore");
        SetNavSelected(navChat, page == "chat");
        SetNavSelected(navShare, page == "share");
        if (page == "explore" && Visible && IsHandleCreated) _ = RefreshNetworkAsync();
    }

    internal void ShowSelfTestPage(string page) => ShowPage(page);

    internal object GetSelfTestLayout()
    {
        var root = Controls.Count > 0 ? Controls[0] : null;
        return new
        {
            formRtl = RightToLeft.ToString(),
            formRtlLayout = RightToLeftLayout,
            rootRtl = root?.RightToLeft.ToString(),
            children = root?.Controls.Cast<Control>().Select(item => new { item.GetType().Name, item.Left, item.Width, rtl = item.RightToLeft.ToString() }).ToArray(),
            exploreCenter = exploreCenter is null ? null : new { exploreCenter.Left, exploreCenter.Width, exploreCenter.RightToLeft },
            deviceRail = exploreDeviceRail is null ? null : new { exploreDeviceRail.Left, exploreDeviceRail.Width, exploreDeviceRail.Dock },
            refreshWrap = exploreRefreshWrap is null ? null : new { exploreRefreshWrap.Left, exploreRefreshWrap.Width, exploreRefreshWrap.Dock },
            metrics = exploreMetrics is null ? null : new { exploreMetrics.Left, exploreMetrics.Width, exploreMetrics.RightToLeft },
            search = new { networkSearch.Left, networkSearch.Width, networkSearch.RightToLeft },
        };
    }

    internal string[] GetSelfTestRtlIssues()
    {
        if (IsEnglish) return [];
        var issues = new List<string>();
        if (navigationSidebar?.Dock != DockStyle.Right) issues.Add("navigation-sidebar");
        if (exploreDeviceRail?.Dock != DockStyle.Left) issues.Add("device-rail");
        if (exploreRefreshWrap?.Dock != DockStyle.Left) issues.Add("refresh-area");
        if (exploreMetrics?.RightToLeft != RightToLeft.Yes) issues.Add("metric-order");
        if (hostsFlow.FlowDirection != FlowDirection.RightToLeft) issues.Add("host-card-order");
        if (sendChat.Dock != DockStyle.Left) issues.Add("chat-composer");
        if (modelSelectionRow?.RightToLeft != RightToLeft.Yes) issues.Add("model-selection-row");
        if (shareButtonsRow?.RightToLeft != RightToLeft.Yes) issues.Add("share-buttons-row");
        AuditRtlControls(Controls, issues);
        return [.. issues.Distinct()];
    }

    private static void AuditRtlControls(Control.ControlCollection controls, List<string> issues)
    {
        foreach (Control control in controls)
        {
            var technical = control.Name.Contains("TechnicalLtr", StringComparison.Ordinal);
            var automatic = control.Name.Contains("AutoDirection", StringComparison.Ordinal);
            if (!automatic && control is Label label && label.TextAlign != ContentAlignment.MiddleCenter)
            {
                var expectedDirection = technical ? RightToLeft.No : RightToLeft.Yes;
                var expectedAlignment = technical ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft;
                if (label.RightToLeft != expectedDirection || label.TextAlign != expectedAlignment)
                    issues.Add($"label:{label.Text}");
            }
            else if (!automatic && control is TextBox box)
            {
                var expectedDirection = technical ? RightToLeft.No : RightToLeft.Yes;
                var expectedAlignment = technical ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                if (box.RightToLeft != expectedDirection || box.TextAlign != expectedAlignment)
                    issues.Add($"textbox:{box.Name}");
            }
            if (control.HasChildren) AuditRtlControls(control.Controls, issues);
        }
    }

    internal void SetSelfTestLanguage(string value)
    {
        language.SelectedIndex = value.Equals("ar", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        ApplyLanguage();
    }

    internal void SetSelfTestConnected(bool connected) => SetConnectedControls(connected);

    internal object GetSelfTestQuickAction() => new
    {
        connected = quickShareToggle.Checked,
        button = quickShareToggle.Text,
        caption = railShareCaption?.Text,
    };

    internal object[] GetSelfTestChatLayout() => chatMessages.Controls.Cast<Control>()
        .Where(control => control.Tag is ChatMessageView)
        .Select(control =>
        {
            var view = (ChatMessageView)control.Tag!;
            var prose = view.Body.Controls.OfType<RichTextBox>().FirstOrDefault(box => box.Name == "AutoDirection");
            var thinkingProse = view.ThinkingBody.Controls.OfType<RichTextBox>().FirstOrDefault(box => box.Name == "AutoDirection");
            return (object)new
            {
                assistant = view.IsAssistant,
                container = new { view.Container.Left, view.Container.Width },
                body = new { view.Body.Left, view.Body.Width },
                thinking = new { view.Thinking.Visible, view.Thinking.Width, length = thinkingProse?.TextLength ?? 0 },
                codeBlocks = view.Body.Controls.OfType<Panel>().Count(panel => panel.Name == "CodeBlock"),
                prose = prose is null ? null : new { prose.Left, prose.Width, prose.RightToLeft, prose.SelectionAlignment, length = prose.TextLength },
            };
        }).ToArray();

    internal void PopulateSelfTestChat()
    {
        selectedChatHostId = "preview-host";
        selectedChatModel = "Qwen3 32B";
        selectedChatHostName = "Atlas";
        selectedChatDevice.Text = "Atlas   •   Qwen3 32B   •   RTX 5090";
        AppendChatMessage("You", "اكتب لي مثالاً بسيطاً بلغة Python لقراءة ملف JSON.", Accent);
        var sample = AppendChatMessage("Model", "سأستخدم مكتبة `json` المدمجة في Python:\n\n```python\nimport json\n\nwith open(\"data.json\", encoding=\"utf-8\") as file:\n    data = json.load(file)\n\nprint(data)\n```\n\nيتعامل هذا المثال مع UTF-8 ويغلق الملف تلقائياً.", Color.FromArgb(102, 230, 153), true);
        RenderThinkingBody(sample, "أفهم المطلوب وأختار مثالاً قصيراً يحافظ على ترميز UTF-8.");
        CompleteAssistantMessage(sample, new JsonObject { ["prompt_tokens"] = 18, ["completion_tokens"] = 54, ["total_tokens"] = 72, ["tokens_per_second"] = 21.6 });
        chatPrompt.Text = "اكتب رسالتك هنا...";
    }

    internal void PopulateSelfTestArabicChat()
    {
        selectedChatHostId = "preview-host";
        selectedChatModel = "Qwen3.8-27B";
        selectedChatHostName = "Tiger";
        selectedChatDevice.Text = "Tiger   •   Qwen3.8-27B";
        AppendChatMessage("أنت", "السلام عليكم", Accent);
        var sample = AppendChatMessage("النموذج", "وعليكم السلام ورحمة الله وبركاته! أهلاً بك، كيف يمكنني مساعدتك اليوم؟", Color.FromArgb(102, 230, 153), true);
        CompleteAssistantMessage(sample, new JsonObject { ["prompt_tokens"] = 4, ["completion_tokens"] = 23, ["total_tokens"] = 27, ["tokens_per_second"] = 9.3 });
    }

    internal bool IsManualScrollLockHandledForSelfTest()
    {
        chatAutoScroll = false;
        ScrollChatToBottom();
        var preserved = !chatAutoScroll;
        chatAutoScroll = true;
        return preserved;
    }

    private static void SetNavSelected(Button button, bool selected)
    {
        button.BackColor = selected ? Color.FromArgb(20, 88, 61) : Color.Transparent;
        button.ForeColor = selected ? TextColor : Muted;
        button.FlatAppearance.BorderColor = selected ? Color.FromArgb(69, 177, 122) : Color.FromArgb(9, 27, 20);
    }

    private async Task RefreshNetworkAsync()
    {
        if (networkRefreshInProgress || string.IsNullOrWhiteSpace(serverUrl.Text)) return;
        networkRefreshInProgress = true;
        exploreState.Text = L("جاري قراءة الشبكة...", "Refreshing the network...");
        try
        {
            var root = await GetJsonAsync($"{serverUrl.Text.Trim().TrimEnd('/')}/api/hosts", null, CancellationToken.None);
            var stats = root["stats"] as JsonObject;
            var hosts = root["hosts"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
            connectedMetric.Text = (stats?["connected"]?.GetValue<int?>() ?? hosts.Length).ToString();
            registeredMetric.Text = (stats?["registered"]?.GetValue<int?>() ?? hosts.Length).ToString();
            modelsMetric.Text = (root["models"]?.AsArray().Count ?? hosts.Select(host => host["model"]?.GetValue<string>()).Distinct().Count()).ToString();
            networkHosts = hosts;
            RenderNetworkHosts();
            exploreState.Text = hosts.Length == 0
                ? L("لا توجد أجهزة متاحة الآن. سنحدّث القائمة تلقائياً.", "No devices are available now. The list will refresh automatically.")
                : L($"{hosts.Length} جهاز متاح للمجتمع", $"{hosts.Length} device(s) available to the community");
        }
        catch (Exception ex)
        {
            exploreState.Text = L("تعذر الاتصال بالشبكة. تحقق من رابط المنصة في صفحة المشاركة.", "Could not reach the network. Check the platform URL on the Share page.");
            SetLog($"تعذر تحديث الشبكة: {FriendlyError(ex)}", $"Network refresh failed: {FriendlyError(ex)}");
        }
        finally
        {
            networkRefreshInProgress = false;
        }
    }

    private void PopulateHosts(JsonObject[] hosts)
    {
        hostsFlow.SuspendLayout();
        hostsFlow.Controls.Clear();
        foreach (var host in hosts) hostsFlow.Controls.Add(CreateHostCard(host));
        if (hosts.Length == 0)
        {
            var empty = Localize(MakeLabel("بانتظار جهاز متطوع...", 12, true, Muted), "بانتظار جهاز متطوع...", "Waiting for a volunteer device...");
            empty.Width = Math.Max(320, hostsFlow.ClientSize.Width - 32);
            empty.Height = 100;
            empty.TextAlign = ContentAlignment.MiddleCenter;
            hostsFlow.Controls.Add(empty);
        }
        ResizeHostCards();
        hostsFlow.ResumeLayout();
    }

    private void RenderNetworkHosts()
    {
        var query = networkSearch.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? networkHosts
            : networkHosts.Where(host => new[] { "name", "gpu", "gpu_vendor", "model" }
                .Any(key => (host[key]?.GetValue<string>() ?? "").Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToArray();
        PopulateHosts(filtered);
    }

    private Panel CreateHostCard(JsonObject host)
    {
        var name = host["name"]?.GetValue<string>() ?? L("جهاز متطوع", "Volunteer device");
        var vendor = host["gpu_vendor"]?.GetValue<string>() ?? "GPU";
        var gpu = host["gpu"]?.GetValue<string>() ?? L("بطاقة غير معروفة", "Unknown GPU");
        var modelName = host["model"]?.GetValue<string>() ?? L("نموذج غير محدد", "Unspecified model");
        var vram = host["vram_gb"]?.GetValue<double?>();
        var busy = host["busy"]?.GetValue<bool?>() ?? false;

        var card = new Panel { Width = 290, Height = 218, BackColor = Panel, Margin = new Padding(0, 0, 12, 12), Padding = new Padding(16) };
        var vendorBadge = MakeLabel(vendor.ToUpperInvariant(), 9, true, VendorColor(vendor));
        vendorBadge.BackColor = Color.FromArgb(14, 23, 19);
        vendorBadge.TextAlign = ContentAlignment.MiddleCenter;
        vendorBadge.Location = new Point(16, 17);
        vendorBadge.Size = new Size(62, 54);

        var nameLabel = MakeLabel(name, 13, true, TextColor);
        nameLabel.Location = new Point(16, 79);
        nameLabel.Size = new Size(card.Width - 32, 27);
        nameLabel.TextAlign = ContentAlignment.MiddleLeft;
        var gpuLabel = MakeLabel(gpu, 10, true, TextColor);
        gpuLabel.Name = "TechnicalLtr";
        gpuLabel.Location = new Point(16, 106);
        gpuLabel.Size = new Size(card.Width - 32, 25);
        gpuLabel.TextAlign = ContentAlignment.MiddleLeft;
        var detailLabel = MakeLabel($"{(vram is null ? "—" : $"{Math.Round(vram.Value):0} GB VRAM")}   •   {modelName}", 9, false, Accent);
        detailLabel.Name = "TechnicalLtr";
        detailLabel.Location = new Point(16, 132);
        detailLabel.Size = new Size(card.Width - 32, 25);
        detailLabel.TextAlign = ContentAlignment.MiddleLeft;

        var status = MakeLabel(busy ? L("● قيد الاستخدام", "● In use") : L("● متصل", "● Online"), 9, true, busy ? Color.Goldenrod : Color.FromArgb(102, 230, 153));
        status.Location = new Point(card.Width - 150, 25);
        status.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        status.Size = new Size(130, 26);
        status.TextAlign = ContentAlignment.MiddleRight;
        var action = MakePrimaryButton(L("فتح المحادثة", "Open chat"));
        action.Location = new Point(16, 166);
        action.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        action.Size = new Size(card.Width - 32, 38);
        action.Enabled = !busy;
        action.Click += (_, _) => OpenChatForHost(host);
        card.Controls.AddRange([vendorBadge, nameLabel, gpuLabel, detailLabel, status, action]);
        if (!IsEnglish)
        {
            vendorBadge.Location = new Point(card.Width - 78, 17);
            vendorBadge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            status.Location = new Point(16, 25);
            status.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        }
        ApplyLanguageToControls(card.Controls);
        return card;
    }

    private void OpenChatForHost(JsonObject host)
    {
        selectedChatHostId = host["id"]?.GetValue<string>();
        selectedChatModel = host["model"]?.GetValue<string>();
        selectedChatHostName = host["name"]?.GetValue<string>() ?? L("جهاز متطوع", "Volunteer device");
        selectedChatDevice.Text = $"{selectedChatHostName}   •   {selectedChatModel}";
        chatState.Text = L("الجهاز جاهز لهذه المحادثة", "The device is ready for this chat");
        sendChat.Enabled = !string.IsNullOrWhiteSpace(selectedChatHostId) && !chatRunning;
        ShowPage("chat");
        chatPrompt.Focus();
    }

    private async Task SendChatAsync()
    {
        var prompt = chatPrompt.Text.Trim();
        if (chatRunning || string.IsNullOrWhiteSpace(prompt)) return;
        if (string.IsNullOrWhiteSpace(selectedChatHostId) || string.IsNullOrWhiteSpace(selectedChatModel))
        {
            MessageBox.Show(this, L("اختر جهازاً متاحاً من صفحة الاستكشاف أولاً.", "Choose an available device from Explore first."), L("لم يتم اختيار جهاز", "No device selected"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            ShowPage("explore");
            return;
        }

        chatRunning = true;
        sendChat.Enabled = false;
        chatPrompt.Enabled = false;
        AppendChatMessage(L("أنت", "You"), prompt, Accent);
        chatPrompt.Clear();
        chatState.Text = L("يتم حجز الجهاز وإرسال الرسالة...", "Reserving the device and sending your message...");
        try
        {
            var created = await PostJsonAsync(
                $"{serverUrl.Text.Trim().TrimEnd('/')}/api/jobs",
                new { kind = "chat", prompt, model = selectedChatModel, target_host_id = selectedChatHostId },
                null,
                CancellationToken.None);
            var jobId = created["job"]?["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(jobId)) throw new InvalidOperationException(L("لم يُرجع الخادم معرّف المهمة", "The server did not return a job ID"));
            await PollChatJobAsync(jobId);
        }
        catch (Exception ex)
        {
            AppendChatMessage(L("تعذر إكمال المهمة", "Request failed"), FriendlyError(ex), Danger);
            chatState.Text = L("تعذر تنفيذ الرسالة", "The message could not be completed");
        }
        finally
        {
            chatRunning = false;
            chatPrompt.Enabled = true;
            sendChat.Enabled = !string.IsNullOrWhiteSpace(selectedChatHostId);
            chatPrompt.Focus();
            _ = RefreshNetworkAsync();
        }
    }

    private async Task PollChatJobAsync(string jobId)
    {
        var assistant = AppendChatMessage(L("النموذج", "Model"), "", Color.FromArgb(102, 230, 153), true);
        var lastResult = "";
        var lastThinking = "";
        for (var attempt = 0; attempt < 3600; attempt++)
        {
            var root = await GetJsonAsync($"{serverUrl.Text.Trim().TrimEnd('/')}/api/jobs/{jobId}", null, CancellationToken.None);
            var job = root["job"] as JsonObject ?? throw new InvalidOperationException(L("تعذر قراءة المهمة", "Could not read the job"));
            var status = job["status"]?.GetValue<string>() ?? "queued";
            var result = job["result"]?.GetValue<string>() ?? "";
            var thinking = job["thinking"]?.GetValue<string>() ?? "";
            var compatibility = ExtractCompatibilityReasoning(result);
            result = compatibility.Answer;
            thinking += compatibility.Thinking;
            if (thinking != lastThinking)
            {
                RenderThinkingBody(assistant, thinking);
                lastThinking = thinking;
                ScrollChatToBottom();
            }
            if (result != lastResult)
            {
                RenderMessageBody(assistant, result);
                lastResult = result;
                ScrollChatToBottom();
            }
            chatState.Text = status switch
            {
                "queued" => L("بانتظار الجهاز المحدد...", "Waiting for the selected device..."),
                "running" => L("تصل الإجابة الآن...", "The answer is arriving..."),
                "completed" => L("اكتملت المهمة", "Completed"),
                "failed" => L("فشلت المهمة", "Failed"),
                _ => status,
            };
            if (status == "completed")
            {
                CompleteAssistantMessage(assistant, job["usage"] as JsonObject);
                return;
            }
            if (status == "failed")
                throw new InvalidOperationException(job["error"]?.GetValue<string>() ?? L("فشلت المهمة", "The request failed"));
            await Task.Delay(650);
        }
        throw new TimeoutException(L("استغرقت المهمة وقتاً أطول من المتوقع", "The request took longer than expected"));
    }

    private ChatMessageView AppendChatMessage(string role, string text, Color roleColor, bool isAssistant = false)
    {
        var width = Math.Max(560, chatMessages.ClientSize.Width - 46);
        var container = new TableLayoutPanel
        {
            Width = width,
            MinimumSize = new Size(width, 0),
            MaximumSize = new Size(width, 0),
            AutoSize = true,
            ColumnCount = 1,
            BackColor = isAssistant ? Color.FromArgb(17, 31, 25) : Color.FromArgb(28, 34, 31),
            Padding = new Padding(18, 14, 18, 13),
            Margin = new Padding(0, 0, 0, 14),
        };
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var roleLabel = MakeLabel(role, 9, true, roleColor);
        roleLabel.Dock = DockStyle.Top;
        roleLabel.Height = 25;
        var body = new FlowLayoutPanel
        {
            Width = width - 38,
            MinimumSize = new Size(width - 38, 0),
            MaximumSize = new Size(width - 38, 0),
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        var thinkingBody = new FlowLayoutPanel
        {
            Width = width - 58,
            MinimumSize = new Size(width - 58, 0),
            MaximumSize = new Size(width - 58, 0),
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        var thinking = new FlowLayoutPanel
        {
            Width = width - 38,
            MinimumSize = new Size(width - 38, 0),
            MaximumSize = new Size(width - 38, 0),
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.FromArgb(13, 24, 20),
            Margin = new Padding(0, 2, 0, 10),
            Padding = new Padding(10, 7, 10, 8),
            Visible = false,
        };
        var thinkingTitle = Localize(MakeLabel("تفكير النموذج · مباشر", 8, true, Accent), "تفكير النموذج · مباشر", "Model reasoning · live");
        thinkingTitle.Width = width - 58;
        thinkingTitle.Height = 23;
        thinking.Controls.Add(thinkingTitle);
        thinking.Controls.Add(thinkingBody);
        var actions = new FlowLayoutPanel
        {
            Width = width - 38,
            MinimumSize = new Size(width - 38, 38),
            MaximumSize = new Size(width - 38, 38),
            Height = 38,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 8, 0, 0),
            Visible = false,
        };
        var view = new ChatMessageView { Container = container, Thinking = thinking, ThinkingBody = thinkingBody, Body = body, Actions = actions, Text = text, IsAssistant = isAssistant };
        container.Tag = view;
        container.Controls.Add(roleLabel, 0, 0);
        container.Controls.Add(thinking, 0, 1);
        container.Controls.Add(body, 0, 2);
        container.Controls.Add(actions, 0, 3);
        chatMessages.Controls.Add(container);
        RenderMessageBody(view, text);
        ApplyLanguageToControls(container.Controls);
        ApplyChatMessageDirection(view);
        ScrollChatToBottom(true);
        return view;
    }

    private void RenderMessageBody(ChatMessageView view, string text)
    {
        view.Text = text;
        SetRedraw(view.Container, false);
        view.Container.SuspendLayout();
        view.Body.SuspendLayout();
        try
        {
            view.Body.Controls.Clear();
            var bodyWidth = Math.Max(460, view.Container.Width - 38);
            var matches = Regex.Matches(text, "```(?<language>[^\\r\\n`]*)[\\r\\n]+(?<code>[\\s\\S]*?)(?:```|$)", RegexOptions.CultureInvariant);
            var position = 0;
            foreach (Match match in matches)
            {
                if (match.Index > position) AddProseBlock(view.Body, text[position..match.Index], bodyWidth);
                AddCodeBlock(view.Body, match.Groups["code"].Value.TrimEnd(), match.Groups["language"].Value.Trim(), bodyWidth);
                position = match.Index + match.Length;
            }
            if (position < text.Length) AddProseBlock(view.Body, text[position..], bodyWidth);
            if (text.Length == 0) AddProseBlock(view.Body, L("يفكر النموذج...", "The model is thinking..."), bodyWidth, true);
        }
        finally
        {
            view.Body.ResumeLayout(true);
            view.Container.ResumeLayout(true);
            SetRedraw(view.Container, true);
        }
    }

    private void RenderThinkingBody(ChatMessageView view, string text)
    {
        view.ThinkingText = text;
        SetRedraw(view.Container, false);
        view.Container.SuspendLayout();
        view.ThinkingBody.SuspendLayout();
        try
        {
            view.ThinkingBody.Controls.Clear();
            if (!string.IsNullOrWhiteSpace(text))
                AddProseBlock(view.ThinkingBody, text, Math.Max(440, view.Container.Width - 58), true);
            view.Thinking.Visible = !string.IsNullOrWhiteSpace(text);
        }
        finally
        {
            view.ThinkingBody.ResumeLayout(true);
            view.Container.ResumeLayout(true);
            SetRedraw(view.Container, true);
        }
    }

    private void AddProseBlock(FlowLayoutPanel parent, string text, int width, bool muted = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var rtl = ContainsRtl(text);
        var box = new RichTextBox
        {
            Name = "AutoDirection",
            Width = width,
            Height = MeasureTextHeight(text, width, new Font("Segoe UI", 11), 60, 360),
            ReadOnly = true,
            BackColor = parent.Parent?.BackColor ?? Panel,
            ForeColor = muted ? Muted : TextColor,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 11),
            DetectUrls = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            RightToLeft = rtl ? RightToLeft.Yes : RightToLeft.No,
            Text = text.Trim(),
            Margin = new Padding(0, 2, 0, 7),
        };
        box.SelectAll();
        box.SelectionAlignment = rtl ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        box.DeselectAll();
        parent.Controls.Add(box);
    }

    private void AddCodeBlock(FlowLayoutPanel parent, string code, string languageName, int width)
    {
        var panel = new Panel
        {
            Name = "CodeBlock",
            Width = width,
            Height = Math.Min(330, Math.Max(120, 61 + code.Split('\n').Length * 20)),
            BackColor = Color.FromArgb(7, 12, 10),
            Margin = new Padding(0, 5, 0, 9),
            Padding = new Padding(1),
        };
        var header = new Panel { Name = "CodeHeader", Location = new Point(1, 1), Size = new Size(width - 2, 38), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, BackColor = Color.FromArgb(23, 39, 32) };
        var languageLabel = MakeLabel(string.IsNullOrWhiteSpace(languageName) ? "code" : languageName.ToLowerInvariant(), 8, true, Muted);
        languageLabel.Name = "CodeLanguageTechnicalLtr";
        languageLabel.Dock = IsEnglish ? DockStyle.Left : DockStyle.Right;
        languageLabel.Width = 180;
        languageLabel.Padding = IsEnglish ? new Padding(12, 0, 0, 0) : new Padding(0, 0, 12, 0);
        languageLabel.TextAlign = IsEnglish ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleRight;
        var copyCode = Localize(MakeMiniButton(L("⧉ نسخ الكود", "⧉ Copy code")), "⧉ نسخ الكود", "⧉ Copy code");
        copyCode.Name = "CodeCopy";
        copyCode.Dock = IsEnglish ? DockStyle.Right : DockStyle.Left;
        copyCode.Width = 112;
        copyCode.Click += (_, _) => CopyText(code, copyCode);
        header.Controls.Add(copyCode);
        header.Controls.Add(languageLabel);
        var codeBox = new RichTextBox
        {
            Name = "CodeTechnicalLtr",
            Location = new Point(1, 39),
            Size = new Size(width - 2, panel.Height - 40),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            ReadOnly = true,
            WordWrap = false,
            BackColor = Color.FromArgb(7, 12, 10),
            ForeColor = Color.FromArgb(219, 230, 223),
            BorderStyle = BorderStyle.None,
            Font = new Font("Cascadia Mono", 9.5f),
            DetectUrls = false,
            ScrollBars = RichTextBoxScrollBars.Both,
            RightToLeft = RightToLeft.No,
            Text = code,
            Margin = Padding.Empty,
        };
        panel.Controls.Add(codeBox);
        panel.Controls.Add(header);
        header.BringToFront();
        ApplySyntaxHighlighting(codeBox, languageName);
        parent.Controls.Add(panel);
    }

    private void CompleteAssistantMessage(ChatMessageView view, JsonObject? usage)
    {
        view.Actions.Controls.Clear();
        var copy = Localize(MakeMiniButton(L("⧉ نسخ", "⧉ Copy")), "⧉ نسخ", "⧉ Copy");
        copy.Click += (_, _) => CopyText(view.Text, copy);
        view.Actions.Controls.Add(copy);
        if (usage is not null)
        {
            var input = ReadInt64(usage["prompt_tokens"]) ?? 0;
            var output = ReadInt64(usage["completion_tokens"]) ?? 0;
            var total = ReadInt64(usage["total_tokens"]) ?? input + output;
            var speed = ReadDouble(usage["tokens_per_second"]);
            var metrics = MakeLabel($"{input} in  •  {output} out  •  {total} total{(speed is null ? "" : $"  •  {speed:0.0} tok/s")}", 8, false, Muted);
            metrics.Name = "TechnicalLtr";
            metrics.Width = 370;
            metrics.Height = 32;
            metrics.TextAlign = ContentAlignment.MiddleLeft;
            view.Actions.Controls.Add(metrics);
        }
        view.Actions.Visible = true;
        ApplyLanguageToControls(view.Actions.Controls);
        ApplyChatMessageDirection(view);
        view.Container.PerformLayout();
        ScrollChatToBottom();
    }

    private static Button MakeMiniButton(string text)
    {
        var button = MakeSecondaryButton(text);
        button.AutoSize = true;
        button.MinimumSize = new Size(82, 32);
        button.Height = 32;
        button.Margin = new Padding(0, 0, 8, 0);
        button.Font = new Font("Segoe UI", 8, FontStyle.Bold);
        return button;
    }

    private void CopyText(string text, Button feedback)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            var original = feedback.Text;
            feedback.Text = L("تم النسخ", "Copied");
            var timer = new System.Windows.Forms.Timer { Interval = 1400 };
            timer.Tick += (_, _) => { feedback.Text = original; timer.Stop(); timer.Dispose(); };
            timer.Start();
        }
        catch { }
    }

    private static bool ContainsRtl(string text) => text.Any(character =>
        character is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF');

    private static int MeasureTextHeight(string text, int width, Font font, int minimum, int maximum)
    {
        var measured = TextRenderer.MeasureText(text + "\n", font, new Size(Math.Max(120, width - 14), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        return Math.Clamp(measured.Height + 15, minimum, maximum);
    }

    private static void ApplySyntaxHighlighting(RichTextBox box, string languageName)
    {
        var selection = box.SelectionStart;
        box.SelectAll();
        box.SelectionColor = Color.FromArgb(219, 230, 223);
        var language = languageName.ToLowerInvariant();
        var keywords = language switch
        {
            "python" or "py" => "and|as|assert|async|await|break|class|continue|def|del|elif|else|except|False|finally|for|from|global|if|import|in|is|lambda|None|not|or|pass|raise|return|True|try|while|with|yield",
            "javascript" or "js" or "typescript" or "ts" => "async|await|break|case|catch|class|const|continue|default|delete|do|else|export|extends|false|finally|for|from|function|if|import|in|instanceof|let|new|null|of|return|static|super|switch|this|throw|true|try|typeof|undefined|var|while",
            "csharp" or "cs" or "c#" => "abstract|as|async|await|base|bool|break|case|catch|class|const|continue|decimal|default|delegate|do|double|else|enum|event|explicit|extern|false|finally|fixed|float|for|foreach|if|implicit|in|int|interface|internal|is|lock|long|namespace|new|null|object|operator|out|override|private|protected|public|readonly|record|ref|return|sealed|short|static|string|struct|switch|this|throw|true|try|typeof|uint|ulong|unchecked|unsafe|ushort|using|var|virtual|void|volatile|while",
            "php" => "abstract|and|array|as|break|callable|case|catch|class|clone|const|continue|declare|default|do|echo|else|elseif|empty|enddeclare|endfor|endforeach|endif|endswitch|endwhile|eval|exit|extends|final|finally|fn|for|foreach|function|global|goto|if|implements|include|instanceof|interface|isset|list|match|namespace|new|null|or|print|private|protected|public|readonly|require|return|static|switch|throw|trait|true|try|unset|use|var|while|yield",
            _ => "true|false|null|class|function|return|if|else|for|while|const|let|var|import|from|def|async|await|public|private|static|new",
        };
        ColorMatches(box, $@"\b({keywords})\b", Color.FromArgb(198, 145, 255));
        if (language is "html" or "htm" or "xml" or "svg")
            ColorMatches(box, @"</?[A-Za-z][^>]*>", Color.FromArgb(120, 205, 255));
        else if (language is "css" or "scss")
        {
            ColorMatches(box, @"(?m)^[^{}]+(?=\s*\{)", Color.FromArgb(198, 145, 255));
            ColorMatches(box, @"(?m)(?:^|[;{])\s*[A-Za-z-]+(?=\s*:)", Color.FromArgb(120, 205, 255));
        }
        ColorMatches(box, "\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'", Color.FromArgb(238, 199, 117));
        ColorMatches(box, @"\b\d+(?:\.\d+)?\b", Color.FromArgb(120, 205, 255));
        ColorMatches(box, @"(?m)//.*$|#.*$|/\*[\s\S]*?\*/", Color.FromArgb(105, 136, 119));
        box.Select(Math.Min(selection, box.TextLength), 0);
        box.SelectionColor = Color.FromArgb(219, 230, 223);
    }

    private static void ColorMatches(RichTextBox box, string pattern, Color color)
    {
        foreach (Match match in Regex.Matches(box.Text, pattern, RegexOptions.CultureInvariant))
        {
            box.Select(match.Index, match.Length);
            box.SelectionColor = color;
        }
    }

    private void ResizeChatMessages()
    {
        var width = Math.Max(560, chatMessages.ClientSize.Width - 46);
        foreach (Control control in chatMessages.Controls)
        {
            if (control.Tag is not ChatMessageView view) continue;
            view.Container.Width = width;
            view.Container.MinimumSize = new Size(width, 0);
            view.Container.MaximumSize = new Size(width, 0);
            view.Thinking.Width = width - 38;
            view.Thinking.MinimumSize = new Size(width - 38, 0);
            view.Thinking.MaximumSize = new Size(width - 38, 0);
            view.ThinkingBody.Width = width - 58;
            view.ThinkingBody.MinimumSize = new Size(width - 58, 0);
            view.ThinkingBody.MaximumSize = new Size(width - 58, 0);
            if (view.Thinking.Controls.OfType<Label>().FirstOrDefault() is Label thinkingTitle)
                thinkingTitle.Width = width - 58;
            view.Body.Width = width - 38;
            view.Body.MinimumSize = new Size(width - 38, 0);
            view.Body.MaximumSize = new Size(width - 38, 0);
            view.Actions.Width = width - 38;
            view.Actions.MinimumSize = new Size(width - 38, 38);
            view.Actions.MaximumSize = new Size(width - 38, 38);
            RenderThinkingBody(view, view.ThinkingText);
            RenderMessageBody(view, view.Text);
        }
    }

    private void UpdateChatAutoScrollPreference()
    {
        var scroll = chatMessages.VerticalScroll;
        chatAutoScroll = scroll.Value + scroll.LargeChange >= scroll.Maximum - 28;
    }

    private void ScrollChatToBottom(bool force = false)
    {
        if (chatMessages.Controls.Count == 0 || (!force && !chatAutoScroll)) return;
        var scroll = chatMessages.VerticalScroll;
        var bottom = Math.Max(scroll.Minimum, scroll.Maximum - scroll.LargeChange + 1);
        chatMessages.AutoScrollPosition = new Point(0, bottom);
        chatAutoScroll = true;
    }

    private void ResizeHostCards()
    {
        var available = Math.Max(300, hostsFlow.ClientSize.Width - 28);
        var columns = available >= 900 ? 3 : available >= 520 ? 2 : 1;
        var width = Math.Max(270, (available - (columns - 1) * 12) / columns);
        foreach (Control control in hostsFlow.Controls)
        {
            control.Width = control is Panel ? width : available;
        }
    }

    private static Color VendorColor(string vendor)
    {
        if (vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return Color.FromArgb(118, 231, 80);
        if (vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase)) return Color.FromArgb(255, 107, 94);
        if (vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return Color.FromArgb(93, 189, 255);
        return Accent;
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
        if (webShellRestore is not null)
        {
            webShellRestore();
            return;
        }
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
        if (webShellExit is not null)
        {
            webShellExit();
            return;
        }
        Close();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    private static void SetRedraw(Control control, bool enabled)
    {
        if (!control.IsHandleCreated) return;
        SendMessage(control.Handle, WmSetRedraw, enabled ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
        if (enabled) control.Invalidate(true);
    }

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
        Text = L("نخلة AI", "AI Palm");
        // Keep the application shell stable. Arabic changes text alignment only;
        // it must not mirror the sidebar, content area, or device rail.
        RightToLeft = RightToLeft.No;
        RightToLeftLayout = false;
        ApplyLanguageToControls(Controls);
        ApplyStructuralDirection();
        var modeIndex = mode.SelectedIndex;
        SetModeItems();
        mode.SelectedIndex = modeIndex;
        refreshModels.Text = L("تحديث النماذج", "Refresh models");
        UpdateEngineDetectionStatus(activeEngine.Kind != LocalEngineKind.Unknown);
        refreshNetwork.Text = L("تحديث الشبكة", "Refresh network");
        networkSearch.PlaceholderText = L("ابحث باسم الجهاز أو البطاقة أو النموذج...", "Search models, GPUs, or devices...");
        sendChat.Text = L("إرسال", "Send");
        if (string.IsNullOrWhiteSpace(selectedChatHostId))
        {
            selectedChatDevice.Text = L("اختر جهازاً من صفحة الاستكشاف", "Choose a device from Explore");
            chatState.Text = L("جاهز", "Ready");
        }
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
        UpdateQuickShareVisual();
        if (explorePage is not null && explorePage.Visible && Visible && IsHandleCreated) _ = RefreshNetworkAsync();
    }

    private void ApplyStructuralDirection()
    {
        var arabic = !IsEnglish;
        if (navigationSidebar is not null)
            navigationSidebar.Dock = arabic ? DockStyle.Right : DockStyle.Left;
        if (exploreDeviceRail is not null)
            exploreDeviceRail.Dock = arabic ? DockStyle.Left : DockStyle.Right;
        if (exploreCenter is not null)
            exploreCenter.Padding = arabic ? new Padding(16, 0, 0, 0) : new Padding(0, 0, 16, 0);
        if (exploreRefreshWrap is not null)
        {
            exploreRefreshWrap.Dock = arabic ? DockStyle.Left : DockStyle.Right;
            exploreRefreshWrap.Padding = arabic ? new Padding(0, 6, 10, 38) : new Padding(10, 6, 0, 38);
        }
        if (exploreMetrics is not null)
            exploreMetrics.RightToLeft = arabic ? RightToLeft.Yes : RightToLeft.No;
        hostsFlow.FlowDirection = arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        sendChat.Dock = arabic ? DockStyle.Left : DockStyle.Right;
        if (modelSelectionRow is not null)
        {
            modelSelectionRow.RightToLeft = arabic ? RightToLeft.Yes : RightToLeft.No;
            refreshModels.Margin = arabic ? new Padding(0, 0, 8, 0) : new Padding(8, 0, 0, 0);
        }
        if (shareButtonsRow is not null)
        {
            shareButtonsRow.RightToLeft = arabic ? RightToLeft.Yes : RightToLeft.No;
            stop.Margin = arabic ? new Padding(0, 0, 8, 0) : new Padding(8, 0, 0, 0);
        }

        foreach (Control control in chatMessages.Controls)
            if (control.Tag is ChatMessageView message) ApplyChatMessageDirection(message);

        if (railShareCaption is not null)
        {
            railShareCaption.Location = arabic ? new Point(91, 77) : new Point(20, 77);
            railShareState.Location = arabic ? new Point(91, 106) : new Point(20, 106);
            quickShareToggle.Location = arabic ? new Point(20, 79) : new Point(174, 79);
        }

        rootShell?.PerformLayout();
        explorePage?.PerformLayout();
        chatPage?.PerformLayout();
    }

    private void ApplyChatMessageDirection(ChatMessageView view)
    {
        var arabic = !IsEnglish;
        view.Actions.FlowDirection = arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        foreach (var codePanel in view.Body.Controls.OfType<System.Windows.Forms.Panel>())
        {
            var header = codePanel.Controls.Find("CodeHeader", false).FirstOrDefault();
            if (header is null) continue;
            var languageLabel = header.Controls.Find("CodeLanguageTechnicalLtr", false).FirstOrDefault();
            var copyButton = header.Controls.Find("CodeCopy", false).FirstOrDefault();
            if (languageLabel is not null)
            {
                languageLabel.Dock = arabic ? DockStyle.Right : DockStyle.Left;
                languageLabel.Padding = arabic ? new Padding(0, 0, 12, 0) : new Padding(12, 0, 0, 0);
                if (languageLabel is Label label)
                    label.TextAlign = arabic ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft;
            }
            if (copyButton is not null)
                copyButton.Dock = arabic ? DockStyle.Left : DockStyle.Right;
            header.PerformLayout();
        }
    }

    private void ApplyLanguageToControls(Control.ControlCollection controls)
    {
        foreach (Control control in controls)
        {
            var forceLeftToRight = control.Name.Contains("TechnicalLtr", StringComparison.Ordinal);
            var automaticContentDirection = control.Name.Contains("AutoDirection", StringComparison.Ordinal);
            var layoutContainer = control is System.Windows.Forms.Panel or TableLayoutPanel or FlowLayoutPanel or SplitContainer or TabControl;
            if (!automaticContentDirection)
                control.RightToLeft = forceLeftToRight || IsEnglish || layoutContainer ? RightToLeft.No : RightToLeft.Yes;
            if (control.Tag is LocalizedText text) control.Text = L(text.Arabic, text.English);
            if (!automaticContentDirection && control is TextBox box)
                box.TextAlign = forceLeftToRight && !IsEnglish ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            else if (!automaticContentDirection && control is Label label && label.Text != "ن" && label.TextAlign != ContentAlignment.MiddleCenter)
                label.TextAlign = forceLeftToRight && !IsEnglish ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft;
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

    private static ComboBox MakeComboBox(bool editable)
    {
        return new ComboBox
        {
            BackColor = editable ? Field : Color.FromArgb(238, 242, 239),
            ForeColor = editable ? TextColor : Color.FromArgb(18, 27, 22),
            FlatStyle = FlatStyle.Flat,
            DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI", 10),
        };
    }

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

    private static Button MakeNavButton(string arabic, string english)
    {
        var button = new Button
        {
            Text = english,
            Tag = new LocalizedText(arabic, english),
            Height = 54,
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(16, 0, 16, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(9, 27, 20);
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
            activeEngine = await DetectEngineAsync(modelApiUrl.Text, apiKey.Text, CancellationToken.None);
            UpdateEngineDetectionStatus(true);
            var root = await GetOpenAiJsonAsync(ApiEndpoint(modelApiUrl.Text, "models"), apiKey.Text, CancellationToken.None);
            var names = root["data"]?.AsArray()
                .Select(item => item?["id"]?.GetValue<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .ToArray() ?? [];
            model.Items.Clear();
            model.Items.AddRange(names);
            if (names.Length > 0 && !names.Contains(model.Text)) model.Text = names[0];
            activeContextLength = await DetectContextLengthAsync(activeEngine, modelApiUrl.Text, apiKey.Text, model.Text, CancellationToken.None);
            SetLog($"عُثر على {names.Length} نموذج محلي", $"Found {names.Length} local model(s)");
        }
        catch
        {
            activeEngine = EngineDetection.Unknown(NormalizeEngineRoot(modelApiUrl.Text));
            activeContextLength = null;
            UpdateEngineDetectionStatus(false, true);
            SetLog("تعذر قراءة النماذج. تحقق من عنوان API والمفتاح.", "Could not load models. Check the API URL and key.");
        }
        finally
        {
            refreshModels.Enabled = true;
            refreshModels.Text = L("تحديث النماذج", "Refresh models");
        }
    }

    private async Task<EngineDetection> DetectEngineAsync(string apiBaseUrl, string apiKeyValue, CancellationToken cancellation)
    {
        var root = NormalizeEngineRoot(apiBaseUrl);
        if (string.IsNullOrWhiteSpace(root)) return EngineDetection.Unknown(root);

        var ollamaVersionTask = TryGetEngineJsonAsync(ApiEndpoint(root, "api/version"), apiKeyValue, cancellation);
        var ollamaTagsTask = TryGetEngineJsonAsync(ApiEndpoint(root, "api/tags"), apiKeyValue, cancellation);
        var lmStudioV1Task = TryGetEngineJsonAsync(ApiEndpoint(root, "api/v1/models"), apiKeyValue, cancellation);
        var lmStudioV0Task = TryGetEngineJsonAsync(ApiEndpoint(root, "api/v0/models"), apiKeyValue, cancellation);
        var llamaPropsTask = TryGetEngineJsonAsync(ApiEndpoint(root, "props"), apiKeyValue, cancellation);
        await Task.WhenAll(ollamaVersionTask, ollamaTagsTask, lmStudioV1Task, lmStudioV0Task, llamaPropsTask);

        var ollamaVersion = await ollamaVersionTask;
        var ollamaTags = await ollamaTagsTask;
        if (ollamaVersion?["version"] is JsonValue && ollamaTags?["models"] is JsonArray)
            return new EngineDetection(LocalEngineKind.Ollama, "Ollama", ReadString(ollamaVersion["version"]), root);

        var lmStudioV1 = await lmStudioV1Task;
        if (lmStudioV1?["models"] is JsonArray)
            return new EngineDetection(LocalEngineKind.LmStudioV1, "LM Studio", "v1 API", root);

        var lmStudioV0 = await lmStudioV0Task;
        if (lmStudioV0?["data"] is JsonArray && ReadString(lmStudioV0["object"]) == "list")
            return new EngineDetection(LocalEngineKind.LmStudioLegacy, "LM Studio", "legacy API", root);

        var llamaProps = await llamaPropsTask;
        if (llamaProps?["build_info"] is JsonValue &&
            llamaProps?["default_generation_settings"] is JsonObject &&
            (llamaProps["model_path"] is JsonValue || llamaProps["chat_template"] is JsonValue || llamaProps["total_slots"] is JsonValue))
            return new EngineDetection(LocalEngineKind.LlamaCpp, "llama.cpp", ReadString(llamaProps["build_info"]), root);

        return EngineDetection.Unknown(root);
    }

    internal Task<EngineDetection> DetectEngineForSelfTestAsync(string apiBaseUrl) =>
        DetectEngineAsync(apiBaseUrl, "", CancellationToken.None);

    private async Task<JsonObject?> TryGetEngineJsonAsync(string url, string apiKeyValue, CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKeyValue))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKeyValue);
            using var timeoutCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeoutCancellation.Token);
            using var response = await http.SendAsync(request, linked.Token);
            if (!response.IsSuccessStatusCode) return null;
            var text = await response.Content.ReadAsStringAsync(linked.Token);
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    private async Task<long?> DetectContextLengthAsync(EngineDetection engine, string apiBaseUrl, string apiKeyValue, string modelName, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return null;
        JsonObject? payload = engine.Kind switch
        {
            LocalEngineKind.Ollama => await TryPostEngineJsonAsync(ApiEndpoint(engine.ServerRoot, "api/show"), new { model = modelName }, apiKeyValue, cancellation),
            LocalEngineKind.LlamaCpp => await TryGetEngineJsonAsync(ApiEndpoint(engine.ServerRoot, "props"), apiKeyValue, cancellation),
            LocalEngineKind.LmStudioV1 => await TryGetEngineJsonAsync(ApiEndpoint(engine.ServerRoot, "api/v1/models"), apiKeyValue, cancellation),
            LocalEngineKind.LmStudioLegacy => await TryGetEngineJsonAsync(ApiEndpoint(engine.ServerRoot, "api/v0/models"), apiKeyValue, cancellation),
            _ => await TryGetEngineJsonAsync(ApiEndpoint(apiBaseUrl, "models"), apiKeyValue, cancellation),
        };
        return FindContextLength(payload, modelName);
    }

    private async Task<JsonObject?> TryPostEngineJsonAsync(string url, object body, string apiKeyValue, CancellationToken cancellation)
    {
        try
        {
            return await PostOpenAiJsonAsync(url, body, apiKeyValue, cancellation, TimeSpan.FromSeconds(4));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    private static long? FindContextLength(JsonNode? payload, string modelName)
    {
        if (payload is null) return null;
        var objects = EnumerateJsonObjects(payload).ToArray();
        var matching = objects.Where(item => new[] { "id", "model", "key", "name" }
            .Any(key => string.Equals(ReadString(item[key]), modelName, StringComparison.OrdinalIgnoreCase)));
        foreach (var item in matching.Concat(objects).Distinct())
        {
            foreach (var property in item)
            {
                var key = property.Key.ToLowerInvariant();
                if (key is not ("context_length" or "max_context_length" or "context_window" or "context_size" or "n_ctx" or "n_ctx_train" or "num_ctx") &&
                    !key.EndsWith(".context_length", StringComparison.Ordinal)) continue;
                var value = ReadInt64(property.Value);
                if (value > 0) return value;
            }
        }
        return null;
    }

    private static IEnumerable<JsonObject> EnumerateJsonObjects(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            yield return jsonObject;
            foreach (var child in jsonObject.Select(property => property.Value).Where(value => value is not null))
                foreach (var nested in EnumerateJsonObjects(child!)) yield return nested;
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var child in jsonArray.Where(value => value is not null))
                foreach (var nested in EnumerateJsonObjects(child!)) yield return nested;
        }
    }

    private void UpdateEngineDetectionStatus(bool checkedSuccessfully, bool connectionFailed = false)
    {
        if (connectionFailed)
        {
            engineDetectionStatus.Text = L("تعذر فحص المحرك", "Could not inspect the engine");
            engineDetectionStatus.ForeColor = Danger;
            return;
        }
        if (!checkedSuccessfully)
        {
            engineDetectionStatus.Text = L("سيتم اكتشافه تلقائياً", "Will be detected automatically");
            engineDetectionStatus.ForeColor = Muted;
            return;
        }
        var suffix = string.IsNullOrWhiteSpace(activeEngine.Version) ? "" : $" · {activeEngine.Version}";
        engineDetectionStatus.Text = activeEngine.Kind == LocalEngineKind.Unknown
            ? L("محرك OpenAI-compatible غير معروف · سيتم فحص القدرات", "Unknown OpenAI-compatible engine · capabilities will be checked")
            : $"{activeEngine.Name}{suffix} · {L("تم الكشف تلقائياً", "Detected automatically")}";
        engineDetectionStatus.ForeColor = activeEngine.Kind == LocalEngineKind.Unknown ? Accent : Color.FromArgb(143, 236, 190);
    }

    private static string NormalizeEngineRoot(string apiBaseUrl)
    {
        var value = apiBaseUrl.Trim().TrimEnd('/');
        foreach (var suffix in new[] { "/api/v1", "/api/v0", "/v1" })
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return value[..^suffix.Length].TrimEnd('/');
        return value;
    }

    private static string ReadString(JsonNode? node)
    {
        try { return node?.GetValue<string>() ?? ""; }
        catch (InvalidOperationException) { return ""; }
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

        if (activeSettings.Mode == "openai")
        {
            activeEngine = await DetectEngineAsync(activeSettings.ApiUrl, activeSettings.ApiKey, CancellationToken.None);
            activeContextLength = await DetectContextLengthAsync(activeEngine, activeSettings.ApiUrl, activeSettings.ApiKey, activeSettings.Model, CancellationToken.None);
            UpdateEngineDetectionStatus(true);
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
                    context_length = activeContextLength,
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
            _ = RefreshNetworkAsync();
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
                var messages = JobMessages(job, prompt);
                SafeUi(() =>
                {
                    SetStatus("يتم تنفيذ مهمة الآن", "Running a task", $"المهمة: {jobId}", $"Task: {jobId}", Color.Goldenrod);
                    SetLog($"بدأ تنفيذ المهمة {jobId}", $"Started task {jobId}");
                });
                try
                {
                    var result = await RunTaskWithHeartbeatAsync(prompt, messages, jobId, cancellation);
                    // The answer has already been streamed to the coordinator. Avoid sending a potentially
                    // very large duplicate payload when marking the job complete.
                    await PostJsonAsync($"{activeSettings.ServerUrl}/api/jobs/{jobId}/complete", new { result = "" }, hostToken, cancellation);
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

    private static JsonArray JobMessages(JsonObject job, string prompt)
    {
        if (job["messages"] is JsonArray supplied && supplied.Count > 0)
            return JsonNode.Parse(supplied.ToJsonString())?.AsArray() ?? [];
        return new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt });
    }

    private async Task<string> RunOpenAiCompatibleAsync(string prompt, JsonArray messages, string jobId, CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint(activeSettings.ApiUrl, "chat/completions"));
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = activeSettings.Model,
                stream = true,
                stream_options = new { include_usage = true },
                messages,
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
                var message = FirstChoice(payload)?["message"];
                var rawText = ReadString(message?["content"]);
                var embedded = new ReasoningContentAccumulator().Append(rawText, true);
                var fullText = embedded.Answer;
                if (string.IsNullOrEmpty(fullText)) fullText = L("لم يُرجع النموذج إجابة", "The model returned no answer");
                var fullThinking = ReadString(message?["reasoning_content"]);
                if (string.IsNullOrEmpty(fullThinking)) fullThinking = ReadString(message?["reasoning"]);
                if (string.IsNullOrEmpty(fullThinking)) fullThinking = ReadString(message?["thinking"]);
                if (!string.IsNullOrWhiteSpace(embedded.Thinking)) fullThinking = $"{fullThinking}{embedded.Thinking}";
                var usage = CreateEngineUsage(payload["usage"] as JsonObject, payload["stats"] as JsonObject, payload["timings"] as JsonObject);
                await PublishStreamAsync(jobId, fullText, usage, linked.Token, fullThinking);
                return fullText;
            }

            var fullResult = new StringBuilder();
            var pending = new StringBuilder();
            var pendingThinking = new StringBuilder();
            var embeddedReasoning = new ReasoningContentAccumulator();
            JsonObject? exactUsage = null;
            JsonObject? exactStats = null;
            JsonObject? exactTimings = null;
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
                var choiceDelta = FirstChoice(chunk)?["delta"];
                var delta = ReadString(choiceDelta?["content"]);
                var thinkingDelta = ReadString(choiceDelta?["reasoning_content"]);
                if (string.IsNullOrEmpty(thinkingDelta)) thinkingDelta = ReadString(choiceDelta?["reasoning"]);
                if (string.IsNullOrEmpty(thinkingDelta)) thinkingDelta = ReadString(choiceDelta?["thinking"]);
                if (!string.IsNullOrEmpty(delta))
                {
                    var extracted = embeddedReasoning.Append(delta);
                    fullResult.Append(extracted.Answer);
                    pending.Append(extracted.Answer);
                    pendingThinking.Append(extracted.Thinking);
                }
                if (!string.IsNullOrEmpty(thinkingDelta)) pendingThinking.Append(thinkingDelta);
                if (chunk?["usage"] is JsonObject returnedUsage) exactUsage = returnedUsage;
                if (chunk?["stats"] is JsonObject returnedStats) exactStats = returnedStats;
                if (chunk?["timings"] is JsonObject returnedTimings) exactTimings = returnedTimings;

                if (pending.Length >= 64 || pendingThinking.Length >= 64)
                {
                    await PublishStreamAsync(jobId, pending.ToString(), null, linked.Token, pendingThinking.ToString());
                    pending.Clear();
                    pendingThinking.Clear();
                }
            }
            var embeddedTail = embeddedReasoning.Flush();
            fullResult.Append(embeddedTail.Answer);
            pending.Append(embeddedTail.Answer);
            pendingThinking.Append(embeddedTail.Thinking);
            if (pending.Length > 0 || pendingThinking.Length > 0)
                await PublishStreamAsync(jobId, pending.ToString(), null, linked.Token, pendingThinking.ToString());

            var finalText = fullResult.ToString();
            var finalUsage = CreateEngineUsage(exactUsage, exactStats, exactTimings);
            await PublishStreamAsync(jobId, "", finalUsage, linked.Token);
            return string.IsNullOrEmpty(finalText) ? L("لم يُرجع النموذج إجابة", "The model returned no answer") : finalText;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException(L("انتهت مهلة انتظار خادم النموذج بعد 30 دقيقة", "The model server timed out after 30 minutes"));
        }
    }

    private async Task<string> RunOllamaNativeAsync(string prompt, JsonArray messages, string jobId, CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint(activeEngine.ServerRoot, "api/chat"));
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = activeSettings.Model,
                stream = true,
                messages,
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

            var fullResult = new StringBuilder();
            var pending = new StringBuilder();
            var pendingThinking = new StringBuilder();
            var embeddedReasoning = new ReasoningContentAccumulator();
            JsonObject? exactUsage = null;
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            using var reader = new StreamReader(stream);
            while (true)
            {
                var line = await reader.ReadLineAsync(linked.Token);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonObject? chunk;
                try { chunk = JsonNode.Parse(line)?.AsObject(); }
                catch (JsonException) { continue; }
                var delta = chunk?["message"]?["content"]?.GetValue<string>();
                var thinkingDelta = ReadString(chunk?["message"]?["thinking"]);
                if (!string.IsNullOrEmpty(delta))
                {
                    var extracted = embeddedReasoning.Append(delta);
                    fullResult.Append(extracted.Answer);
                    pending.Append(extracted.Answer);
                    pendingThinking.Append(extracted.Thinking);
                }
                if (!string.IsNullOrEmpty(thinkingDelta)) pendingThinking.Append(thinkingDelta);
                if (chunk?["done"]?.GetValue<bool?>() == true)
                    exactUsage = CreateOllamaUsage(chunk);
                if (pending.Length >= 64 || pendingThinking.Length >= 64)
                {
                    await PublishStreamAsync(jobId, pending.ToString(), null, linked.Token, pendingThinking.ToString());
                    pending.Clear();
                    pendingThinking.Clear();
                }
            }
            var embeddedTail = embeddedReasoning.Flush();
            fullResult.Append(embeddedTail.Answer);
            pending.Append(embeddedTail.Answer);
            pendingThinking.Append(embeddedTail.Thinking);
            if (pending.Length > 0 || pendingThinking.Length > 0)
                await PublishStreamAsync(jobId, pending.ToString(), null, linked.Token, pendingThinking.ToString());
            await PublishStreamAsync(jobId, "", exactUsage, linked.Token);
            var finalText = fullResult.ToString();
            return string.IsNullOrEmpty(finalText) ? L("لم يُرجع النموذج إجابة", "The model returned no answer") : finalText;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException(L("انتهت مهلة انتظار خادم النموذج بعد 30 دقيقة", "The model server timed out after 30 minutes"));
        }
    }

    private async Task<string> RunLmStudioV1Async(string prompt, string jobId, CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint(activeEngine.ServerRoot, "api/v1/chat"));
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = activeSettings.Model,
                input = prompt,
                stream = true,
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

            var fullResult = new StringBuilder();
            var pending = new StringBuilder();
            var pendingThinking = new StringBuilder();
            var embeddedReasoning = new ReasoningContentAccumulator();
            JsonObject? exactUsage = null;
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            using var reader = new StreamReader(stream);
            while (true)
            {
                var line = await reader.ReadLineAsync(linked.Token);
                if (line is null) break;
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                var data = line[5..].Trim();
                if (string.IsNullOrWhiteSpace(data)) continue;
                JsonObject? chunk;
                try { chunk = JsonNode.Parse(data)?.AsObject(); }
                catch (JsonException) { continue; }

                var eventType = ReadString(chunk?["type"]);
                if (eventType == "message.delta")
                {
                    var delta = ReadString(chunk?["content"]);
                    if (!string.IsNullOrEmpty(delta))
                    {
                        var extracted = embeddedReasoning.Append(delta);
                        fullResult.Append(extracted.Answer);
                        pending.Append(extracted.Answer);
                        pendingThinking.Append(extracted.Thinking);
                    }
                }
                else if (eventType == "reasoning.delta")
                {
                    var reasoning = ReadString(chunk?["content"]);
                    if (!string.IsNullOrEmpty(reasoning)) pendingThinking.Append(reasoning);
                }
                else if (eventType == "chat.end")
                {
                    exactUsage = CreateEngineUsage(null, chunk?["result"]?["stats"] as JsonObject, null);
                }
                else if (eventType == "error")
                {
                    var message = ReadString(chunk?["error"]?["message"]);
                    if (!string.IsNullOrWhiteSpace(message)) throw new HttpRequestException(message);
                }

                if (pending.Length >= 64 || pendingThinking.Length >= 64)
                {
                    await PublishStreamAsync(jobId, pending.ToString(), null, linked.Token, pendingThinking.ToString());
                    pending.Clear();
                    pendingThinking.Clear();
                }
            }
            var embeddedTail = embeddedReasoning.Flush();
            fullResult.Append(embeddedTail.Answer);
            pending.Append(embeddedTail.Answer);
            pendingThinking.Append(embeddedTail.Thinking);
            if (pending.Length > 0 || pendingThinking.Length > 0)
                await PublishStreamAsync(jobId, pending.ToString(), null, linked.Token, pendingThinking.ToString());
            await PublishStreamAsync(jobId, "", exactUsage, linked.Token);
            var finalText = fullResult.ToString();
            return string.IsNullOrEmpty(finalText) ? L("لم يُرجع النموذج إجابة", "The model returned no answer") : finalText;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException(L("انتهت مهلة انتظار خادم النموذج بعد 30 دقيقة", "The model server timed out after 30 minutes"));
        }
    }

    private async Task<string> RunTaskWithHeartbeatAsync(string prompt, JsonArray messages, string jobId, CancellationToken cancellation)
    {
        var inference = activeSettings.Mode == "openai"
            ? activeEngine.Kind == LocalEngineKind.Ollama
                ? RunOllamaNativeAsync(prompt, messages, jobId, cancellation)
                : activeEngine.Kind == LocalEngineKind.LmStudioV1
                    ? RunOpenAiCompatibleAsync(prompt, messages, jobId, cancellation)
                : RunOpenAiCompatibleAsync(prompt, messages, jobId, cancellation)
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
            await PublishStreamAsync(jobId, delta, CreateEstimatedUsage(prompt, accumulated.ToString(), timer.ElapsedMilliseconds), cancellation);
            await Task.Delay(70, cancellation);
        }
        return result;
    }

    private async Task PublishStreamAsync(string jobId, string delta, JsonObject? usage, CancellationToken cancellation, string thinkingDelta = "")
    {
        const int safeChunkSize = 16_000;
        var answerOffset = 0;
        var thinkingOffset = 0;
        do
        {
            var answerLength = Math.Min(safeChunkSize, Math.Max(0, delta.Length - answerOffset));
            var thinkingLength = Math.Min(safeChunkSize, Math.Max(0, thinkingDelta.Length - thinkingOffset));
            var answerPart = answerLength == 0 ? "" : delta.Substring(answerOffset, answerLength);
            var thinkingPart = thinkingLength == 0 ? "" : thinkingDelta.Substring(thinkingOffset, thinkingLength);
            answerOffset += answerLength;
            thinkingOffset += thinkingLength;
            var isLast = answerOffset >= delta.Length && thinkingOffset >= thinkingDelta.Length;
            await PostStreamChunkAsync(jobId, answerPart, thinkingPart, isLast ? usage : null, cancellation);
        }
        while (answerOffset < delta.Length || thinkingOffset < thinkingDelta.Length);
    }

    private async Task PostStreamChunkAsync(string jobId, string delta, string thinkingDelta, JsonObject? usage, CancellationToken cancellation)
    {
        var url = $"{activeSettings.ServerUrl}/api/jobs/{jobId}/stream";
        if (string.IsNullOrEmpty(thinkingDelta))
        {
            await PostJsonAsync(url, new { delta, usage }, hostToken, cancellation, TimeSpan.FromSeconds(15));
            return;
        }

        try
        {
            await PostJsonAsync(url, new { delta, thinking_delta = thinkingDelta, usage }, hostToken, cancellation, TimeSpan.FromSeconds(15));
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.UnprocessableEntity)
        {
            // An older server does not know the reasoning field yet. Keep the answer
            // alive and carry reasoning in a backward-compatible encoded marker.
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(thinkingDelta));
            await PostJsonAsync(url, new { delta = $"[[AI_PALM_REASONING_BASE64:{encoded}]]{delta}", usage }, hostToken, cancellation, TimeSpan.FromSeconds(15));
        }
    }

    private static (string Answer, string Thinking) ExtractCompatibilityReasoning(string value)
    {
        var thinking = new StringBuilder();
        var answer = Regex.Replace(value, @"\[\[AI_PALM_REASONING_BASE64:(?<data>[A-Za-z0-9+/=]+)\]\]", match =>
        {
            try { thinking.Append(Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups["data"].Value))); }
            catch (FormatException) { }
            return "";
        }, RegexOptions.CultureInvariant);
        return (answer, thinking.ToString());
    }

    private static JsonObject? CreateEngineUsage(JsonObject? usage, JsonObject? stats, JsonObject? timings)
    {
        var promptTokens = ReadInt64(usage?["prompt_tokens"]) ?? ReadInt64(stats?["input_tokens"]) ?? ReadInt64(timings?["prompt_n"]);
        var completionTokens = ReadInt64(usage?["completion_tokens"]) ?? ReadInt64(stats?["total_output_tokens"]) ?? ReadInt64(timings?["predicted_n"]);
        if (promptTokens is null && completionTokens is null) return null;
        var totalTokens = ReadInt64(usage?["total_tokens"]) ?? (promptTokens ?? 0) + (completionTokens ?? 0);
        var speed = ReadDouble(stats?["tokens_per_second"]) ?? ReadDouble(timings?["predicted_per_second"]);
        return new JsonObject
        {
            ["prompt_tokens"] = promptTokens ?? 0,
            ["completion_tokens"] = completionTokens ?? 0,
            ["total_tokens"] = totalTokens,
            ["tokens_per_second"] = speed,
            ["estimated"] = false,
        };
    }

    private static JsonObject? FirstChoice(JsonObject? payload)
    {
        if (payload?["choices"] is not JsonArray choices || choices.Count == 0) return null;
        return choices[0] as JsonObject;
    }

    internal static bool IsUsageOnlyChunkHandledForSelfTest()
    {
        var usageOnlyChunk = new JsonObject
        {
            ["choices"] = new JsonArray(),
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = 7,
                ["completion_tokens"] = 11,
                ["total_tokens"] = 18,
            },
        };
        return FirstChoice(usageOnlyChunk) is null &&
            CreateEngineUsage(usageOnlyChunk["usage"] as JsonObject, null, null)?["total_tokens"]?.ToString() == "18";
    }

    internal static bool IsReasoningStreamHandledForSelfTest()
    {
        var accumulator = new ReasoningContentAccumulator();
        var parts = new[] { "<thi", "nk>أحلل ", "السؤال</thi", "nk>الجواب النهائي" };
        var answer = new StringBuilder();
        var thinking = new StringBuilder();
        foreach (var part in parts)
        {
            var extracted = accumulator.Append(part);
            answer.Append(extracted.Answer);
            thinking.Append(extracted.Thinking);
        }
        var tail = accumulator.Flush();
        answer.Append(tail.Answer);
        thinking.Append(tail.Thinking);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("تفكير قديم"));
        var compatibility = ExtractCompatibilityReasoning($"[[AI_PALM_REASONING_BASE64:{encoded}]]جواب");
        return answer.ToString() == "الجواب النهائي" &&
            thinking.ToString() == "أحلل السؤال" &&
            compatibility.Answer == "جواب" &&
            compatibility.Thinking == "تفكير قديم";
    }

    private static JsonObject? CreateOllamaUsage(JsonObject? response)
    {
        var promptTokens = ReadInt64(response?["prompt_eval_count"]);
        var completionTokens = ReadInt64(response?["eval_count"]);
        if (promptTokens is null && completionTokens is null) return null;
        var evalDuration = ReadDouble(response?["eval_duration"]);
        var speed = completionTokens is > 0 && evalDuration is > 0
            ? Math.Round(completionTokens.Value / (evalDuration.Value / 1_000_000_000d), 1)
            : (double?)null;
        return new JsonObject
        {
            ["prompt_tokens"] = promptTokens ?? 0,
            ["completion_tokens"] = completionTokens ?? 0,
            ["total_tokens"] = (promptTokens ?? 0) + (completionTokens ?? 0),
            ["tokens_per_second"] = speed,
            ["estimated"] = false,
        };
    }

    private static JsonObject CreateEstimatedUsage(string prompt, string result, long elapsedMilliseconds)
    {
        var promptTokens = EstimateTokens(prompt);
        var completionTokens = EstimateTokens(result);
        var seconds = Math.Max(elapsedMilliseconds / 1000d, 0.001);
        return new JsonObject
        {
            ["prompt_tokens"] = promptTokens,
            ["completion_tokens"] = completionTokens,
            ["total_tokens"] = promptTokens + completionTokens,
            ["tokens_per_second"] = elapsedMilliseconds > 0 ? Math.Round(completionTokens / seconds, 1) : null,
            ["estimated"] = true,
        };
    }

    private static long? ReadInt64(JsonNode? node)
    {
        if (node is null) return null;
        if (long.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer;
        if (double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var floating) &&
            floating >= long.MinValue && floating <= long.MaxValue)
            return checked((long)floating);
        return null;
    }

    private static double? ReadDouble(JsonNode? node)
    {
        if (node is null) return null;
        return double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
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
        _ = RefreshNetworkAsync();
    }

    private async Task<JsonObject> GetJsonAsync(string url, string? token, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null) request.Headers.Add("X-Host-Token", token);
        using var timeoutCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeoutCancellation.Token);
        using var response = await http.SendAsync(request, linked.Token);
        var text = await response.Content.ReadAsStringAsync(linked.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(ReadApiError(text), null, response.StatusCode);
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
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(ReadApiError(text), null, response.StatusCode);
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
        suppressQuickToggle = true;
        quickShareToggle.Checked = connectedState;
        suppressQuickToggle = false;
        railModel.Text = string.IsNullOrWhiteSpace(model.Text) ? "—" : model.Text;
        UpdateQuickShareVisual();
    }

    private void UpdateQuickShareVisual()
    {
        var active = quickShareToggle.Checked;
        quickShareToggle.Text = active ? L("إيقاف", "Stop") : L("تشغيل", "Start");
        quickShareToggle.BackColor = active ? Color.FromArgb(41, 192, 111) : Field;
        quickShareToggle.ForeColor = active ? Color.FromArgb(4, 25, 14) : TextColor;
        railShareState.Text = active ? L("متصل", "Connected") : L("المشاركة متوقفة", "Sharing is off");
        railShareState.ForeColor = active ? Color.FromArgb(102, 230, 153) : Muted;
        railActivity.Text = active ? L("جاهز لتنفيذ مهمة", "Ready to infer") : L("خامل", "Idle");
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
        railActivity.Text = title;
        railActivity.ForeColor = color;
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

    private static Image? LoadBrandImage()
    {
        try
        {
            using var stream = typeof(HostForm).Assembly.GetManifestResourceStream("AIPalm.BrandMark");
            if (stream is null) return null;
            using var source = Image.FromStream(stream);
            return new Bitmap(source);
        }
        catch
        {
            return null;
        }
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
        if (disposing)
        {
            networkTimer.Stop();
            networkTimer.Dispose();
            trayIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
