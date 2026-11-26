using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Crest.Island;

namespace Crest.Settings;

/// <summary>
/// Every Crest setting in four sections. Each change goes to the store at once, so the island updates live;
/// changes made elsewhere (the tray menu) show here too.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsStore _store;
    private bool _loading;
    private bool _updating;

    internal SettingsWindow(SettingsStore store)
    {
        InitializeComponent();
        _store = store;
        FillDisplays(store.Current.DisplayDeviceName);
        Load(store.Current);
        store.Changed += OnStoreChanged;
        Closed += (_, _) => store.Changed -= OnStoreChanged;
        Closing += (_, _) => Apply(); // a colour typed but not yet committed is kept when the window closes
    }

    private void OnStoreChanged(AppSettings settings)
    {
        if (!_updating) Load(settings);
    }

    // ----- settings to controls -----

    private void Load(AppSettings s)
    {
        _loading = true;
        Select(ThemeBox, s.Theme.ToString());
        // A colour box the user is typing in keeps its text; it commits on Enter, on leaving it, or on close.
        if (!BackgroundBox.IsKeyboardFocusWithin) BackgroundBox.Text = s.BackgroundColor;
        if (!TextColourBox.IsKeyboardFocusWithin) TextColourBox.Text = s.TextColor;
        TransparencySlider.Value = s.Transparency;
        Select(AccentBox, s.AccentMode.ToString());
        if (!AccentColourBox.IsKeyboardFocusWithin) AccentColourBox.Text = s.AccentColor;
        Select(ShapeBox, s.Shape.ToString());
        RoundnessSlider.Value = s.CornerRoundness;
        ScaleSlider.Value = s.Scale;
        AlbumArtCheck.IsChecked = s.ShowAlbumArt;
        EqualizerCheck.IsChecked = s.ShowEqualizer;

        Select(ExpandOnBox, s.ExpandOn.ToString());
        HoverDelaySlider.Value = s.HoverDelayMs;
        CollapseDelaySlider.Value = s.CollapseDelayMs;
        AutoTuckCheck.IsChecked = s.AutoTuckWhilePlaying;
        Select(NothingPlayingBox, s.WhenNothingPlaying.ToString());
        Select(WhileTimerRunsBox, s.WhileTimerRuns.ToString());
        PeekCheck.IsChecked = s.PeekOnTrackChange;
        PeekSlider.Value = s.PeekSeconds;
        Select(FullScreenBox, s.OverFullScreen.ToString());

        if (!Select(DisplayBox, s.DisplayDeviceName))
        {
            FillDisplays(s.DisplayDeviceName);
            Select(DisplayBox, s.DisplayDeviceName);
        }
        PositionSlider.Value = s.HorizontalPosition;
        TopOffsetSlider.Value = s.TopOffset;

        StartupCheck.IsChecked = s.StartWithWindows;
        VisibleCheck.IsChecked = s.IslandVisible;
        Select(AnimationsBox, s.Animations.ToString());
        _loading = false;
        ShowDetails(s);
    }

    /// <summary>Value labels, colour swatches, and the rows that only apply to some choices.</summary>
    private void ShowDetails(AppSettings s)
    {
        TransparencyValue.Text = s.Transparency == 0 ? "Solid" : $"{s.Transparency}%";
        RoundnessValue.Text = $"{s.CornerRoundness}%";
        ScaleValue.Text = $"{s.Scale}%";
        HoverDelayValue.Text = $"{s.HoverDelayMs} ms";
        CollapseDelayValue.Text = $"{s.CollapseDelayMs} ms";
        PeekValue.Text = $"{s.PeekSeconds} s";
        PositionValue.Text = s.HorizontalPosition == 50 ? "Centre"
            : s.HorizontalPosition < 50 ? $"{50 - s.HorizontalPosition}% left" : $"{s.HorizontalPosition - 50}% right";
        TopOffsetValue.Text = s.TopOffset == 0 ? "None" : $"{s.TopOffset} px";

        CustomColours.Visibility = s.Theme == ThemeName.Custom ? Visibility.Visible : Visibility.Collapsed;
        FixedAccentRow.Visibility = s.AccentMode == AccentMode.Fixed ? Visibility.Visible : Visibility.Collapsed;
        PeekSlider.IsEnabled = s.PeekOnTrackChange;
        BackgroundSwatch.Background = Swatch(s.BackgroundColor);
        TextSwatch.Background = Swatch(s.TextColor);
        AccentSwatch.Background = Swatch(s.AccentColor);
    }

    private void FillDisplays(string? saved)
    {
        DisplayBox.Items.Clear();
        DisplayBox.Items.Add(new ComboBoxItem { Content = "Main display (follows Windows)", Tag = null });
        var displays = IslandPositioner.GetDisplays();
        for (int i = 0; i < displays.Count; i++)
        {
            var d = displays[i];
            string main = d.IsPrimary ? ", main" : "";
            DisplayBox.Items.Add(new ComboBoxItem { Content = $"Display {i + 1}: {d.Bounds.Width} × {d.Bounds.Height}{main}", Tag = d.DeviceName });
        }
        if (!string.IsNullOrEmpty(saved) && !displays.Any(d => string.Equals(d.DeviceName, saved, StringComparison.OrdinalIgnoreCase)))
            DisplayBox.Items.Add(new ComboBoxItem { Content = "Saved display (not connected, using main)", Tag = saved });
    }

    // ----- controls to settings -----

    private void OnChanged(object sender, RoutedEventArgs e) => Apply();

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Apply();

    private void OnColourKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true; // Enter commits the colour; it must not press the Done button
        if (sender is TextBox box) box.Text = AppSettings.NormalizeHex(box.Text) ?? box.Text;
        Apply();
        if (sender is TextBox committed) committed.SelectAll();
    }

    private void OnColourCommitted(object sender, RoutedEventArgs e)
    {
        Apply();
        Load(_store.Current); // show the stored colour (normalised, or the previous one after a typo)
    }

    private void Apply()
    {
        if (_loading || !IsInitialized) return;
        _updating = true;
        _store.Update(Collect);
        _updating = false;
        Load(_store.Current); // shows clamped values and puts back an invalid colour
    }

    private AppSettings Collect(AppSettings s) => s with
    {
        Theme = Choice(ThemeBox, s.Theme),
        BackgroundColor = AppSettings.NormalizeHex(BackgroundBox.Text) ?? s.BackgroundColor, // a typo keeps the old colour
        TextColor = AppSettings.NormalizeHex(TextColourBox.Text) ?? s.TextColor,
        Transparency = (int)TransparencySlider.Value,
        AccentMode = Choice(AccentBox, s.AccentMode),
        AccentColor = AppSettings.NormalizeHex(AccentColourBox.Text) ?? s.AccentColor,
        Shape = Choice(ShapeBox, s.Shape),
        CornerRoundness = (int)RoundnessSlider.Value,
        Scale = (int)ScaleSlider.Value,
        ShowAlbumArt = AlbumArtCheck.IsChecked == true,
        ShowEqualizer = EqualizerCheck.IsChecked == true,
        ExpandOn = Choice(ExpandOnBox, s.ExpandOn),
        HoverDelayMs = (int)HoverDelaySlider.Value,
        CollapseDelayMs = (int)CollapseDelaySlider.Value,
        AutoTuckWhilePlaying = AutoTuckCheck.IsChecked == true,
        WhenNothingPlaying = Choice(NothingPlayingBox, s.WhenNothingPlaying),
        WhileTimerRuns = Choice(WhileTimerRunsBox, s.WhileTimerRuns),
        PeekOnTrackChange = PeekCheck.IsChecked == true,
        PeekSeconds = (int)PeekSlider.Value,
        OverFullScreen = Choice(FullScreenBox, s.OverFullScreen),
        DisplayDeviceName = (DisplayBox.SelectedItem as ComboBoxItem)?.Tag as string,
        HorizontalPosition = (int)PositionSlider.Value,
        TopOffset = (int)TopOffsetSlider.Value,
        StartWithWindows = StartupCheck.IsChecked == true,
        IslandVisible = VisibleCheck.IsChecked == true,
        Animations = Choice(AnimationsBox, s.Animations),
    };

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _updating = true;
        _store.Update(_ => AppSettings.Defaults);
        _updating = false;
        Load(_store.Current);
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();

    // ----- helpers -----

    private static bool Select(ComboBox box, string? tag)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return true;
            }
        }
        return false;
    }

    private static T Choice<T>(ComboBox box, T fallback) where T : struct, Enum =>
        (box.SelectedItem as ComboBoxItem)?.Tag is string tag && Enum.TryParse<T>(tag, out var value) ? value : fallback;

    private static Brush Swatch(string hex)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return Brushes.Transparent;
        }
    }
}
