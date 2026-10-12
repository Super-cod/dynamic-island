using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WindowsDynamicBar.Core.Buddy;
using WindowsDynamicBar.Core.Settings;

namespace WindowsDynamicBar.UI.Buddy;

public sealed class BuddyCardControl : Grid
{
    private readonly Image _bigSpriteImage;
    private readonly TextBlock _nameText;
    private readonly TextBlock _personalityBadge;
    private readonly Button _closeButton;
    private readonly StackPanel _metersPanel;
    private readonly WrapPanel _outfitPanel;
    private readonly TextBlock _statsText;
    private readonly StackPanel _shopPanel;
    private readonly Button _shopToggleBtn;
    private bool _shopVisible;

    /// <summary>The default (Aero2) Button template paints its own light-blue hover/pressed chrome over
    /// any Background, which shows as a bright box on the dark island: card buttons use this flat one.</summary>
    private static readonly ControlTemplate FlatButton = CreateFlatButtonTemplate();

    private static ControlTemplate CreateFlatButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.8));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.6));
        template.Triggers.Add(pressed);
        template.Seal();
        return template;
    }

    public event Action? CloseRequested;
    public event Action<BuddyOutfit>? OutfitSelected;
    public event Action<BuddyPersonality>? PersonalityChanged;
    public event Action<string>? SpeakRequested;
    public event Action<CosmeticPackId>? DemoPackToggled;

    public BuddyCardControl()
    {
        Background = Brushes.Transparent;
        Margin = new Thickness(20, 10, 22, 10);

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); // gap
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Left: 96x96 sprite on faint yellow radial glow
        var bigContainer = new Border
        {
            Width = 96,
            Height = 96,
            CornerRadius = new CornerRadius(20),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var radialGlow = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.7,
            RadiusY = 0.7
        };
        radialGlow.GradientStops.Add(new GradientStop(Color.FromArgb(0x38, 0xFF, 0xCF, 0x4D), 0.0));
        radialGlow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xCF, 0x4D), 1.0));
        bigContainer.Background = radialGlow;

        _bigSpriteImage = new Image
        {
            Width = 96,
            Height = 96,
            Stretch = Stretch.Fill
        };
        RenderOptions.SetBitmapScalingMode(_bigSpriteImage, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(_bigSpriteImage, EdgeMode.Aliased);
        bigContainer.Child = _bigSpriteImage;
        Children.Add(bigContainer);
        SetColumn(bigContainer, 0);

        // Right column: Content stack
        var contentStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center
        };
        Children.Add(contentStack);
        SetColumn(contentStack, 2);

        // Header: Name + Personality badge + Shop toggle + Close button
        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        _nameText = new TextBlock
        {
            Text = "Dot",
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(_nameText, Dock.Left);
        header.Children.Add(_nameText);

        _personalityBadge = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xCF, 0x4D)),
            Margin = new Thickness(8, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "Click to cycle personality (Playful, Chill, Sleepy, Chaotic, Focus Friend)"
        };
        _personalityBadge.MouseLeftButtonUp += (_, _) =>
        {
            CyclePersonality();
        };
        DockPanel.SetDock(_personalityBadge, Dock.Left);
        header.Children.Add(_personalityBadge);

        _closeButton = new Button
        {
            Content = "\u2715",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x96)),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 11,
            Padding = new Thickness(6, 2, 6, 2),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Template = FlatButton,
        };
        _closeButton.Click += (_, _) => CloseRequested?.Invoke();
        DockPanel.SetDock(_closeButton, Dock.Right);
        header.Children.Add(_closeButton);

        // Panels instantiated before buttons
        _metersPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 6)
        };

        _outfitPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 6)
        };

        _shopPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 0, 0, 6),
            Visibility = Visibility.Collapsed
        };

        _shopToggleBtn = new Button
        {
            Content = "\uD83D\uDECD Shop",
            Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xCE)),
            Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            FontSize = 10.5,
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Template = FlatButton,
        };
        _shopToggleBtn.Click += (_, _) =>
        {
            _shopVisible = !_shopVisible;
            if (_shopPanel is not null) _shopPanel.Visibility = _shopVisible ? Visibility.Visible : Visibility.Collapsed;
            if (_outfitPanel is not null) _outfitPanel.Visibility = _shopVisible ? Visibility.Collapsed : Visibility.Visible;
            _shopToggleBtn.Content = _shopVisible ? "\u2715 Outfits" : "\uD83D\uDECD Shop";
        };
        DockPanel.SetDock(_shopToggleBtn, Dock.Right);
        header.Children.Add(_shopToggleBtn);

        contentStack.Children.Add(header);
        contentStack.Children.Add(_metersPanel);
        contentStack.Children.Add(_outfitPanel);
        contentStack.Children.Add(_shopPanel);

        // Small monospace stats line: streak 5 days - pets 12 - focus sessions 3
        _statsText = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x96))
        };
        contentStack.Children.Add(_statsText);
    }

    private void CyclePersonality()
    {
        var s = App.Settings?.Current;
        if (s is null) return;
        var next = (BuddyPersonality)(((int)s.BuddyPersonality + 1) % 5);
        App.Settings?.Update(cfg => cfg.BuddyPersonality = next);
        PersonalityChanged?.Invoke(next);
        _personalityBadge.Text = $"· {next}";
    }

    public void UpdateBigSprite(ImageSource source)
    {
        _bigSpriteImage.Source = source;
    }

    public void Refresh(SettingsModel s, bool isOnline, bool isCharging, bool isLowBattery, bool isRamHigh, bool isMusicPlaying)
    {
        _nameText.Text = string.IsNullOrWhiteSpace(s.BuddyName) ? "Dot" : s.BuddyName;
        _personalityBadge.Text = $"· {s.BuddyPersonality}";

        // Refresh meters
        _metersPanel.Children.Clear();
        int hour = DateTime.Now.Hour;
        int moodVal = Math.Clamp(60 + (isOnline ? 10 : -25) + (isLowBattery ? -20 : 0) + (isRamHigh ? -15 : 0) + (isMusicPlaying ? 10 : 0) + Math.Min(20, s.BuddyPets), 0, 100);
        int energyVal = hour switch
        {
            < 5 => 25,
            < 9 => 80,
            < 14 => 85,
            < 18 => 65,
            < 22 => 50,
            _ => 35
        };
        int powerVal = isLowBattery ? 12 : (isCharging ? 100 : 80);

        AddDottedMeter("mood", moodVal);
        AddDottedMeter("energy", energyVal);
        AddDottedMeter("power", powerVal);

        // Refresh outfits
        _outfitPanel.Children.Clear();
        var owned = new HashSet<string>(s.BuddyOwnedPacks ?? new List<string>());

        foreach (var meta in BuddyCosmeticCatalog.Outfits)
        {
            bool unlocked = BuddyCosmeticCatalog.IsOutfitUnlocked(meta.Outfit, owned, s.BuddyShopDemoMode);
            bool active = s.BuddyOutfit == meta.Outfit;

            var btn = new Button
            {
                Margin = new Thickness(0, 0, 5, 4),
                Padding = new Thickness(7, 2, 7, 2),
                Cursor = Cursors.Hand,
                FontSize = 10.5,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                BorderThickness = new Thickness(0),
                ToolTip = unlocked ? meta.Description : $"{meta.DisplayName} ({meta.Pack} Pack)",
                Background = Brushes.Transparent,   // the pill colour is on the inner border
                Template = FlatButton,
            };

            var btnBorder = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(7, 2, 7, 2)
            };

            if (active)
            {
                btnBorder.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xCF, 0x4D));
                var tb = new TextBlock
                {
                    Text = meta.DisplayName,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x20, 0x14, 0x00)),
                    FontWeight = FontWeights.SemiBold
                };
                btnBorder.Child = tb;
            }
            else if (!unlocked)
            {
                btnBorder.Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
                var tb = new TextBlock
                {
                    Text = $"\uD83D\uDD12 {meta.DisplayName}",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x72))
                };
                btnBorder.Child = tb;
            }
            else
            {
                btnBorder.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
                var tb = new TextBlock
                {
                    Text = meta.DisplayName,
                    Foreground = Brushes.White
                };
                btnBorder.Child = tb;
            }

            btn.Content = btnBorder;
            var outfitEnum = meta.Outfit;
            btn.Click += (_, _) =>
            {
                if (!unlocked)
                {
                    SpeakRequested?.Invoke($"That outfit is part of the {meta.Pack} pack! Try it in Demo mode.");
                    return;
                }
                OutfitSelected?.Invoke(outfitEnum);
            };

            _outfitPanel.Children.Add(btn);
        }

        // Refresh Shop Panel
        _shopPanel.Children.Clear();
        var shopHeader = new TextBlock
        {
            Text = "LOCAL COSMETIC PACKS (DEMO MODE)",
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xCF, 0x4D)),
            Margin = new Thickness(0, 0, 0, 4)
        };
        _shopPanel.Children.Add(shopHeader);

        var packRow = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var pack in BuddyCosmeticCatalog.Packs.Where(p => p.Id != CosmeticPackId.Core))
        {
            bool packOwned = owned.Contains(pack.Id.ToString()) || s.BuddyShopDemoMode;
            var packBtn = new Button
            {
                Margin = new Thickness(0, 0, 6, 4),
                Padding = new Thickness(8, 3, 8, 3),
                Cursor = Cursors.Hand,
                FontSize = 10,
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(packOwned ? Color.FromArgb(0x30, 0x7C, 0xF0, 0xC1) : Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
                ToolTip = pack.Description,
                Template = FlatButton,
            };
            var packTb = new TextBlock
            {
                Text = $"{pack.Title} · {(packOwned ? "ACTIVE" : "DEMO TRY")}",
                Foreground = packOwned ? new SolidColorBrush(Color.FromRgb(0x7C, 0xF0, 0xC1)) : Brushes.White
            };
            packBtn.Content = packTb;
            var packId = pack.Id;
            packBtn.Click += (_, _) =>
            {
                DemoPackToggled?.Invoke(packId);
            };
            packRow.Children.Add(packBtn);
        }
        _shopPanel.Children.Add(packRow);

        _statsText.Text = $"streak {s.BuddyStreak} days \u00B7 pets {s.BuddyPets} \u00B7 focus sessions {s.BuddyFocusSessions}";
    }

    private void AddDottedMeter(string name, int value)
    {
        var meterStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var label = new TextBlock
        {
            Text = name.ToLowerInvariant(),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 9.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x96)),
            Width = 44,
            VerticalAlignment = VerticalAlignment.Center
        };
        meterStack.Children.Add(label);

        // Dotted bar: 8 dots (2 px width with 2 px gaps)
        var dotsContainer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };

        double hue = Math.Clamp(value * 1.1, 0, 120);
        var activeBrush = new SolidColorBrush(HslToRgb(hue, 0.65, 0.58));
        var inactiveBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));

        int activeCount = (int)Math.Round((value / 100.0) * 8);

        for (int i = 0; i < 8; i++)
        {
            var dot = new Border
            {
                Width = 4,
                Height = 5,
                CornerRadius = new CornerRadius(1.5),
                Background = i < activeCount ? activeBrush : inactiveBrush,
                Margin = new Thickness(0, 0, 2, 0)
            };
            dotsContainer.Children.Add(dot);
        }

        meterStack.Children.Add(dotsContainer);
        _metersPanel.Children.Add(meterStack);
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
        double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
        double m = l - c / 2.0;
        double r = 0, g = 0, b = 0;

        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return Color.FromRgb(
            (byte)Math.Clamp((r + m) * 255.0, 0, 255),
            (byte)Math.Clamp((g + m) * 255.0, 0, 255),
            (byte)Math.Clamp((b + m) * 255.0, 0, 255)
        );
    }
}
