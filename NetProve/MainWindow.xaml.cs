using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NetProve.Core;
using NetProve.Engines;
using NetProve.Helpers;
using NetProve.Localization;
using NetProve.Models;
using NetProve.Themes;
using NetProve.ViewModels;

namespace NetProve
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private readonly CoreEngine _engine = CoreEngine.Instance;
        private readonly LocalizationManager _loc = LocalizationManager.Instance;

        // Map nav tag → page panel
        private Dictionary<string, UIElement> _pages = new();
        private string _currentPage = "Dashboard";
        private DispatcherTimer? _toastTimer;
        private DispatcherTimer? _lagBannerTimer;
        private SystemTrayHelper? _tray;
        private readonly NetworkScanner _scanner = new();

        public MainWindow()
        {
            InitializeComponent();

            try { Icon = new BitmapImage(new Uri("pack://application:,,,/app.ico")); }
            catch { /* icon not critical */ }

            _vm = new MainViewModel();

            // System tray setup
            _tray = new SystemTrayHelper(this);

            SubscribeToEvents();
            InitPages();

            // Wire activity log
            LogList.ItemsSource = _vm.ActivityLog;

            // Bind charts to history collections
            ChartCpu.Data = _vm.CpuHistory;
            ChartCpuSys.Data = _vm.CpuHistory;
            ChartRam.Data = _vm.RamHistory;
            ChartRamSys.Data = _vm.RamHistory;
            ChartPing.Data = _vm.PingHistory;
            ChartPingNet.Data = _vm.PingHistory;

            // Bind process list and cache list
            GridProc.ItemsSource = _vm.Processes;
            CacheList.ItemsSource = _vm.Caches;
            ReportList.ItemsSource = _vm.Reports;

            // Bind DNS results
            DnsList.ItemsSource = _vm.DnsResults;

            // Subscribe to VM property changes for UI updates
            _vm.PropertyChanged += Vm_PropertyChanged;

            // Apply saved theme
            var settings = AppSettings.Load();
            var mode = settings.DarkTheme ? ThemeMode.Dark : ThemeMode.Light;
            ThemeManager.Apply(mode, Resources);
            UpdateThemeToggleLabel(mode);

            // Apply saved language
            var lang = LocalizationManager.Parse(settings.Language);
            _loc.Apply(lang, Resources);
            InitLanguageComboBox(lang);
            RefreshStaticStrings();

            // Start background services AFTER the window is fully rendered so the
            // UI is responsive immediately. Non-critical tasks (desktop shortcut)
            // are also deferred here.
            ContentRendered += OnContentRendered;
        }

        private void OnContentRendered(object? sender, EventArgs e)
        {
            ContentRendered -= OnContentRendered;

            // Create desktop + Start Menu shortcut on first run
            ShortcutHelper.CreateDesktopShortcutIfNeeded();
            ShortcutHelper.PinToStartMenu();

            // Start all background monitors and engines now that the window is visible
            _engine.Start();
        }

        // Named handlers for proper unsubscription
        private Action<SystemMetricsUpdatedEvent>? _sysHandler;
        private Action<NetworkMetricsUpdatedEvent>? _netHandler;
        private Action<GameDetectedEvent>? _gameDetHandler;
        private Action<GameEndedEvent>? _gameEndHandler;
        private Action<LagWarningEvent>? _lagHandler;
        private Action<LagWarningDismissEvent>? _lagDismissHandler;
        private Action<OptimizationAppliedEvent>? _optHandler;

        private void SubscribeToEvents()
        {
            _sysHandler = e => Dispatcher.BeginInvoke(() => UpdateSystemUI(e.Metrics));
            _netHandler = e => Dispatcher.BeginInvoke(() => UpdateNetworkUI(e.Metrics));
            _gameDetHandler = e => Dispatcher.BeginInvoke(() => UpdateGameUI(e.GameName, e.Platform, true));
            _gameEndHandler = e => Dispatcher.BeginInvoke(() => UpdateGameUI(_loc["NoGame"], "", false));
            _lagHandler = e => Dispatcher.BeginInvoke(() => ShowLagWarning(e.Detail));
            _lagDismissHandler = e => Dispatcher.BeginInvoke(() => DismissLagWarning());
            _optHandler = e => Dispatcher.BeginInvoke(() => TbStatus.Text = e.ActionName);

            EventBus.Instance.Subscribe(_sysHandler);
            EventBus.Instance.Subscribe(_netHandler);
            EventBus.Instance.Subscribe(_gameDetHandler);
            EventBus.Instance.Subscribe(_gameEndHandler);
            EventBus.Instance.Subscribe(_lagHandler);
            EventBus.Instance.Subscribe(_lagDismissHandler);
            EventBus.Instance.Subscribe(_optHandler);
        }

        private void UnsubscribeFromEvents()
        {
            if (_sysHandler != null) EventBus.Instance.Unsubscribe(_sysHandler);
            if (_netHandler != null) EventBus.Instance.Unsubscribe(_netHandler);
            if (_gameDetHandler != null) EventBus.Instance.Unsubscribe(_gameDetHandler);
            if (_gameEndHandler != null) EventBus.Instance.Unsubscribe(_gameEndHandler);
            if (_lagHandler != null) EventBus.Instance.Unsubscribe(_lagHandler);
            if (_lagDismissHandler != null) EventBus.Instance.Unsubscribe(_lagDismissHandler);
            if (_optHandler != null) EventBus.Instance.Unsubscribe(_optHandler);
        }

        private void InitPages()
        {
            _pages = new Dictionary<string, UIElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["Dashboard"] = PageDashboard,
                ["System"]    = PageSystem,
                ["Network"]   = PageNetwork,
                ["Gaming"]    = PageGaming,
                ["Processes"] = PageProcesses,
                ["Cache"]     = PageCache,
                ["Lag"]       = PageLag,
                ["Speed"]     = PageSpeed,
                ["Reports"]   = PageReports,
                ["Devices"]   = PageDevices,
                ["Settings"]  = PageSettings,
            };
        }

        // ── Navigation ────────────────────────────────────────────────────────
        private void NavBtn_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string tag)
                ShowPage(tag);
        }

        private void ShowPage(string tag)
        {
            _currentPage = tag;
            foreach (var kv in _pages)
                kv.Value.Visibility = kv.Key == tag
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            // Refresh page-specific data
            if (tag == "Processes") _vm.RefreshProcessesCommand.Execute(null);
            if (tag == "Cache")     _vm.ScanCachesCommand.Execute(null);
            if (tag == "Reports")   UpdateReportsPage();
            if (tag == "Settings")  { UpdatePinLabels(); UpdateThemeToggleLabel(ThemeManager.Current); }
        }

        // ── System metrics UI ────────────────────────────────────────────────
        private void UpdateSystemUI(SystemMetrics m)
        {
            // Dashboard gauges
            GaugeCpu.Value = m.CpuUsagePercent;
            GaugeCpu.ArcColor = GetBrushForPercent(m.CpuUsagePercent);
            TbCpuText.Text = $"{m.CpuUsagePercent:F1}%";

            GaugeRam.Value = m.RamUsagePercent;
            GaugeRam.ArcColor = GetBrushForPercent(m.RamUsagePercent);
            TbRamText.Text = $"{m.UsedRamGb:F1} / {m.TotalRamGb:F1} GB";

            GaugeDisk.Value = m.DiskActivityPercent;
            GaugeDisk.ArcColor = GetBrushForPercent(m.DiskActivityPercent);
            TbDiskText.Text = $"{m.DiskTotalMbPerSec:F1} MB/s";

            TbCpuName.Text = m.CpuName;
            TbCpuNameSys.Text = m.CpuName;

            // System page
            PbCpu.Value = m.CpuUsagePercent;
            TbCpuPct.Text = $"{m.CpuUsagePercent:F1}";
            TbCpuPct.Foreground = GetBrushForPercent(m.CpuUsagePercent);

            PbRam.Value = m.RamUsagePercent;
            TbRamPct.Text = $"{m.RamUsagePercent:F1}";
            TbRamPct.Foreground = GetBrushForPercent(m.RamUsagePercent);
            TbRamDetail.Text = $"{m.UsedRamGb:F1} {_loc["GBused"]} / {m.TotalRamGb:F1} {_loc["GBtotal"]}";

            TbDiskPct.Text = $"{m.DiskActivityPercent:F1}";
            TbDiskRead.Text = $"{m.DiskReadBytesPerSec / 1_048_576f:F1} MB/s";
            TbDiskWrite.Text = $"{m.DiskWriteBytesPerSec / 1_048_576f:F1} MB/s";
        }

        // ── Network metrics UI ───────────────────────────────────────────────
        private void UpdateNetworkUI(NetworkMetrics m)
        {
            var pingBrush = GetBrushForPing(m.PingMs);

            // Dashboard
            GaugePing.Value = Math.Min(m.PingMs, 200);
            GaugePing.ArcColor = pingBrush;
            TbNetText.Text = $"{m.Quality}";

            TbPing.Text = $"{m.PingMs:F0}";
            TbPing.Foreground = pingBrush;
            TbJitter.Text = $"{m.JitterMs:F1}";
            TbPacketLoss.Text = $"{m.PacketLossPercent:F1}";
            TbDownload.Text = $"{m.DownloadMbps:F1} {_loc["Mbps"]}";
            TbUpload.Text = $"{m.UploadMbps:F1} {_loc["Mbps"]}";

            // Network page
            TbPingNet.Text = $"{m.PingMs:F0}";
            TbPingNet.Foreground = pingBrush;
            TbJitterNet.Text = $"{m.JitterMs:F1}";
            TbPLNet.Text = $"{m.PacketLossPercent:F1}";
            TbDlNet.Text = $"{m.DownloadMbps:F1} {_loc["Mbps"]}";
            TbUlNet.Text = $"{m.UploadMbps:F1} {_loc["Mbps"]}";

            // Quality indicator
            TbQuality.Text = m.Quality.ToString();
            TbQuality.Foreground = m.Quality switch
            {
                NetworkQuality.Excellent => ThemeManager.GetBrush("Success"),
                NetworkQuality.Good => ThemeManager.GetBrush("Accent"),
                NetworkQuality.Fair => ThemeManager.GetBrush("Warning"),
                _ => ThemeManager.GetBrush("Danger")
            };
        }

        // ── Game UI ──────────────────────────────────────────────────────────
        private void UpdateGameUI(string gameName, string platform, bool active)
        {
            TbActiveGame.Text = gameName;
            TbPlatform.Text = platform;

            if (active)
            {
                TbGameStatus.Text = _loc["Running"];
                TbGameStatus.Foreground = ThemeManager.GetBrush("Success");
                GameStatusBg.Color = ThemeManager.GetColor("StatusGreenBg");
            }
            else
            {
                TbGameStatus.Text = _loc["NoGame"];
                TbGameStatus.Foreground = ThemeManager.GetBrush("TextSub");
                GameStatusBg.Color = ThemeManager.GetColor("BgCard");
            }
        }

        // ── Lag warning ──────────────────────────────────────────────────────
        private void ShowLagWarning(string detail)
        {
            TbPrediction.Text = detail;
            BannerLag.Visibility = Visibility.Visible;

            // Auto-dismiss after 30 seconds if conditions improve
            if (_lagBannerTimer == null)
            {
                _lagBannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                _lagBannerTimer.Tick += (s, e) =>
                {
                    _lagBannerTimer.Stop();
                    DismissLagWarning();
                };
            }
            _lagBannerTimer.Stop();
            _lagBannerTimer.Start();
        }

        private void DismissLagWarning()
        {
            _lagBannerTimer?.Stop();
            BannerLag.Visibility = Visibility.Collapsed;
        }

        // ── VM property changes ───────────────────────────────────────────────
        private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.GamingMode):
                    UpdateModeButtons();
                    break;
                case nameof(MainViewModel.StreamingMode):
                    UpdateModeButtons();
                    break;
                case nameof(MainViewModel.LastAnalysis):
                    UpdateLagAnalysisPage(_vm.LastAnalysis);
                    break;
                case nameof(MainViewModel.SpeedTestStatus):
                    TbSpeedStatus.Text = _vm.SpeedTestStatus;
                    break;
                case nameof(MainViewModel.SpeedTestProgress):
                    PbSpeed.Value = _vm.SpeedTestProgress;
                    break;
                case nameof(MainViewModel.SpeedTestDownload):
                    TbSpeedDl.Text = _vm.SpeedTestDownload;
                    break;
                case nameof(MainViewModel.SpeedTestUpload):
                    TbSpeedUl.Text = _vm.SpeedTestUpload;
                    break;
                case nameof(MainViewModel.SpeedTestPing):
                    TbSpeedPing.Text = _vm.SpeedTestPing;
                    break;
                case nameof(MainViewModel.SpeedTestRunning):
                    BtnRunSpeed.IsEnabled = !_vm.SpeedTestRunning;
                    BtnRunSpeed.Content = _vm.SpeedTestRunning ? $"▶ {_loc["Testing"]}" : $"▶ {_loc["RunSpeedTest"]}";
                    break;
                case nameof(MainViewModel.IsBusy):
                    BusyOverlay.Visibility = _vm.IsBusy ? Visibility.Visible : Visibility.Collapsed;
                    TbBusyText.Text = _vm.StatusText;
                    break;
                case nameof(MainViewModel.StatusText):
                    TbStatus.Text = _vm.StatusText;
                    if (_vm.IsBusy) TbBusyText.Text = _vm.StatusText;
                    break;
                case nameof(MainViewModel.LastResult):
                    if (_vm.LastResult.HasValue)
                        ShowToast(_vm.LastResult.Value, _vm.LastResultText);
                    break;
                case nameof(MainViewModel.AutoMode):
                    UpdateAutoModeButton();
                    break;
                case nameof(MainViewModel.NagleDisabled):
                    UpdateNagleButton();
                    break;
                case nameof(MainViewModel.WifiBandInfo):
                    TbWifiBand.Text = _vm.WifiBandInfo ?? "–";
                    break;
                case nameof(MainViewModel.DnsBenchmarkRunning):
                    BtnRunDnsBenchmark.IsEnabled = !_vm.DnsBenchmarkRunning;
                    BtnRunDnsBenchmark.Content = _vm.DnsBenchmarkRunning
                        ? $"⏳ {_loc["RunDnsBenchmark"]}"
                        : $"🔍 {_loc["RunDnsBenchmark"]}";
                    break;
            }
        }

        private void ShowToast(bool success, string message)
        {
            // Stop any existing timer
            _toastTimer?.Stop();

            // Set toast appearance
            ToastIcon.Text = success ? "✅" : "❌";
            ToastText.Text = message;
            ResultToast.Background = success
                ? new SolidColorBrush(Color.FromArgb(230, 16, 185, 129))   // green
                : new SolidColorBrush(Color.FromArgb(230, 239, 68, 68));   // red

            // Reset transform and show
            ToastTranslate.Y = -50;
            ResultToast.Opacity = 0;
            ResultToast.Visibility = Visibility.Visible;

            // Slide-in + fade-in animation
            var slideIn = new DoubleAnimation(-50, 0, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));

            ToastTranslate.BeginAnimation(TranslateTransform.YProperty, slideIn);
            ResultToast.BeginAnimation(OpacityProperty, fadeIn);

            // Auto-hide after 3 seconds
            if (_toastTimer == null)
            {
                _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _toastTimer.Tick += (s, e) =>
                {
                    _toastTimer!.Stop();
                    HideToast();
                };
            }
            _toastTimer.Start();
        }

        private void HideToast()
        {
            var slideOut = new DoubleAnimation(0, -50, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
            fadeOut.Completed += (s, e) =>
            {
                ResultToast.Visibility = Visibility.Collapsed;
                // Reset LastResult so next identical result still triggers
                _vm.LastResult = null;
            };

            ToastTranslate.BeginAnimation(TranslateTransform.YProperty, slideOut);
            ResultToast.BeginAnimation(OpacityProperty, fadeOut);
        }

        private void UpdateModeButtons()
        {
            // Dashboard buttons
            BtnGaming.Content = $"🎮 {(_vm.GamingMode ? _loc["GamingModeON"] : _loc["GamingModeOFF"])}";
            BtnGaming.Background = _vm.GamingMode
                ? ThemeManager.GetBrush("StatusGreenBg")
                : ThemeManager.GetBrush("SecBg");

            BtnStreaming.Content = $"▶ {(_vm.StreamingMode ? _loc["StreamingON"] : _loc["StreamingModeOFF"])}";
            BtnStreaming.Background = _vm.StreamingMode
                ? ThemeManager.GetBrush("StatusBlueBg")
                : ThemeManager.GetBrush("SecBg");

            // Gaming page buttons
            BtnGamingFull.Content = $"🎮 {(_vm.GamingMode ? _loc["DisableGamingMode"] : _loc["EnableGamingMode"])}";
            BtnStreamingFull.Content = $"▶ {(_vm.StreamingMode ? _loc["DisableStreamingMode"] : _loc["EnableStreamingMode"])}";
        }

        private void UpdateLagAnalysisPage(LagAnalysisResult? r)
        {
            if (r == null) return;

            TbLagSummary.Text = r.Summary;
            TbSeverity.Text = r.Severity.ToString();
            TbSeverity.Foreground = r.Severity switch
            {
                LagSeverity.None => ThemeManager.GetBrush("Success"),
                LagSeverity.Low => ThemeManager.GetBrush("Accent"),
                LagSeverity.Medium => ThemeManager.GetBrush("Warning"),
                LagSeverity.High => ThemeManager.GetBrush("Danger"),
                _ => ThemeManager.GetBrush("Danger")
            };

            // Snapshot metrics
            TbSnapCpu.Text = $"{r.CpuPercent:F1}";
            TbSnapRam.Text = $"{r.RamPercent:F1}";
            TbSnapDisk.Text = $"{r.DiskPercent:F1}";
            TbSnapPing.Text = $"{r.PingMs:F0}";
            TbSnapJitter.Text = $"{r.JitterMs:F1}";
            TbSnapPL.Text = $"{r.PacketLossPercent:F1}";

            // Causes
            CausesList.Items.Clear();
            foreach (var cause in r.Causes)
            {
                var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
                panel.Children.Add(new TextBlock
                {
                    Text = $"● {FormatCause(cause.Cause)} — {_loc["Confidence"]}: {cause.Confidence:F0}%",
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 13,
                    Foreground = ThemeManager.GetBrush("TextPrimary")
                });
                panel.Children.Add(new TextBlock
                {
                    Text = cause.Description,
                    FontSize = 12,
                    Foreground = ThemeManager.GetBrush("TextSub"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(12, 4, 0, 0)
                });
                CausesList.Items.Add(panel);
            }

            if (r.Causes.Count == 0)
            {
                CausesList.Items.Add(new TextBlock
                {
                    Text = _loc["NoIssuesDetected"],
                    Foreground = ThemeManager.GetBrush("Success"),
                    FontSize = 13
                });
            }

            // Recommendations
            RecList.ItemsSource = r.Recommendations;
        }

        private void UpdateReportsPage()
        {
            BdrNoReports.Visibility = _vm.Reports.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        // ── Theme toggle ─────────────────────────────────────────────────────
        private void BtnThemeToggle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var newMode = ThemeManager.Toggle(Resources);
            UpdateThemeToggleLabel(newMode);

            // Persist preference
            var settings = AppSettings.Load();
            settings.DarkTheme = newMode == ThemeMode.Dark;
            settings.Save();
        }

        private void UpdateThemeToggleLabel(ThemeMode mode)
        {
            var text = mode == ThemeMode.Dark
                ? "\u2600 " + _loc["LightMode"]
                : "\u263D " + _loc["DarkMode"];
            if (ThemeToggleText != null) ThemeToggleText.Text = text;
            if (TbSettingsTheme != null) TbSettingsTheme.Text = text;
        }

        // ── Pin to Taskbar (via Start Menu shortcut) ─────────────────────────
        private void BtnPinTaskbar_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ShortcutHelper.IsStartMenuPinned)
            {
                ShortcutHelper.UnpinFromStartMenu();
            }
            else
            {
                ShortcutHelper.PinToStartMenu();
                ShowToast(true, _loc["PinnedToTaskbar"]);
            }
            UpdatePinLabels();
        }

        // ── Run at Startup toggle ────────────────────────────────────────────
        private void BtnRunAtStartup_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ShortcutHelper.IsStartupEnabled)
            {
                ShortcutHelper.DisableStartup();
                ShowToast(true, _loc["RemovedFromStartup"]);
            }
            else
            {
                ShortcutHelper.EnableStartup();
                ShowToast(true, _loc["AddedToStartup"]);
            }
            UpdatePinLabels();
        }

        private void UpdatePinLabels()
        {
            if (TbPinTaskbar != null)
                TbPinTaskbar.Text = ShortcutHelper.IsStartMenuPinned
                    ? $"📌 {_loc["UnpinFromTaskbar"]}"
                    : $"📌 {_loc["PinToTaskbar"]}";

            if (TbRunAtStartup != null)
                TbRunAtStartup.Text = ShortcutHelper.IsStartupEnabled
                    ? $"✅ {_loc["RunAtStartup"]}"
                    : $"⬜ {_loc["RunAtStartup"]}";
        }

        // ── Localized static strings ─────────────────────────────────────────
        private void RefreshStaticStrings()
        {
            BtnOptimize.Content = $"⚡ {_loc["StartOptimization"]}";
            BtnLagAnalysisQuick.Content = $"🔍 {_loc["LagAnalysisBtn"]}";
            BtnOptimizeRam.Content = $"🔧 {_loc["OptimizeRAM"]}";
            BtnFlushDns.Content = $"🔄 {_loc["FlushDNS"]}";
            BtnTcpOpt.Content = _vm.TcpOptimized
                ? $"⚙ {_loc["RestoreTCP"]}"
                : $"⚙ {_loc["ApplyTCP"]}";
            BtnRefreshProc.Content = $"↻ {_loc["Refresh"]}";
            BtnScanCache.Content = $"↻ {_loc["Scan"]}";
            BtnRunLagFull.Content = $"🔍 {_loc["RunLagAnalysis"]}";
            BtnRunSpeed.Content = $"▶ {_loc["RunSpeedTest"]}";

            // Tooltips
            BtnOptimize.ToolTip = _loc["TipStartOpt"];
            BtnOptimizeRam.ToolTip = _loc["TipOptimizeRam"];
            BtnFlushDns.ToolTip = _loc["TipFlushDns"];
            BtnTcpOpt.ToolTip = _vm.TcpOptimized ? _loc["TipRestoreTcp"] : _loc["TipApplyTcp"];
            BtnGaming.ToolTip = _loc["TipGamingMode"];
            BtnStreaming.ToolTip = _loc["TipStreamingMode"];
            BtnGamingFull.ToolTip = _loc["TipGamingMode"];
            BtnStreamingFull.ToolTip = _loc["TipStreamingMode"];
            BtnLagAnalysisQuick.ToolTip = _loc["TipLagAnalysis"];
            BtnRunLagFull.ToolTip = _loc["TipLagAnalysis"];
            BtnScanCache.ToolTip = _loc["TipScanCache"];
            BtnRefreshProc.ToolTip = _loc["TipRefreshProc"];
            BtnRunSpeed.ToolTip = _loc["TipRunSpeed"];
            CmbLanguage.ToolTip = _loc["TipLanguage"];
            ThemeToggleText.ToolTip = _loc["TipThemeToggle"];

            // DataGrid status converter
            StatusConverter.TrueText = _loc["Throttled"];
            StatusConverter.FalseText = _loc["Normal"];

            // DataGrid column headers
            ColProcess.Header = _loc["Process"];
            ColDesc.Header = _loc["Description"];
            ColMemory.Header = _loc["MemoryMB"];
            ColPriority.Header = _loc["Priority"];
            ColStatus.Header = _loc["Status"];

            UpdateModeButtons();

            // New feature buttons
            UpdateAutoModeButton();
            UpdateNagleButton();
            BtnRunDnsBenchmark.Content = $"🔍 {_loc["RunDnsBenchmark"]}";
            BtnRestoreDns.Content = $"↩ {_loc["RestoreDns"]}";
            BtnDetectBand.Content = $"📡 {_loc["DetectBand"]}";
            BtnResetNetwork.Content = $"🔄 {_loc["NetworkReset"]}";
            BtnScanNetwork.Content = $"🔍 {_loc["ScanNetwork"]}";
            BtnScanNetwork.ToolTip = _loc["TipScanNetwork"];

            // Tooltips for new buttons
            BtnAutoMode.ToolTip = _loc["TipAutoMode"];
            BtnRunDnsBenchmark.ToolTip = _loc["TipDnsBenchmark"];
            BtnToggleNagle.ToolTip = _loc["TipNagle"];
            BtnResetNetwork.ToolTip = _loc["TipNetworkReset"];
            BtnRestoreDns.ToolTip = _loc["TipRestoreDns"];
            BtnDetectBand.ToolTip = _loc["TipDetectBand"];
            // Pin/Startup labels
            UpdatePinLabels();

            // Gauge labels (localized)
            GaugeCpu.Label = _loc["Usage"];
            GaugeRam.Label = _loc["Usage"];
            GaugeDisk.Label = _loc["Activity"];
            GaugePing.Label = _loc["Ping"];

            // Gaming enhancements status
            TbPowerPlanStatus.Text = _vm.GamingMode ? _loc["PowerPlanHigh"] : _loc["PowerPlanRestored"];
            TbVisualFxStatus.Text = _vm.GamingMode ? _loc["NagleDisabled"] : _loc["NagleEnabled"];

            // Game status (only update if no game is active)
            if (!_vm.GamingMode || string.IsNullOrEmpty(TbActiveGame.Text))
                TbGameStatus.Text = _loc["NoGame"];
        }

        // ── Language ──────────────────────────────────────────────────────────
        private bool _suppressLangChange;

        private void InitLanguageComboBox(AppLanguage selected)
        {
            _suppressLangChange = true;
            CmbLanguage.Items.Clear();
            CmbLanguageSettings?.Items.Clear();
            foreach (var lang in Enum.GetValues<AppLanguage>())
            {
                var flag = LocalizationManager.LanguageFlags[lang];
                var name = LocalizationManager.LanguageNames[lang];
                var item = new ComboBoxItem
                {
                    Content = $"{flag}  {name}",
                    Tag = lang,
                    Foreground = ThemeManager.GetBrush("TextPrimary"),
                    FontSize = 12
                };
                CmbLanguage.Items.Add(item);
                CmbLanguageSettings?.Items.Add(new ComboBoxItem
                {
                    Content = $"{flag}  {name}",
                    Tag = lang,
                    Foreground = ThemeManager.GetBrush("TextPrimary"),
                    FontSize = 12
                });
            }
            CmbLanguage.SelectedIndex = (int)selected;
            if (CmbLanguageSettings != null) CmbLanguageSettings.SelectedIndex = (int)selected;
            _suppressLangChange = false;
        }

        private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressLangChange) return;
            // Determine which combobox triggered
            var src = sender as System.Windows.Controls.ComboBox;
            if (src?.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not AppLanguage lang) return;

            _suppressLangChange = true;
            CmbLanguage.SelectedIndex = (int)lang;
            if (CmbLanguageSettings != null) CmbLanguageSettings.SelectedIndex = (int)lang;
            _suppressLangChange = false;

            _loc.Apply(lang, Resources);
            UpdateThemeToggleLabel(ThemeManager.Current);
            RefreshStaticStrings();
            _tray?.UpdateLanguage();

            var settings = AppSettings.Load();
            settings.Language = lang.ToString();
            settings.Save();
        }

        // ── Button handlers ───────────────────────────────────────────────────
        private void BtnOptimize_Click(object sender, RoutedEventArgs e)
            => _vm.RunFullOptimizationCommand.Execute(null);

        private void BtnOptimizeRam_Click(object sender, RoutedEventArgs e)
            => _vm.OptimizeRamCommand.Execute(null);

        private void BtnGaming_Click(object sender, RoutedEventArgs e)
            => _vm.ToggleGamingModeCommand.Execute(null);

        private void BtnStreaming_Click(object sender, RoutedEventArgs e)
            => _vm.ToggleStreamingModeCommand.Execute(null);

        private void BtnLagAnalysis_Click(object sender, RoutedEventArgs e)
        {
            _vm.RunLagAnalysisCommand.Execute(null);
            ShowPage("Lag");
            // Select Lag nav button
            foreach (var rb in FindVisualChildren<RadioButton>(this))
                if (rb.Tag?.ToString() == "Lag") { rb.IsChecked = true; break; }
        }

        private void BtnFlushDns_Click(object sender, RoutedEventArgs e)
            => _vm.FlushDnsCommand.Execute(null);

        private void BtnTcpOpt_Click(object sender, RoutedEventArgs e)
        {
            _vm.ToggleTcpOptimizeCommand.Execute(null);
            BtnTcpOpt.Content = _vm.TcpOptimized
                ? $"⚙ {_loc["RestoreTCP"]}"
                : $"⚙ {_loc["ApplyTCP"]}";
        }

        private void BtnScanCache_Click(object sender, RoutedEventArgs e)
            => _vm.ScanCachesCommand.Execute(null);

        private void BtnClearCache_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is BrowserType bt)
                _vm.ClearCacheCommand.Execute(bt);
        }

        private void BtnRefreshProc_Click(object sender, RoutedEventArgs e)
            => _vm.RefreshProcessesCommand.Execute(null);

        private void BtnRunSpeed_Click(object sender, RoutedEventArgs e)
            => _vm.RunSpeedTestCommand.Execute(null);

        // ── New feature handlers ────────────────────────────────────────────
        private void BtnAutoMode_Click(object sender, RoutedEventArgs e)
            => _vm.ToggleAutoModeCommand.Execute(null);


        private void BtnRunDnsBenchmark_Click(object sender, RoutedEventArgs e)
            => _vm.RunDnsBenchmarkCommand.Execute(null);

        private void BtnApplyDns_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is DnsBenchmarkResult dns)
                _vm.ApplyDnsCommand.Execute(dns);
        }

        private void BtnRestoreDns_Click(object sender, RoutedEventArgs e)
            => _vm.RestoreDnsCommand.Execute(null);

        private void BtnToggleNagle_Click(object sender, RoutedEventArgs e)
            => _vm.ToggleNagleCommand.Execute(null);

        private void BtnDetectBand_Click(object sender, RoutedEventArgs e)
            => _vm.DetectWifiBandCommand.Execute(null);

        private void BtnResetNetwork_Click(object sender, RoutedEventArgs e)
            => _vm.ResetNetworkStackCommand.Execute(null);

        private async void BtnScanNetwork_Click(object sender, RoutedEventArgs e)
        {
            if (_scanner.IsScanning) return;
            BtnScanNetwork.IsEnabled = false;
            BtnScanNetwork.Content = $"🔍 {_loc["Scanning"]}";
            TbDeviceCount.Text = "";

            try
            {
                var devices = await Task.Run(() => _scanner.ScanAsync());
                DeviceList.ItemsSource = devices;
                TbDeviceCount.Text = string.Format(_loc["DevicesFound"], devices.Count);
            }
            catch (Exception ex)
            {
                var msg = $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
                try { System.IO.File.WriteAllText(
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "netprove_crash.txt"), msg); }
                catch { }
                TbDeviceCount.Text = ex.Message;
            }
            finally
            {
                BtnScanNetwork.IsEnabled = true;
                BtnScanNetwork.Content = $"🔍 {_loc["ScanNetwork"]}";
            }
        }

        private void UpdateAutoModeButton()
        {
            BtnAutoMode.Content = _vm.AutoMode
                ? $"🤖 {_loc["AutoModeEnabled"]}"
                : $"🔧 {_loc["ManualMode"]}";
            BtnAutoMode.Background = _vm.AutoMode
                ? ThemeManager.GetBrush("StatusBlueBg")
                : ThemeManager.GetBrush("SecBg");
        }

        private void UpdateNagleButton()
        {
            BtnToggleNagle.Content = _vm.NagleDisabled
                ? $"✅ {_loc["NagleDisabled"]}"
                : $"⚙ {_loc["ToggleNagle"]}";
        }

        // ── Window lifecycle (tray + cleanup) ──────────────────────────────
        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            // Normal minimize stays in taskbar — no tray hide
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_tray != null && !_tray.IsExiting && AppSettings.Instance.MinimizeToTray)
            {
                e.Cancel = true;
                _tray.MinimizeToTray();
                return;
            }

            // Clean up resources
            UnsubscribeFromEvents();
            _engine.Dispose();
            _tray?.Dispose();
            base.OnClosing(e);
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static SolidColorBrush GetBrushForPercent(float v) =>
            v >= 90 ? ThemeManager.GetBrush("Danger") :
            v >= 70 ? ThemeManager.GetBrush("Warning") :
            ThemeManager.GetBrush("Accent");

        private static SolidColorBrush GetBrushForPing(double v) =>
            v >= 150 ? ThemeManager.GetBrush("Danger") :
            v >= 80  ? ThemeManager.GetBrush("Warning") :
            ThemeManager.GetBrush("Success");

        private string FormatCause(LagCause c) => c switch
        {
            LagCause.CpuBottleneck => _loc["CpuBottleneck"],
            LagCause.RamPressure => _loc["RamPressure"],
            LagCause.DiskIoBottleneck => _loc["DiskIoBottleneck"],
            LagCause.NetworkLatencySpike => _loc["NetworkLatencySpike"],
            LagCause.PacketLoss => _loc["PacketLossCause"],
            LagCause.UnstableConnection => _loc["UnstableConnection"],
            LagCause.BackgroundInterference => _loc["BackgroundInterference"],
            _ => c.ToString()
        };

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) yield return t;
                foreach (var c in FindVisualChildren<T>(child)) yield return c;
            }
        }
    }
}
