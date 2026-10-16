using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace LingoFlow;

public partial class OverlayWindow : Window
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private string target = "";
    private int[] characters = [];
    private readonly LinkedList<(Subtitle Subtitle, AppSettings Settings)> displayQueue = new();
    private readonly Dictionary<long, LinkedListNode<(Subtitle Subtitle, AppSettings Settings)>> queued = [];
    private readonly Stopwatch displayClock = new();
    private double animationSeconds, holdSeconds;
    private bool displaying;
    private long currentSequence = -1;
    private bool currentPending;
    private double finalAt;
    private readonly List<Subtitle> recent = [];
    private Subtitle? displayed;
    private AppSettings appearance = new();
    public event Action? BoundsChanged;
    public event Action? StartRequested;
    public event Action? PauseRequested;
    public event Action? StopRequested;
    public void SetTransportState(bool active, bool paused, bool busy, bool stopping)
    {
        OverlayStartButton.IsEnabled = !busy && (!active || paused) && !stopping;
        OverlayStartButton.Content = paused && active ? "▶ 继续" : "▶ 开始";
        OverlayPauseButton.IsEnabled = active && !paused && !busy && !stopping;
        OverlayStopButton.IsEnabled = active;
        OverlayStopButton.Content = stopping ? "■ 立即结束" : "■ 停止";
    }
    private void Start_Click(object sender, RoutedEventArgs e) { e.Handled = true; StartRequested?.Invoke(); }
    private void Pause_Click(object sender, RoutedEventArgs e) { e.Handled = true; PauseRequested?.Invoke(); }
    private void Stop_Click(object sender, RoutedEventArgs e) { e.Handled = true; StopRequested?.Invoke(); }
    internal IReadOnlyList<Subtitle> RecentRows => recent;
    internal event Action<long>? PresentationStarted;
    private bool locked;
    private string? hotkeyText;
    public bool Locked => locked;
    public OverlayWindow(AppSettings settings)
    {
        InitializeComponent();
        Width = Math.Clamp(settings.OverlayWidth, MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
        Height = Math.Clamp(settings.OverlayHeight, MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
        var work = SystemParameters.WorkArea;
        Left = double.IsFinite(settings.OverlayLeft) ? settings.OverlayLeft : work.Left + (work.Width - Width) / 2;
        Top = double.IsFinite(settings.OverlayTop) ? settings.OverlayTop : work.Bottom - Height - 55;
        var virtualArea = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!virtualArea.IntersectsWith(new Rect(Left, Top, Width, Height))) { Left = work.Left + 20; Top = work.Bottom - Height - 30; }
        SourceInitialized += (_, _) => SetLocked(false);
        timer.Tick += (_, _) =>
        {
            int position = animationSeconds <= 0 ? characters.Length : Math.Min(characters.Length, (int)(characters.Length * displayClock.Elapsed.TotalSeconds / animationSeconds));
            TranslatedText.Text = position == characters.Length ? target : target[..characters[position]];
            if (currentPending) CaptionScroll.ScrollToEnd();
            double hold = displayQueue.Count > 0 ? Math.Min(holdSeconds, .45) : holdSeconds;
            if (!currentPending && displayClock.Elapsed.TotalSeconds >= Math.Max(animationSeconds, finalAt) + hold)
            {
                TranslatedText.Text = target;
                if (displayQueue.First is { } node)
                {
                    var next = node.Value; displayQueue.RemoveFirst(); queued.Remove(next.Subtitle.Sequence);
                    BeginPresentation(next.Subtitle, next.Settings, false);
                }
                else { displaying = false; timer.Stop(); }
            }
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) ClearPendingDisplay(true); };
        Apply(settings);
    }
    public void Apply(AppSettings settings)
    {
        appearance = settings.Copy();
        if (settings.OverlayLines > 1) { displayQueue.Clear(); queued.Clear(); }
        TranslatedText.FontSize = settings.SubtitleSize;
        OriginalText.Visibility = settings.ShowOriginal ? Visibility.Visible : Visibility.Collapsed;
        var background = LingoFlow.Services.Theme.Color(settings.Colors.SubtitleBackground);
        Surface.Background = new SolidColorBrush(Color.FromArgb((byte)(settings.SubtitleOpacity * 255), background.R, background.G, background.B));
        OriginalText.Foreground = LingoFlow.Services.Theme.Brush(settings.Colors.Original);
        TranslatedText.Foreground = LingoFlow.Services.Theme.Brush(settings.Colors.Translation);
        OverlayLabel.Foreground = TranslatedText.Foreground; LockHint.Foreground = TranslatedText.Foreground;
        foreach (System.Windows.Controls.Button button in Controls.Children) button.Foreground = TranslatedText.Foreground;
        foreach (System.Windows.Controls.Button button in TransportControls.Children) button.Foreground = TranslatedText.Foreground;
        var accent = LingoFlow.Services.Theme.Color(settings.Colors.Accent);
        Surface.BorderBrush = new SolidColorBrush(Color.FromArgb(110, accent.R, accent.G, accent.B));
        RenderRecent();
    }
    private void RenderRecent()
    {
        while (recent.Count > Math.Clamp(appearance.OverlayLines, 1, 6) - 1) recent.RemoveAt(0);
        RecentCaptions.Children.Clear();
        foreach (var row in recent)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            if (appearance.ShowOriginal) panel.Children.Add(new TextBlock { Text = row.Original, Foreground = LingoFlow.Services.Theme.Brush(appearance.Colors.Original), FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 5) });
            panel.Children.Add(new TextBlock { Text = row.Translation, Foreground = LingoFlow.Services.Theme.Brush(appearance.Colors.Translation), FontSize = Math.Max(18, appearance.SubtitleSize * .82), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
            RecentCaptions.Children.Add(new Border { Child = panel, Opacity = .86 });
        }
    }
    public void Present(Subtitle subtitle, AppSettings settings, bool preview = false)
    {
        if (preview) ClearPendingDisplay(true);
        if (!preview && settings.OverlayLines > 1 && subtitle.Sequence > 0 && subtitle.Sequence < currentSequence)
        {
            int index = recent.FindIndex(row => row.Sequence == subtitle.Sequence);
            if (index >= 0) { recent[index] = subtitle; appearance = settings.Copy(); RenderRecent(); }
            return;
        }
        if (!preview && subtitle.Sequence > 0 && subtitle.Sequence == currentSequence)
        {
            displayed = subtitle;
            Apply(settings); OriginalText.Text = subtitle.Original;
            target = subtitle.Translation; characters = StringInfo.ParseCombiningCharacters(target);
            if (currentPending && !subtitle.IsPending) finalAt = displayClock.Elapsed.TotalSeconds;
            currentPending = subtitle.IsPending;
            OverlayLabel.Text = $"译流  /  {Languages.Name(settings.TargetLanguage)}  ·  {(currentPending ? "正在补全" : DateTime.Now.ToString("HH:mm"))}";
            if (!displaying || animationSeconds == 0) TranslatedText.Text = target;
            return;
        }
        if (!preview && subtitle.Sequence > 0 && queued.TryGetValue(subtitle.Sequence, out var existing))
        { existing.Value = (subtitle, settings.Copy()); return; }
        if (!preview && IsVisible && displaying && settings.OverlayLines <= 1)
        {
            var node = displayQueue.AddLast((subtitle, settings.Copy()));
            if (subtitle.Sequence > 0) queued[subtitle.Sequence] = node;
            return;
        }
        BeginPresentation(subtitle, settings, preview);
        if (!IsVisible) CompletePreviewAnimation();
    }
    private void BeginPresentation(Subtitle subtitle, AppSettings settings, bool preview)
    {
        if (!preview && displayed != null && displayed.Sequence != subtitle.Sequence) recent.Add(displayed);
        displayed = subtitle;
        Apply(settings);
        OverlayLabel.Text = preview ? "译流  /  样式预览 · 示例内容" : $"译流  /  {Languages.Name(settings.TargetLanguage)}  ·  {(subtitle.IsPending ? "正在补全" : DateTime.Now.ToString("HH:mm"))}";
        OriginalText.Text = subtitle.Original;
        target = subtitle.Translation; characters = StringInfo.ParseCombiningCharacters(target);
        currentSequence = subtitle.Sequence; currentPending = subtitle.IsPending; finalAt = 0;
        // Incoming tokens already provide a typewriter effect; do not wait for
        // completion and then replay the same sentence in a second animation.
        animationSeconds = settings.Typewriter && !settings.StreamTranslation && !currentPending && characters.Length > 0 ? .65 : 0;
        holdSeconds = Math.Clamp(characters.Length * .045, .9, 3);
        TranslatedText.Text = animationSeconds > 0 ? "" : target;
        displaying = true; displayClock.Restart(); timer.Start();
        if (settings.OverlayLines > 1) CaptionScroll.ScrollToEnd(); else CaptionScroll.ScrollToTop();
        PresentationStarted?.Invoke(subtitle.Sequence);
        Surface.BeginAnimation(OpacityProperty, new DoubleAnimation(.55, 1, TimeSpan.FromMilliseconds(220)));
    }
    public void CapturePosition(AppSettings settings)
    { settings.OverlayLeft = Left; settings.OverlayTop = Top; settings.OverlayWidth = Width; settings.OverlayHeight = Height; }
    public void ToggleLock() => SetLocked(!locked);
    public void ClearPendingDisplay(bool clearHistory = false) { displayQueue.Clear(); queued.Clear(); if (clearHistory) recent.Clear(); displayed = null; RenderRecent(); currentSequence = -1; currentPending = false; displaying = false; timer.Stop(); TranslatedText.Text = target; }
    public void CompletePreviewAnimation() => ClearPendingDisplay();
    public void SetLocked(bool value)
    {
        locked = value;
        var handle = new WindowInteropHelper(this).Handle; if (handle == IntPtr.Zero) return;
        long style = GetWindowLongPtr(handle, -20).ToInt64();
        style |= 0x80 | 0x08000000; // TOOLWINDOW + NOACTIVATE
        style = value ? style | 0x20 : style & ~0x20;
        SetWindowLongPtr(handle, -20, new IntPtr(style));
        Controls.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        TransportControls.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        ResizeHandles.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        ResizeMode = value ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        RefreshLockHint();
    }
    public void UpdateHotkeyHint(string? text) { hotkeyText = text; RefreshLockHint(); }
    private void RefreshLockHint()
    {
        LockHint.Text = locked
            ? hotkeyText == null ? "已穿透 · 右键托盘菜单解锁" : $"已穿透 · {hotkeyText} 解锁"
            : hotkeyText == null ? "拖动移动 · 边框 / 右下角缩放" : $"边框 / 右下角缩放 · {hotkeyText} 切换穿透";
    }
    internal void ResizeCaption(string direction, double dx, double dy)
    {
        if (locked) return;
        double oldWidth = Width, oldHeight = Height;
        if (direction.Contains('E') || direction.Contains('W'))
        {
            Width = Math.Clamp(oldWidth + (direction.Contains('W') ? -dx : dx), MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
            if (direction.Contains('W')) Left += oldWidth - Width;
        }
        if (direction.Contains('N') || direction.Contains('S'))
        {
            Height = Math.Clamp(oldHeight + (direction.Contains('N') ? -dy : dy), MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
            if (direction.Contains('N')) Top += oldHeight - Height;
        }
    }
    private void Resize_Delta(object sender, DragDeltaEventArgs e)
    { ResizeCaption((string)((Thumb)sender).Tag, e.HorizontalChange, e.VerticalChange); e.Handled = true; }
    private void Resize_Completed(object sender, DragCompletedEventArgs e) { if (!locked) BoundsChanged?.Invoke(); }
    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (locked || e.ChangedButton != MouseButton.Left) return;
        for (DependencyObject? at = e.OriginalSource as DependencyObject; at != null && at != Surface; at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
            if (at is Button or Thumb or ScrollBar) return;
        try { DragMove(); BoundsChanged?.Invoke(); } catch (InvalidOperationException) { }
    }
    private void Lock_Click(object sender, RoutedEventArgs e) { e.Handled = true; SetLocked(true); }
    private void Hide_Click(object sender, RoutedEventArgs e) { e.Handled = true; Hide(); }
    protected override void OnClosed(EventArgs e) { timer.Stop(); base.OnClosed(e); }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
