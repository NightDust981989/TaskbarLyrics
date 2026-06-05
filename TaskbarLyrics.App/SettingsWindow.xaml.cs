using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TaskbarLyrics.Core.Services;
using TaskbarLyrics.Core.Utilities;

namespace TaskbarLyrics.App;

public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    private const string DefaultFontFamily =
        "SF Pro Display, SF Pro Text, Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Microsoft YaHei";

    private readonly AppSettings _settings;
    private readonly ObservableCollection<RecognitionSourceItem> _recognitionOrderItems = new();
    private readonly System.Windows.Threading.DispatcherTimer _liveApplyTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(250)
    };

    private System.Windows.Point _recognitionDragStartPoint;
    private RecognitionSourceItem? _draggedRecognitionItem;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        _liveApplyTimer.Tick += LiveApplyTimer_Tick;
        SourceInitialized += SettingsWindow_SourceInitialized;
        Loaded += SettingsWindow_Loaded;
        Activated += SettingsWindow_Activated;
        StateChanged += SettingsWindow_StateChanged;

        PopulateFontFamilyOptions();
        LoadFromSettings();
        AttachLiveSettingsHandlers();
    }

    private void SettingsWindow_SourceInitialized(object? sender, EventArgs e)
    {
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this, Wpf.Ui.Controls.WindowBackdropType.Acrylic, true);
        ApplyWindowsBackdrop();
        ScheduleWindowsBackdropRefresh();
    }

    private void SettingsWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        ScheduleWindowsBackdropRefresh();
    }

    private void SettingsWindow_Activated(object? sender, EventArgs e)
    {
        ApplyWindowsBackdrop();
        ScheduleWindowsBackdropRefresh();
    }

    private void SettingsWindow_StateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeRestoreIcon();
    }

    private void CaptionDragArea_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximizeRestore();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UpdateMaximizeRestoreIcon()
    {
        MaximizeRestoreIcon.Text = WindowState == WindowState.Maximized
            ? "\uE923"
            : "\uE922";
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void LoadFromSettings()
    {
        LoadRecognitionOrder(_settings.SourceRecognitionOrder);

        QQMusicCheckBox.IsChecked = _settings.EnableQQMusic;
        NeteaseCheckBox.IsChecked = _settings.EnableNetease;
        KugouCheckBox.IsChecked = _settings.EnableKugou;
        SpotifyCheckBox.IsChecked = _settings.EnableSpotify;
        StartupCheckBox.IsChecked = _settings.ShowLyricsOnStartup;
        LyricTranslationCheckBox.IsChecked = _settings.ShowLyricTranslation;
        BackgroundCheckBox.IsChecked = _settings.ShowBackground;
        BorderCheckBox.IsChecked = _settings.ShowBorder;
        SmtcTimelineMonitorCheckBox.IsChecked = _settings.EnableSmtcTimelineMonitor;

        FontSizeTextBox.Text = _settings.FontSize.ToString(CultureInfo.InvariantCulture);
        FontFamilyComboBox.Text = ExtractPrimaryFontFamily(_settings.FontFamily);
        FontWeightComboBox.SelectedIndex = NormalizeFontWeight(_settings.FontWeight) switch
        {
            "Light" => 0,
            "Normal" => 1,
            "Medium" => 2,
            "SemiBold" => 3,
            "Bold" => 4,
            _ => 3
        };
        ForegroundColorTextBox.Text = _settings.ForegroundColor;
        BackgroundOpacityTextBox.Text = _settings.BackgroundOpacity.ToString(CultureInfo.InvariantCulture);
        WindowWidthTextBox.Text = _settings.WindowWidth.ToString(CultureInfo.InvariantCulture);
        XOffsetTextBox.Text = _settings.XOffset.ToString(CultureInfo.InvariantCulture);
        YOffsetTextBox.Text = _settings.YOffset.ToString(CultureInfo.InvariantCulture);

        AnchorComboBox.SelectedIndex = _settings.HorizontalAnchor switch
        {
            LyricsHorizontalAnchor.Left => 0,
            LyricsHorizontalAnchor.Center => 1,
            _ => 2
        };
    }

    private void AttachLiveSettingsHandlers()
    {
        AttachToggleHandler(QQMusicCheckBox);
        AttachToggleHandler(NeteaseCheckBox);
        AttachToggleHandler(KugouCheckBox);
        AttachToggleHandler(SpotifyCheckBox);
        AttachToggleHandler(StartupCheckBox);
        AttachToggleHandler(LyricTranslationCheckBox);
        AttachToggleHandler(BackgroundCheckBox);
        AttachToggleHandler(BorderCheckBox);
        AttachToggleHandler(SmtcTimelineMonitorCheckBox);

        FontSizeTextBox.TextChanged += SettingsControl_Changed;
        ForegroundColorTextBox.TextChanged += SettingsControl_Changed;
        BackgroundOpacityTextBox.TextChanged += SettingsControl_Changed;
        WindowWidthTextBox.TextChanged += SettingsControl_Changed;
        XOffsetTextBox.TextChanged += SettingsControl_Changed;
        YOffsetTextBox.TextChanged += SettingsControl_Changed;

        FontFamilyComboBox.SelectionChanged += SettingsControl_Changed;
        FontFamilyComboBox.LostKeyboardFocus += SettingsControl_Changed;
        FontWeightComboBox.SelectionChanged += SettingsControl_Changed;
        AnchorComboBox.SelectionChanged += SettingsControl_Changed;
    }

    private void AttachToggleHandler(System.Windows.Controls.CheckBox toggle)
    {
        toggle.Checked += SettingsControl_Changed;
        toggle.Unchecked += SettingsControl_Changed;
    }

    private void SettingsControl_Changed(object sender, EventArgs e)
    {
        ScheduleLiveApply();
    }

    private void ScheduleLiveApply()
    {
        _liveApplyTimer.Stop();
        _liveApplyTimer.Start();
    }

    private void LiveApplyTimer_Tick(object? sender, EventArgs e)
    {
        _liveApplyTimer.Stop();
        _ = ApplySettingsFromControls();
    }

    private bool ApplySettingsFromControls()
    {
        if (!TryReadDouble(FontSizeTextBox, 10, 40, out var fontSize) ||
            !TryReadDouble(BackgroundOpacityTextBox, 0, 1, out var backgroundOpacity) ||
            !TryReadDouble(WindowWidthTextBox, 320, 1400, out var windowWidth) ||
            !TryReadDouble(XOffsetTextBox, -2000, 2000, out var xOffset) ||
            !TryReadDouble(YOffsetTextBox, -2000, 2000, out var yOffset))
        {
            return false;
        }

        _settings.SourceRecognitionOrder = _recognitionOrderItems.Select(x => x.SourceKey).ToList();
        _settings.EnableQQMusic = QQMusicCheckBox.IsChecked == true;
        _settings.EnableNetease = NeteaseCheckBox.IsChecked == true;
        _settings.EnableKugou = KugouCheckBox.IsChecked == true;
        _settings.EnableSpotify = SpotifyCheckBox.IsChecked == true;
        _settings.ShowLyricsOnStartup = StartupCheckBox.IsChecked == true;
        _settings.ShowLyricTranslation = LyricTranslationCheckBox.IsChecked == true;
        _settings.ShowBackground = BackgroundCheckBox.IsChecked == true;
        _settings.ShowBorder = BorderCheckBox.IsChecked == true;
        _settings.EnableSmtcTimelineMonitor = SmtcTimelineMonitorCheckBox.IsChecked == true;
        _settings.FontSize = fontSize;
        _settings.FontFamily = string.IsNullOrWhiteSpace(FontFamilyComboBox.Text)
            ? DefaultFontFamily
            : FontFamilyComboBox.Text.Trim();
        _settings.FontWeight = FontWeightComboBox.SelectedIndex switch
        {
            0 => "Light",
            1 => "Normal",
            2 => "Medium",
            3 => "SemiBold",
            4 => "Bold",
            _ => "SemiBold"
        };
        _settings.ForegroundColor = string.IsNullOrWhiteSpace(ForegroundColorTextBox.Text)
            ? "#FFFFFFFF"
            : ForegroundColorTextBox.Text.Trim();
        _settings.BackgroundOpacity = backgroundOpacity;
        _settings.WindowWidth = windowWidth;
        _settings.XOffset = xOffset;
        _settings.YOffset = yOffset;
        _settings.HorizontalAnchor = AnchorComboBox.SelectedIndex switch
        {
            0 => LyricsHorizontalAnchor.Left,
            1 => LyricsHorizontalAnchor.Center,
            _ => LyricsHorizontalAnchor.Right
        };

        if (System.Windows.Application.Current is App app)
        {
            app.SaveSettings(_settings.Clone());
        }

        return true;
    }

    private void RecognitionNavButton_Click(object sender, RoutedEventArgs e)
    {
        SelectNavigationButton(RecognitionNavButton);
        ScrollToSection(RecognitionSection);
    }

    private void DisplayNavButton_Click(object sender, RoutedEventArgs e)
    {
        SelectNavigationButton(DisplayNavButton);
        ScrollToSection(DisplaySection);
    }

    private void FontNavButton_Click(object sender, RoutedEventArgs e)
    {
        SelectNavigationButton(FontNavButton);
        ScrollToSection(FontSection);
    }

    private void LayoutNavButton_Click(object sender, RoutedEventArgs e)
    {
        SelectNavigationButton(LayoutNavButton);
        ScrollToSection(LayoutSection);
    }

    private void DebugNavButton_Click(object sender, RoutedEventArgs e)
    {
        SelectNavigationButton(DebugNavButton);
        ScrollToSection(DebugSection);
    }

    private void SettingsContentScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.ExtentHeightChange == 0)
        {
            return;
        }

        UpdateSelectedNavigationFromScroll();
    }

    private void SelectNavigationButton(System.Windows.Controls.Button selectedButton)
    {
        RecognitionNavButton.Tag = ReferenceEquals(selectedButton, RecognitionNavButton) ? "Selected" : null;
        DisplayNavButton.Tag = ReferenceEquals(selectedButton, DisplayNavButton) ? "Selected" : null;
        FontNavButton.Tag = ReferenceEquals(selectedButton, FontNavButton) ? "Selected" : null;
        LayoutNavButton.Tag = ReferenceEquals(selectedButton, LayoutNavButton) ? "Selected" : null;
        DebugNavButton.Tag = ReferenceEquals(selectedButton, DebugNavButton) ? "Selected" : null;
    }

    private void ScrollToSection(FrameworkElement section)
    {
        if (!section.IsLoaded)
        {
            return;
        }

        var relativePosition = section.TransformToAncestor(SettingsContentScrollViewer)
            .Transform(new System.Windows.Point(0, 0));
        SettingsContentScrollViewer.ScrollToVerticalOffset(SettingsContentScrollViewer.VerticalOffset + relativePosition.Y - 16);
    }

    private void UpdateSelectedNavigationFromScroll()
    {
        var candidates = new[]
        {
            (Section: RecognitionSection, Button: RecognitionNavButton),
            (Section: DisplaySection, Button: DisplayNavButton),
            (Section: FontSection, Button: FontNavButton),
            (Section: LayoutSection, Button: LayoutNavButton),
            (Section: DebugSection, Button: DebugNavButton)
        };

        var selectedButton = candidates[0].Button;
        var bestDistance = double.MaxValue;

        foreach (var (section, button) in candidates)
        {
            if (!section.IsLoaded)
            {
                continue;
            }

            var y = section.TransformToAncestor(SettingsContentScrollViewer)
                .Transform(new System.Windows.Point(0, 0))
                .Y;
            var distance = Math.Abs(y - 24);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                selectedButton = button;
            }
        }

        SelectNavigationButton(selectedButton);
    }

    private void ClearLyricCacheButton_Click(object sender, RoutedEventArgs e)
    {
        LyricProviderBase.ClearCache();
        LrcLibLyricProvider.ClearCache();
        GenericSmtcLyricProvider.ClearCache();
        System.Windows.MessageBox.Show("歌词缓存已清除。", "TaskbarLyrics 设置", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void PopulateFontFamilyOptions()
    {
        FontFamilyComboBox.ItemsSource = Fonts.SystemFontFamilies
            .Select(x => x.Source)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void LoadRecognitionOrder(IReadOnlyList<string>? configuredOrder)
    {
        _recognitionOrderItems.Clear();
        foreach (var key in NormalizeRecognitionOrder(configuredOrder))
        {
            _recognitionOrderItems.Add(new RecognitionSourceItem(key, ToDisplayName(key)));
        }

        RecognitionOrderListBox.ItemsSource = _recognitionOrderItems;
    }

    private static List<string> NormalizeRecognitionOrder(IReadOnlyList<string>? configuredOrder)
    {
        var defaults = new[] { "QQMusic", "Netease", "Kugou", "Spotify" };
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (configuredOrder is not null)
        {
            foreach (var item in configuredOrder)
            {
                var normalized = NormalizeRecognitionSource(item);
                if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
                {
                    result.Add(normalized);
                }
            }
        }

        foreach (var item in defaults)
        {
            if (seen.Add(item))
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static string NormalizeRecognitionSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        var trimmed = source.Trim();
        if (trimmed.Contains("qqmusic", StringComparison.OrdinalIgnoreCase))
        {
            return "QQMusic";
        }

        if (trimmed.Contains("netease", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase))
        {
            return "Netease";
        }

        if (trimmed.Contains("kugou", StringComparison.OrdinalIgnoreCase))
        {
            return "Kugou";
        }

        if (trimmed.Contains("spotify", StringComparison.OrdinalIgnoreCase))
        {
            return "Spotify";
        }

        return string.Empty;
    }

    private static string ToDisplayName(string source)
    {
        return source switch
        {
            "QQMusic" => "QQ音乐",
            "Netease" => "网易云音乐",
            "Kugou" => "酷狗音乐",
            "Spotify" => "Spotify",
            _ => source
        };
    }

    private void RecognitionOrderListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _recognitionDragStartPoint = e.GetPosition(null);
        _draggedRecognitionItem = FindRecognitionItem(e.OriginalSource as DependencyObject);
    }

    private void RecognitionOrderListBox_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedRecognitionItem is null)
        {
            return;
        }

        var currentPosition = e.GetPosition(null);
        if (Math.Abs(currentPosition.X - _recognitionDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPosition.Y - _recognitionDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(RecognitionOrderListBox, _draggedRecognitionItem, System.Windows.DragDropEffects.Move);
    }

    private void RecognitionOrderListBox_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(RecognitionSourceItem))
            ? System.Windows.DragDropEffects.Move
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void RecognitionOrderListBox_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(RecognitionSourceItem)) ||
            e.Data.GetData(typeof(RecognitionSourceItem)) is not RecognitionSourceItem sourceItem)
        {
            return;
        }

        var targetItem = FindRecognitionItem(e.OriginalSource as DependencyObject);
        if (targetItem is null || ReferenceEquals(sourceItem, targetItem))
        {
            return;
        }

        var fromIndex = _recognitionOrderItems.IndexOf(sourceItem);
        var toIndex = _recognitionOrderItems.IndexOf(targetItem);
        if (fromIndex < 0 || toIndex < 0 || fromIndex == toIndex)
        {
            return;
        }

        _recognitionOrderItems.Move(fromIndex, toIndex);
        RecognitionOrderListBox.SelectedItem = sourceItem;
        ScheduleLiveApply();
    }

    private RecognitionSourceItem? FindRecognitionItem(DependencyObject? origin)
    {
        var current = origin;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: RecognitionSourceItem item })
            {
                return item;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static string NormalizeFontWeight(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Normal";
        }

        return value.Trim() switch
        {
            "Light" => "Light",
            "Normal" => "Normal",
            "Medium" => "Medium",
            "SemiBold" => "SemiBold",
            "Bold" => "Bold",
            _ => "Normal"
        };
    }

    private static string ExtractPrimaryFontFamily(string? fontFamily)
    {
        if (string.IsNullOrWhiteSpace(fontFamily))
        {
            return string.Empty;
        }

        var first = fontFamily.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(first) ? fontFamily.Trim() : first;
    }

    private static bool TryReadDouble(System.Windows.Controls.TextBox input, double min, double max, out double value)
    {
        value = 0;
        if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        value = Math.Clamp(parsed, min, max);
        return true;
    }

    private void ScheduleWindowsBackdropRefresh()
    {
        _ = Dispatcher.BeginInvoke(ApplyWindowsBackdrop, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void ApplyWindowsBackdrop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } compositionTarget })
        {
            compositionTarget.BackgroundColor = Colors.Transparent;
        }

        var darkMode = 1;
        _ = DwmSetWindowAttribute(hwnd, DwmWindowAttributeUseImmersiveDarkMode, ref darkMode, Marshal.SizeOf<int>());

        var cornerPreference = DwmWindowCornerPreferenceRound;
        _ = DwmSetWindowAttribute(hwnd, DwmWindowAttributeWindowCornerPreference, ref cornerPreference, Marshal.SizeOf<int>());

        Wpf.Ui.Controls.WindowBackdrop.RemoveBackground(this);
        var backdropApplied = Wpf.Ui.Controls.WindowBackdrop.ApplyBackdrop(this, Wpf.Ui.Controls.WindowBackdropType.Acrylic);
        Log.Warn($"SettingsWindow backdrop: applied={backdropApplied}, hwnd=0x{hwnd.ToInt64():X}");

        if (!backdropApplied)
        {
            ApplyAcrylicBlur(hwnd);
        }
    }

    private static void ApplyAcrylicBlur(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            return;
        }

        var accentPolicy = new AccentPolicy
        {
            AccentState = AccentState.EnableAcrylicBlurBehind,
            AccentFlags = 0,
            GradientColor = unchecked((int)0xCC0B1220)
        };

        var accentPolicySize = Marshal.SizeOf<AccentPolicy>();
        var accentPolicyPointer = Marshal.AllocHGlobal(accentPolicySize);
        try
        {
            Marshal.StructureToPtr(accentPolicy, accentPolicyPointer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.AccentPolicy,
                SizeOfData = accentPolicySize,
                Data = accentPolicyPointer
            };

            _ = SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(accentPolicyPointer);
        }
    }

    private const int DwmWindowAttributeUseImmersiveDarkMode = 20;
    private const int DwmWindowAttributeWindowCornerPreference = 33;
    private const int DwmWindowCornerPreferenceRound = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", PreserveSig = true)]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private enum WindowCompositionAttribute
    {
        AccentPolicy = 19
    }

    private enum AccentState
    {
        Disabled = 0,
        EnableGradient = 1,
        EnableTransparentGradient = 2,
        EnableBlurBehind = 3,
        EnableAcrylicBlurBehind = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    private sealed record RecognitionSourceItem(string SourceKey, string DisplayName);
}

