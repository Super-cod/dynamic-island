using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WindowsDynamicBar.Core.Clipboard;
using WindowsDynamicBar.Core.Settings;
using WindowsDynamicBar.Core.Telemetry;
using WindowsDynamicBar.Services.Settings;
using WindowsDynamicBar.Services.SystemMonitor;
using WindowsDynamicBar.Services.Timers;
using WindowsDynamicBar.Services.Weather;
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

        // 1. Settings Window Pages
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
                SavePng(rtb, System.IO.Path.Combine(outputDir, filename));
            }
            catch (Exception ex) { Console.WriteLine($"{page} error: {ex.Message}"); }
        }

        // 2. First-Run Onboarding Wizard
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
            SavePng(rtb, System.IO.Path.Combine(outputDir, "first-run.png"));
        }
        catch (Exception ex) { Console.WriteLine("first-run.png error: " + ex.Message); }

        // 3. System Telemetry Detail Panel (populated with real CPU, 16 cores, ACPI temp, btop graph)
        try
        {
            var panel = new TelemetryDetailPanel();
            var cpu = new CpuStats(28.4, "AMD Ryzen 7 7840HS with Radeon 780M", 3800, 8, 16, true);
            var cores = new double[] { 24, 38, 19, 44, 15, 29, 31, 20, 26, 42, 19, 33, 14, 28, 22, 17 };
            var history = new TelemetryHistory(60);
            for (int i = 0; i < 60; i++)
            {
                double val = 20 + 14 * Math.Sin(i * 0.25) + (i % 6);
                history.Push(val);
            }
            var procs = new ProcessSample[]
            {
                new("Code.exe", 9.4),
                new("chrome.exe", 7.1),
                new("WindowsDynamicBar.exe", 2.8),
                new("Spotify.exe", 1.9)
            };
            panel.ShowCpu(cpu, cores, history, procs, tempC: 48.0, load: (24.0, 21.0, 18.0));
            RenderHost(panel, 522, 166, System.IO.Path.Combine(outputDir, "system-telemetry.png"));
        }
        catch (Exception ex) { Console.WriteLine("system-telemetry.png error: " + ex.Message); }

        // 4. Timer Detail Panel (with active countdown digits, progress bar and controls)
        try
        {
            var panel = new TimerDetailPanel();
            panel.Render(new TimerInfo("Focus Timer", TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(32), TimerState.Running, 0.42));
            RenderHost(panel, 522, 166, System.IO.Path.Combine(outputDir, "timer.png"));
        }
        catch (Exception ex) { Console.WriteLine("timer.png error: " + ex.Message); }

        // 5. Speed Test & Network Detail Panel
        try
        {
            var net = new NetworkMonitor(SettingsService.Instance);
            var snapField = typeof(NetworkMonitor).GetField("_snapshot", BindingFlags.NonPublic | BindingFlags.Instance);
            var testField = typeof(NetworkMonitor).GetField("_test", BindingFlags.NonPublic | BindingFlags.Instance);
            var downField = typeof(NetworkMonitor).GetField("_down", BindingFlags.NonPublic | BindingFlags.Instance);
            var upField = typeof(NetworkMonitor).GetField("_up", BindingFlags.NonPublic | BindingFlags.Instance);
            var lastPingField = typeof(NetworkMonitor).GetField("_lastPing", BindingFlags.NonPublic | BindingFlags.Instance);

            var snap = new NetSnapshot
            {
                HasAdapterData = true,
                DownMbps = 184.2,
                UpMbps = 42.8,
                LinkMbps = 1000,
                Connected = true,
                Internet = true,
                Kind = "Wi-Fi 6",
                SignalBars = 4,
                RssiDbm = -48,
                TodayRxBytes = 14L * 1024 * 1024 * 1024,
                TodayTxBytes = 3L * 1024 * 1024 * 1024
            };
            var test = new NetSpeedState
            {
                Phase = NetTestPhase.Done,
                ProgressPercent = 100,
                PingMs = 12,
                JitterMs = 2,
                LossPercent = 0,
                DownMbps = 184.2,
                UpMbps = 42.8
            };

            snapField?.SetValue(net, snap);
            testField?.SetValue(net, test);

            var downHist = (TelemetryHistory?)downField?.GetValue(net);
            var upHist = (TelemetryHistory?)upField?.GetValue(net);
            if (downHist != null && upHist != null)
            {
                for (int i = 0; i < 40; i++)
                {
                    downHist.Push(120 + 60 * Math.Sin(i * 0.3));
                    upHist.Push(30 + 12 * Math.Cos(i * 0.3));
                }
            }

            var lastPing = (Dictionary<NetTarget, double?>?)lastPingField?.GetValue(net);
            if (lastPing != null)
            {
                lastPing[NetTarget.Gateway] = 2;
                lastPing[NetTarget.Dns] = 8;
                lastPing[NetTarget.Cloudflare] = 12;
                lastPing[NetTarget.Google] = 15;
            }

            var panel = new NetDetailPanel();
            panel.Render(net);
            RenderHost(panel, 522, 166, System.IO.Path.Combine(outputDir, "speed-test.png"));
        }
        catch (Exception ex) { Console.WriteLine("speed-test.png error: " + ex.Message); }

        // 6. Clipboard Detail Panel (with populated history entries and search)
        try
        {
            var panel = new ClipboardDetailPanel();
            var items = new List<ClipItem>
            {
                new()
                {
                    Id = 1,
                    Kind = ClipKind.Text,
                    Hash = "h1",
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-2),
                    Pinned = true,
                    Text = "dotnet publish -c Release -r win-x64 --self-contained"
                },
                new()
                {
                    Id = 2,
                    Kind = ClipKind.Link,
                    Hash = "h2",
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-14),
                    Text = "https://github.com/Super-cod/dynamic-island"
                },
                new()
                {
                    Id = 3,
                    Kind = ClipKind.Text,
                    Hash = "h3",
                    CreatedUtc = DateTime.UtcNow.AddHours(-1),
                    Text = "#0A84FF (Accent Blue)"
                },
                new()
                {
                    Id = 4,
                    Kind = ClipKind.Text,
                    Hash = "h4",
                    CreatedUtc = DateTime.UtcNow.AddHours(-3),
                    Text = "island -Build"
                }
            };
            panel.Render(items, 50, paused: false, enabled: true);
            RenderHost(panel, 522, 166, System.IO.Path.Combine(outputDir, "clipboard.png"));
        }
        catch (Exception ex) { Console.WriteLine("clipboard.png error: " + ex.Message); }

        // 7. Weather Detail Panel (with 24h curve and metrics)
        try
        {
            var panel = new WeatherDetailPanel();
            var weather = new WeatherSnapshot
            {
                Available = true,
                LocationName = "Chennai, IN",
                Latitude = 13.08,
                Longitude = 80.27,
                TempC = 29.4,
                ApparentC = 33.1,
                Humidity = 72,
                WindKmh = 14.5,
                WeatherCode = 2,
                IsDay = true,
                UpdatedAt = DateTime.UtcNow,
                Sunrise = DateTime.Today.AddHours(6),
                Sunset = DateTime.Today.AddHours(18).AddMinutes(15),
                NowIndex = 12,
                Hours = Enumerable.Range(0, 24).Select(h => new WeatherHour
                {
                    Time = DateTime.Today.AddHours(h),
                    TempC = 26 + 6 * Math.Sin((h - 6) * Math.PI / 12),
                    WeatherCode = 2,
                    PrecipProb = 10,
                    UvIndex = h is >= 10 and <= 15 ? 7.5 : 1.0
                }).ToArray()
            };
            panel.Render(weather);
            RenderHost(panel, 522, 166, System.IO.Path.Combine(outputDir, "weather-panel.png"));
        }
        catch (Exception ex) { Console.WriteLine("weather-panel.png error: " + ex.Message); }

        // 8. Search Launcher UI
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
            RenderHost(searchBorder, 560, 240, System.IO.Path.Combine(outputDir, "search-panel.png"));
        }
        catch (Exception ex) { Console.WriteLine("search-panel.png error: " + ex.Message); }

        // 9. Main Bar (Collapsed Island Pill)
        try
        {
            var pillBorder = CreateIslandPill();
            RenderHost(pillBorder, 420, 80, System.IO.Path.Combine(outputDir, "main-bar.png"));
        }
        catch (Exception ex) { Console.WriteLine("main-bar.png error: " + ex.Message); }

        // 10. Desktop Bar (Header Banner showing Island on desktop)
        try
        {
            var desktopBar = new Border
            {
                Width = 1000,
                Height = 150,
                Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop(Color.FromRgb(20, 22, 28), 0.0),
                        new GradientStop(Color.FromRgb(28, 30, 38), 0.5),
                        new GradientStop(Color.FromRgb(18, 19, 24), 1.0)
                    }
                },
                Child = new Grid
                {
                    Children =
                    {
                        new Border
                        {
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Top,
                            Margin = new Thickness(0, 18, 0, 0),
                            Child = CreateIslandPill()
                        }
                    }
                }
            };

            var win = new Window
            {
                Width = 1000,
                Height = 150,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Content = desktopBar
            };
            win.Show();
            Pump(250);
            var rtb = new RenderTargetBitmap(1000, 150, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(win);
            win.Close();
            SavePng(rtb, System.IO.Path.Combine(outputDir, "desktop-bar.png"));
        }
        catch (Exception ex) { Console.WriteLine("desktop-bar.png error: " + ex.Message); }

        // 11. Expanded Island Card (Media Player + Telemetry)
        try
        {
            RenderHost(CreateExpandedCard(), 560, 150, System.IO.Path.Combine(outputDir, "expanded-panel.png"));
            RenderHost(CreateExpandedCard(), 560, 150, System.IO.Path.Combine(outputDir, "media-player.png"));
        }
        catch (Exception ex) { Console.WriteLine("expanded-panel.png error: " + ex.Message); }
    }

    private static Border CreateIslandPill()
    {
        return new Border
        {
            Width = 340,
            Height = 38,
            CornerRadius = new CornerRadius(19),
            Background = new SolidColorBrush(Color.FromRgb(10, 10, 12)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(40, 40, 48)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 16,
                ShadowDepth = 3,
                Opacity = 0.65
            },
            Child = new Grid
            {
                Margin = new Thickness(7, 0, 10, 0),
                Children =
                {
                    // Left: Disc icon with accent glow
                    new Border
                    {
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Center,
                        Width = 24,
                        Height = 24,
                        CornerRadius = new CornerRadius(12),
                        Background = new SolidColorBrush(Color.FromRgb(22, 22, 28)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(10, 132, 255)),
                        BorderThickness = new Thickness(1.5),
                        Child = new TextBlock
                        {
                            Text = "\uE8D6",
                            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                            FontSize = 12,
                            Foreground = new SolidColorBrush(Color.FromRgb(10, 132, 255)),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    },

                    // Center-left: Track info
                    new StackPanel
                    {
                        Margin = new Thickness(36, 0, 95, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "d4vd - Feel It",
                                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                                FontSize = 12.5,
                                FontWeight = FontWeights.SemiBold,
                                Foreground = Brushes.White,
                                TextTrimming = TextTrimming.CharacterEllipsis
                            }
                        }
                    },

                    // Right: Clock & Battery
                    new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                        Orientation = Orientation.Horizontal,
                        Children =
                        {
                            new Border
                            {
                                Margin = new Thickness(0, 0, 7, 0),
                                Background = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)),
                                CornerRadius = new CornerRadius(6),
                                Padding = new Thickness(4, 1, 4, 1),
                                Child = new StackPanel
                                {
                                    Orientation = Orientation.Horizontal,
                                    Children =
                                    {
                                        new TextBlock
                                        {
                                            Text = "\uEBB5",
                                            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                                            FontSize = 10,
                                            Foreground = new SolidColorBrush(Color.FromRgb(48, 209, 88)),
                                            VerticalAlignment = VerticalAlignment.Center
                                        },
                                        new TextBlock
                                        {
                                            Text = " 88%",
                                            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                                            FontSize = 10,
                                            FontWeight = FontWeights.SemiBold,
                                            Foreground = Brushes.White,
                                            VerticalAlignment = VerticalAlignment.Center
                                        }
                                    }
                                }
                            },
                            new StackPanel
                            {
                                VerticalAlignment = VerticalAlignment.Center,
                                Children =
                                {
                                    new TextBlock
                                    {
                                        Text = "17:03",
                                        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                                        FontSize = 12,
                                        FontWeight = FontWeights.Bold,
                                        Foreground = new SolidColorBrush(Color.FromRgb(10, 132, 255)),
                                        HorizontalAlignment = HorizontalAlignment.Right
                                    },
                                    new TextBlock
                                    {
                                        Text = "Fri 9 Oct",
                                        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                                        FontSize = 8.5,
                                        Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 150)),
                                        HorizontalAlignment = HorizontalAlignment.Right
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    private static Border CreateExpandedCard()
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(12, 12, 15)),
            CornerRadius = new CornerRadius(26),
            Padding = new Thickness(16, 12, 16, 12),
            BorderBrush = new SolidColorBrush(Color.FromRgb(40, 40, 48)),
            BorderThickness = new Thickness(1),
            Width = 530,
            Height = 118,
            Child = new Grid
            {
                Children =
                {
                    // Vinyl disc
                    new Border
                    {
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Center,
                        Width = 54,
                        Height = 54,
                        CornerRadius = new CornerRadius(27),
                        Background = new SolidColorBrush(Color.FromRgb(22, 22, 26)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 70)),
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

                    // Track & controls
                    new StackPanel
                    {
                        Margin = new Thickness(70, 2, 150, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "Feel It",
                                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                                FontSize = 15,
                                FontWeight = FontWeights.Bold,
                                Foreground = Brushes.White
                            },
                            new TextBlock
                            {
                                Text = "d4vd \u2022 YouTube Music",
                                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                                FontSize = 12,
                                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 160)),
                                Margin = new Thickness(0, 1, 0, 5)
                            },
                            new Grid
                            {
                                Height = 4,
                                Margin = new Thickness(0, 0, 0, 6),
                                Children =
                                {
                                    new Border { Background = new SolidColorBrush(Color.FromRgb(45, 45, 52)), CornerRadius = new CornerRadius(2) },
                                    new Border { Background = new SolidColorBrush(Color.FromRgb(10, 132, 255)), CornerRadius = new CornerRadius(2), Width = 150, HorizontalAlignment = HorizontalAlignment.Left }
                                }
                            },
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Children =
                                {
                                    CreateMediaGlyph("\uE892"), // Prev
                                    CreateMediaGlyph("\uE769"), // Pause
                                    CreateMediaGlyph("\uE893")  // Next
                                }
                            }
                        }
                    },

                    // Telemetry chips
                    new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                        Orientation = Orientation.Horizontal,
                        Children =
                        {
                            CreateTelemetryChip("CPU", "28%", Color.FromRgb(48, 209, 88)),
                            CreateTelemetryChip("RAM", "54%", Color.FromRgb(10, 132, 255)),
                            CreateTelemetryChip("NET", "42M", Color.FromRgb(196, 143, 224))
                        }
                    }
                }
            }
        };
    }

    private static TextBlock CreateMediaGlyph(string glyph)
    {
        return new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 210)),
            Margin = new Thickness(0, 0, 12, 0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
    }

    private static Border CreateTelemetryChip(string label, string val, Color col)
    {
        return new Border
        {
            Margin = new Thickness(4, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 24)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(7, 5, 7, 5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, col.R, col.G, col.B)),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = label, FontSize = 9.5, Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 150)), HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = val, FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(col), HorizontalAlignment = HorizontalAlignment.Center }
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
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(10),
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
