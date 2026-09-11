using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NawahHost;

namespace AIPalmWeb;

internal static class WebProgram
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var host = new HostForm();
        _ = host.Handle;
        using var shell = new WebShellForm(host, args);
        host.EnableWebShellMode(shell.RestoreWindow, shell.ExitApplication);
        Application.Run(shell);
    }
}

internal sealed class WebShellForm : Form
{
    private const string AppVersion = "0.1.7";
    private const string UiHostName = "app-v017.aipalm.local";
    private readonly HostForm host;
    private readonly string[] arguments;
    private readonly WebView2 browser = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(6, 18, 13) };
    private bool exitRequested;

    internal WebShellForm(HostForm host, string[] arguments)
    {
        this.host = host;
        this.arguments = arguments;
        Text = "AI Palm Web";
        ClientSize = new Size(1440, 900);
        MinimumSize = new Size(1040, 700);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(6, 18, 13);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Controls.Add(browser);
        Shown += async (_, _) => await InitializeAsync();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) HideToTray(); };
        FormClosing += OnFormClosing;
    }

    private async Task InitializeAsync()
    {
        try
        {
            var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI Palm Web", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await browser.EnsureCoreWebView2Async(environment);
            browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            var uiFolder = ExtractUi();
            browser.CoreWebView2.SetVirtualHostNameToFolderMapping(UiHostName, uiFolder, CoreWebView2HostResourceAccessKind.DenyCors);
            browser.Source = new Uri($"https://{UiHostName}/index.html");
            await RunSelfTestIfRequestedAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not start the web interface.\n\n{ex.Message}\n\nInstall the Microsoft Edge WebView2 Runtime and try again.", "AI Palm Web", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        string id = "";
        try
        {
            var request = JsonNode.Parse(eventArgs.WebMessageAsJson)?.AsObject() ?? new JsonObject();
            id = request["id"]?.GetValue<string>() ?? "";
            var action = request["action"]?.GetValue<string>() ?? "";
            var payload = request["payload"] as JsonObject ?? new JsonObject();
            object? result = action switch
            {
                "state" => host.GetWebShellState(),
                "saveSettings" => SaveSettings(payload),
                "refreshModels" => await host.RefreshModelsForWebShellAsync(),
                "startSharing" => await host.StartSharingForWebShellAsync(),
                "stopSharing" => await host.StopSharingForWebShellAsync(),
                "apiGet" => await host.WebShellApiGetAsync(payload["path"]?.GetValue<string>() ?? ""),
                "apiPost" => await host.WebShellApiPostAsync(payload["path"]?.GetValue<string>() ?? "", payload["body"]),
                "minimize" => MinimizeWindow(),
                _ => throw new InvalidOperationException($"Unknown bridge action: {action}"),
            };
            PostResponse(id, true, result, null);
        }
        catch (Exception ex)
        {
            PostResponse(id, false, null, ex.Message);
        }
    }

    private object SaveSettings(JsonObject payload)
    {
        host.ApplyWebShellSettings(payload);
        return host.GetWebShellState();
    }

    private object MinimizeWindow()
    {
        HideToTray();
        return new { hidden = true };
    }

    private void PostResponse(string id, bool ok, object? data, string? error)
    {
        if (browser.CoreWebView2 is null) return;
        browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok, data, error }));
    }

    private static string ExtractUi()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI Palm Web", "ui", AppVersion);
        Directory.CreateDirectory(folder);
        var assembly = Assembly.GetExecutingAssembly();
        var files = new Dictionary<string, string>
        {
            ["index.html"] = "AIPalmWeb.UI.index.html",
            ["styles.css"] = "AIPalmWeb.UI.styles.css",
            ["layout-fixes.css"] = "AIPalmWeb.UI.layout-fixes.css",
            ["app.js"] = "AIPalmWeb.UI.app.js",
            ["ai-palm-mark.png"] = "AIPalm.BrandMark",
        };
        foreach (var (fileName, resourceName) in files)
        {
            using var source = assembly.GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException($"Missing interface resource: {resourceName}");
            using var target = File.Create(Path.Combine(folder, fileName));
            source.CopyTo(target);
        }
        return folder;
    }

    private async Task RunSelfTestIfRequestedAsync()
    {
        var index = Array.IndexOf(arguments, "--self-test");
        if (index < 0 || index + 1 >= arguments.Length) return;
        var output = Path.GetFullPath(arguments[index + 1]);
        var interfaceReady = false;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            try
            {
                interfaceReady = string.Equals(await browser.ExecuteScriptAsync("document.body?.dataset?.ready === 'true'"), "true", StringComparison.OrdinalIgnoreCase);
                if (interfaceReady) break;
            }
            catch (InvalidOperationException) { }
            await Task.Delay(250);
        }
        if (!interfaceReady) throw new TimeoutException("The web interface did not become ready for its self-test.");
        await Task.Delay(150);
        var viewIndex = Array.IndexOf(arguments, "--view");
        if (viewIndex >= 0 && viewIndex + 1 < arguments.Length)
        {
            var view = JsonSerializer.Serialize(arguments[viewIndex + 1]);
            await browser.ExecuteScriptAsync($"showView({view})");
            await Task.Delay(250);
        }
        var composerResult = await browser.ExecuteScriptAsync("""
            (() => {
              selectedHost = { id: "self-test", name: "Test device", model: "Test model", gpu: "Test GPU", context_length: 100 };
              updateContextBudget();
              const contextStartsHealthy = contextBudget.dataset.level === "healthy";
              setComposerBusy(true);
              const writableWhileBusy = !promptInput.disabled && sendButton.disabled;
              promptInput.value = "next message";
              setComposerBusy(false);
              const readyAfterCompletion = !promptInput.disabled && !sendButton.disabled && promptInput.value === "next message";
              addTurn("first");
              const firstBody = activeAnswerBody;
              renderJob({ id: "first", status: "completed", result: "first answer" });
              addTurn("second");
              const secondBody = activeAnswerBody;
              setComposerBusy(true);
              renderJob({ id: "second", status: "running", result: "partial answer", usage: { prompt_tokens: 60, completion_tokens: 20, total_tokens: 80, estimated: true } });
              const copyDisabledWhileGenerating = secondBody.querySelector("[data-copy-answer]")?.disabled === true;
              const progressVisibleWhileGenerating = chatLive.classList.contains("generating");
              const contextTurnsOrange = contextBudget.dataset.level === "low" && contextBudgetText.textContent.includes("20 / 100");
              renderJob({ id: "second", status: "completed", result: "second answer", usage: { prompt_tokens: 70, completion_tokens: 25, total_tokens: 95, tokens_per_second: 17.5 } });
              const contextTurnsRed = contextBudget.dataset.level === "critical" && contextBudgetText.textContent.includes("5 / 100");
              const contextMeterUsesRemaining = contextMeterFill.style.width === "5%";
              const contextMeterIsRed = getComputedStyle(contextMeterFill).backgroundColor === "rgb(239, 85, 76)";
              activeJob = null;
              updateContextBudget();
              const contextPersistsAfterCompletion = !contextBudget.hidden && contextBudgetText.textContent.includes("5 / 100") && contextMeterFill.style.width === "5%";
              setComposerBusy(false);
              renderState({ ...appState, engine: { ...(appState?.engine || {}), contextLength: 32768 } });
              applyLanguage("ar");
              const sharingBadgeLocalized = sharingBadge.textContent === "إيقاف";
              return {
                platformUrlHidden: !document.getElementById("serverUrl"),
                sharingBadgeLocalized,
                writableWhileBusy,
                readyAfterCompletion,
                repliesStaySeparate: firstBody !== secondBody && firstBody.textContent.includes("first answer") && secondBody.textContent.includes("second answer"),
                copyActionVisible: Boolean(secondBody.querySelector("[data-copy-answer]") && !secondBody.querySelector("[data-copy-answer]").disabled),
                copyDisabledWhileGenerating,
                progressVisibleWhileGenerating,
                contextStartsHealthy,
                contextTurnsOrange,
                contextTurnsRed,
                contextMeterUsesRemaining,
                contextMeterIsRed,
                contextPersistsAfterCompletion,
                contextLengthVisible: railContext.textContent.includes("32,768"),
                tokenUsageVisible: secondBody.querySelectorAll(".usage-metric").length === 4 && secondBody.textContent.includes("95") && secondBody.textContent.includes("17.5"),
                enterSends: shouldSubmitPrompt({ key: "Enter", shiftKey: false, isComposing: false }),
                shiftEnterAddsLine: !shouldSubmitPrompt({ key: "Enter", shiftKey: true, isComposing: false })
              };
            })()
            """);
        var composer = JsonSerializer.Deserialize<JsonElement>(composerResult);
        var title = await browser.ExecuteScriptAsync("document.title");
        var screenshotIndex = Array.IndexOf(arguments, "--screenshot");
        if (screenshotIndex >= 0 && screenshotIndex + 1 < arguments.Length)
        {
            var requestedView = viewIndex >= 0 && viewIndex + 1 < arguments.Length ? arguments[viewIndex + 1] : "explore";
            await PrepareDocumentationScreenshotAsync(requestedView);
            await Task.Delay(250);
            await using var stream = File.Create(Path.GetFullPath(arguments[screenshotIndex + 1]));
            await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        }
        var composerOk = composer.TryGetProperty("platformUrlHidden", out var platformHidden) && platformHidden.GetBoolean()
            && composer.TryGetProperty("sharingBadgeLocalized", out var localizedBadge) && localizedBadge.GetBoolean()
            && composer.TryGetProperty("writableWhileBusy", out var writable) && writable.GetBoolean()
            && composer.TryGetProperty("readyAfterCompletion", out var ready) && ready.GetBoolean()
            && composer.TryGetProperty("repliesStaySeparate", out var separate) && separate.GetBoolean()
            && composer.TryGetProperty("copyActionVisible", out var copy) && copy.GetBoolean()
            && composer.TryGetProperty("copyDisabledWhileGenerating", out var copyDisabled) && copyDisabled.GetBoolean()
            && composer.TryGetProperty("progressVisibleWhileGenerating", out var progress) && progress.GetBoolean()
            && composer.TryGetProperty("contextStartsHealthy", out var contextHealthy) && contextHealthy.GetBoolean()
            && composer.TryGetProperty("contextTurnsOrange", out var contextOrange) && contextOrange.GetBoolean()
            && composer.TryGetProperty("contextTurnsRed", out var contextRed) && contextRed.GetBoolean()
            && composer.TryGetProperty("contextMeterUsesRemaining", out var meterRemaining) && meterRemaining.GetBoolean()
            && composer.TryGetProperty("contextMeterIsRed", out var meterRed) && meterRed.GetBoolean()
            && composer.TryGetProperty("contextPersistsAfterCompletion", out var contextPersists) && contextPersists.GetBoolean()
            && composer.TryGetProperty("contextLengthVisible", out var context) && context.GetBoolean()
            && composer.TryGetProperty("tokenUsageVisible", out var usage) && usage.GetBoolean()
            && composer.TryGetProperty("enterSends", out var enter) && enter.GetBoolean()
            && composer.TryGetProperty("shiftEnterAddsLine", out var shiftEnter) && shiftEnter.GetBoolean();
        File.WriteAllText(output, JsonSerializer.Serialize(new { ok = composerOk, title = JsonSerializer.Deserialize<string>(title), webView = true, version = AppVersion, composer }));
        exitRequested = true;
        BeginInvoke(Application.Exit);
    }

    private async Task PrepareDocumentationScreenshotAsync(string view)
    {
        if (view == "explore")
        {
            await browser.ExecuteScriptAsync("""
                (() => {
                  applyLanguage("en");
                  networkHosts = [
                    { id: "atlas", name: "Atlas", gpu: "NVIDIA GeForce RTX 4090", vram_gb: 24, model: "Llama 3.3 70B", context_length: 131072 },
                    { id: "tiger", name: "Tiger", gpu: "AMD Radeon RX 7900 XTX", vram_gb: 24, model: "Qwen3 32B", context_length: 32768 },
                    { id: "aurora", name: "Aurora", gpu: "Intel Arc A770", vram_gb: 16, model: "Mistral Small 24B", context_length: 32768 },
                    { id: "cedar", name: "Cedar", gpu: "NVIDIA GeForce RTX 4080", vram_gb: 16, model: "Qwen2.5 Coder 32B", context_length: 65536 }
                  ];
                  onlineMetric.textContent = "4";
                  modelMetric.textContent = "4";
                  registeredMetric.textContent = "12";
                  networkCaption.textContent = "4 online devices ready for a new chat";
                  searchInput.value = "";
                  renderHosts();
                  showView("explore");
                })()
                """);
            return;
        }

        if (view == "chat")
        {
            await browser.ExecuteScriptAsync("""
                (() => {
                  applyLanguage("ar");
                  showView("chat");
                  conversation.innerHTML = "";
                  selectedHost = { id: "tiger", name: "Tiger", model: "Qwen3 32B", gpu: "AMD Radeon RX 7900 XTX", context_length: 32768 };
                  conversationHostId = selectedHost.id;
                  contextBudgetState = { hostId: selectedHost.id, used: 0, estimated: false };
                  chatDevice.textContent = "Tiger · Qwen3 32B · AMD Radeon RX 7900 XTX";
                  addTurn("اكتب مثالاً بسيطاً بلغة Python يوضح كيفية الاتصال بواجهة API.");
                  renderJob({
                    id: "docs-chat",
                    status: "completed",
                    messages: [{ role: "user", content: "اكتب مثالاً بسيطاً بلغة Python يوضح كيفية الاتصال بواجهة API." }],
                    thinking: "سأقدّم مثالاً قصيراً وآمناً مع معالجة واضحة للاستجابة.",
                    result: "يمكنك تمرير عنوان الخدمة إلى مكتبة requests بهذا الشكل:\n\n```python\nimport requests\n\nresponse = requests.get(endpoint)\nresponse.raise_for_status()\nprint(response.json())\n```\n\nيتحقق المثال من نجاح الطلب قبل قراءة JSON.",
                    usage: { prompt_tokens: 3120, completion_tokens: 1228, total_tokens: 4348, tokens_per_second: 38.6 }
                  });
                  setComposerBusy(false);
                  promptInput.value = "";
                  chatStatus.textContent = t("ready");
                  requestAnimationFrame(() => conversation.scrollTop = 0);
                })()
                """);
            return;
        }

        await browser.ExecuteScriptAsync("""
            (() => {
              applyLanguage("ar");
              renderState({
                ...appState,
                sharing: true,
                settings: { ...appState.settings, deviceName: "Tiger", apiUrl: "http://127.0.0.1:8080/v1", model: "Qwen3.8-27B" },
                models: ["Qwen3.8-27B", "Qwen2.5-Coder-32B"],
                gpu: { name: "AMD Radeon RX 9070 XT", vramGb: 16 },
                engine: { name: "llama.cpp", contextLength: 131072 },
                status: "متصل — المشاركة مفعلة",
                detail: "النموذج: Qwen3.8-27B"
              });
              fillSettings(appState);
              showView("share");
            })()
            """);
    }

    internal void RestoreWindow()
    {
        if (InvokeRequired) { BeginInvoke(RestoreWindow); return; }
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    internal void ExitApplication()
    {
        if (InvokeRequired) { BeginInvoke(ExitApplication); return; }
        exitRequested = true;
        Application.Exit();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (exitRequested || eventArgs.CloseReason is CloseReason.WindowsShutDown or CloseReason.ApplicationExitCall) return;
        eventArgs.Cancel = true;
        HideToTray();
    }
}
