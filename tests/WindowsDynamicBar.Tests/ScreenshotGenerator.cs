using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowsDynamicBar.UI;

namespace WindowsDynamicBar.Tests;

public static class ScreenshotGenerator
{
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void SavePng(RenderTargetBitmap rtb, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine($"Generated: {path} ({rtb.PixelWidth}x{rtb.PixelHeight})");
    }

    public static void GenerateAll(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var app = Application.Current ?? new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        string[] pages = new[] { "general", "about", "features", "appearance", "privacy" };
        foreach (var page in pages)
        {
            try
            {
                var win = new SettingsWindow();
                win.Width = 760;
                win.Height = 520;
                win.NavigateToPage(page);
                win.Show();
                Pump(250);
                int w = Math.Max(760, (int)win.ActualWidth);
                int h = Math.Max(520, (int)win.ActualHeight);
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(win);
                win.Close();
                string filename = page == "general" ? "settings.png" : $"{page}.png";
                SavePng(rtb, Path.Combine(outputDir, filename));
            }
            catch (Exception ex) { Console.WriteLine($"{page} error: {ex.Message}"); }
        }

        // First-Run Onboarding Wizard
        try
        {
            var win = new FirstRunWindow();
            win.Width = 620;
            win.Height = 440;
            win.Show();
            Pump(250);
            int w = Math.Max(620, (int)win.ActualWidth);
            int h = Math.Max(440, (int)win.ActualHeight);
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(win);
            win.Close();
            SavePng(rtb, Path.Combine(outputDir, "first-run.png"));
        }
        catch (Exception ex) { Console.WriteLine("first-run.png error: " + ex.Message); }

        // Telemetry Detail Panel
        try
        {
            var panel = new TelemetryDetailPanel();
            RenderHost(panel, 540, 340, Path.Combine(outputDir, "system-telemetry.png"));
        }
        catch (Exception ex) { Console.WriteLine("system-telemetry.png error: " + ex.Message); }

        // Speed Test Detail Panel
        try
        {
            var panel = new NetDetailPanel();
            RenderHost(panel, 540, 360, Path.Combine(outputDir, "speed-test.png"));
        }
        catch (Exception ex) { Console.WriteLine("speed-test.png error: " + ex.Message); }

        // Timer Detail Panel
        try
        {
            var panel = new TimerDetailPanel();
            RenderHost(panel, 540, 340, Path.Combine(outputDir, "timer.png"));
        }
        catch (Exception ex) { Console.WriteLine("timer.png error: " + ex.Message); }

        // Clipboard Detail Panel
        try
        {
            var panel = new ClipboardDetailPanel();
            RenderHost(panel, 540, 360, Path.Combine(outputDir, "clipboard.png"));
        }
        catch (Exception ex) { Console.WriteLine("clipboard.png error: " + ex.Message); }

        // Weather Detail Panel
        try
        {
            var panel = new WeatherDetailPanel();
            RenderHost(panel, 540, 340, Path.Combine(outputDir, "weather-panel.png"));
        }
        catch (Exception ex) { Console.WriteLine("weather-panel.png error: " + ex.Message); }

        // Search Host UI
        try
        {
            var searchBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(15, 15, 17)),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(14, 10, 14, 10),
                BorderBrush = new SolidColorBrush(Color.FromRgb(45, 45, 52)),
                BorderThickness = new Thickness(1),
                Width = 520,
                Child = new StackPanel
                {
                    Children =
                    {
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = "\uE721",
                                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                                    FontSize = 15,
                                    Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 175)),
                                    Margin = new Thickness(0, 0, 10, 0),
                                    VerticalAlignment = VerticalAlignment.Center
                                },
                                new TextBlock
                                {
                                    Text = "Search apps, files, math (e.g. 12 * 45)...",
                                    FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                                    FontSize = 14,
                                    Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 175)),
                                    VerticalAlignment = VerticalAlignment.Center
                                }
                            }
                        },
                        new Border
                        {
                            Margin = new Thickness(0, 12, 0, 0),
                            BorderBrush = new SolidColorBrush(Color.FromRgb(35, 35, 40)),
                            BorderThickness = new Thickness(0, 1, 0, 0),
                            Padding = new Thickness(0, 10, 0, 0),
                            Child = new StackPanel
                            {
                                Children =
                                {
                                    CreateSearchRow("Google Chrome", "Application - Press Enter to launch", "\uE737"),
                                    CreateSearchRow("Visual Studio Code", "Application - Recent", "\uE70F"),
                                    CreateSearchRow("Settings", "System - Windows Dynamic Bar", "\uE713"),
                                    CreateSearchRow("timer 15 min", "Island Command - Starts countdown", "\uE916")
                                }
                            }
                        }
                    }
                }
            };
            RenderHost(searchBorder, 560, 240, Path.Combine(outputDir, "search-panel.png"));
        }
        catch (Exception ex) { Console.WriteLine("search-panel.png error: " + ex.Message); }

        // Expanded Island Card
        try
        {
            var expandedCard = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(12, 12, 14)),
                CornerRadius = new CornerRadius(28),
                Padding = new Thickness(16, 12, 16, 12),
                BorderBrush = new SolidColorBrush(Color.FromRgb(40, 40, 45)),
                BorderThickness = new Thickness(1),
                Width = 480,
                Height = 110,
                Child = new Grid
                {
                    Children =
                    {
                        new Border
                        {
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Center,
                            Width = 54,
                            Height = 54,
                            CornerRadius = new CornerRadius(27),
                            Background = new SolidColorBrush(Color.FromRgb(25, 25, 28)),
                            BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 68)),
                            BorderThickness = new Thickness(2),
                            Child = new TextBlock
                            {
                                Text = "\uE8D6",
                                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                                FontSize = 24,
                                Foreground = new SolidColorBrush(Color.FromRgb(10, 132, 255)),
                                HorizontalAlignment = HorizontalAlignment.Center,
                                VerticalAlignment = VerticalAlignment.Center
                            }
                        },
                        new StackPanel
                        {
                            Margin = new Thickness(70, 4, 110, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = "Feel It",
                                    FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                                    FontSize = 15,
                                    FontWeight = FontWeights.SemiBold,
                                    Foreground = Brushes.White
                                },
                                new TextBlock
                                {
                                    Text = "d4vd - YouTube Music",
                                    FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                                    FontSize = 12,
                                    Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 160)),
                                    Margin = new Thickness(0, 1, 0, 6)
                                },
                                new Grid
                                {
                                    Height = 4,
                                    Children =
                                    {
                                        new Border { Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)), CornerRadius = new CornerRadius(2) },
                                        new Border { Background = new SolidColorBrush(Color.FromRgb(10, 132, 255)), CornerRadius = new CornerRadius(2), Width = 140, HorizontalAlignment = HorizontalAlignment.Left }
                                    }
                                }
                            }
                        },
                        new StackPanel
                        {
                            HorizontalAlignment = HorizontalAlignment.Right,
                            VerticalAlignment = VerticalAlignment.Center,
                            Orientation = Orientation.Horizontal,
                            Children =
                            {
                                CreateTelemetryChip("CPU", "28%", Color.FromRgb(48, 209, 88)),
                                CreateTelemetryChip("RAM", "54%", Color.FromRgb(10, 132, 255))
                            }
                        }
                    }
                }
            };
            RenderHost(expandedCard, 520, 150, Path.Combine(outputDir, "expanded-panel.png"));
        }
        catch (Exception ex) { Console.WriteLine("expanded-panel.png error: " + ex.Message); }
    }

    private static Border CreateTelemetryChip(string label, string val, Color col)
    {
        return new Border
        {
            Margin = new Thickness(4, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(22, 22, 26)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 6, 8, 6),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, col.R, col.G, col.B)),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = label, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 150)), HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = val, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(col), HorizontalAlignment = HorizontalAlignment.Center }
                }
            }
        };
    }

    private static Grid CreateSearchRow(string title, string subtitle, string glyph)
    {
        var row = new Grid { Margin = new Thickness(4, 4, 4, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(10, 132, 255)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var textStack = new StackPanel();
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        });
        textStack.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 150))
        });
        Grid.SetColumn(textStack, 1);
        row.Children.Add(textStack);

        return row;
    }

    private static void RenderHost(FrameworkElement element, int width, int height, string path)
    {
        var win = new Window
        {
            Width = width,
            Height = height,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(12, 12, 14)),
                CornerRadius = new CornerRadius(22),
                Padding = new Thickness(14),
                Child = element
            }
        };
        win.Show();
        Pump(250);
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(win);
        win.Close();
        SavePng(rtb, path);
    }
}