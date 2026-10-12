using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsDynamicBar.Core.Animation;
using WindowsDynamicBar.Core.Logging;
using WindowsDynamicBar.Core.Search;
using WindowsDynamicBar.Core.Telemetry;
using WindowsDynamicBar.Core.Timers;
using WindowsDynamicBar.Interop;
using WindowsDynamicBar.Services.Search;

namespace WindowsDynamicBar.UI;

/// <summary>
/// The search bar (Ctrl+Space). One small state machine owns the island geometry while search is
/// on screen: Closed -> Opening -> Open -> Closing -> Closed. Every transition stops the running
/// morph, starts from the CURRENT geometry and lands on absolute targets (660 x 58, radius 29,
/// top = 22 % of the work area) in the tween's Completed handler, guarded by a version number.
/// The spring animator never drives w/h/top/radius at the same time (see ClaimGeometryForSpring).
/// Default (empty text) = one rounded bar only; results appear after the first character.
/// </summary>
public partial class BarWindow
{
    private const double SearchBarMaxW = 660;
    private const double SearchBarH = 58;
    private const double SearchBarR = 29;
    private const double SearchTopFraction = 0.22;
    private const int SearchOpenMs = 600;
    private const int SearchCloseMs = 450;
    private const int SearchResizeMs = 550;

    // Result list geometry (fixed sizes, so the island height is known without a layout pass).
    private const double ResultRowH = 44 + 2;              // row + 1 px margin above and below
    private const double ResultHeaderFirstH = 22 + 2 + 2;  // header + margins (first group)
    private const double ResultHeaderH = 22 + 6 + 2;       // header + margins (later groups)
    private const double ResultsChromeH = 1 + 6 + 10;      // divider + list margin top/bottom

    private enum SearchPhase { Closed, Opening, Open, Closing }

    private SearchPhase _searchPhase = SearchPhase.Closed;
    private readonly TweenAnimation _searchMorph = new();
    private bool _blurSuspended;
    private int _morphVersion;
    private int _morphRunVersion;
    private Action? _morphDone;
    private double _mFromW, _mFromH, _mFromTop, _mFromR, _mToW, _mToH, _mToTop, _mToR, _mFromOpacity;
    private double _searchTargetH = SearchBarH;
    private bool _searchHadFocus;
    private bool _searchPrewarmed;
    private readonly RectangleGeometry _pillClip = new();

    // Results of the current query: in-memory rows (instant) + file rows (async) composed together.
    private readonly List<SearchResult> _pinnedRows = new();
    private readonly List<SearchResult> _scoredRows = new();
    private readonly List<SearchResult> _fileRows = new();
    private SearchResult? _webRow;
    private string _resultsQuery = "";

    private static readonly Brush SearchBorderBrush = FrozenBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    private static readonly Brush SearchGhostBrush = FrozenBrush(Color.FromArgb(0x5A, 0xFF, 0xFF, 0xFF));
    private static readonly Brush SearchRowSelectedBrush = FrozenBrush(Color.FromArgb(26, 255, 255, 255));

    private static Brush FrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private double SearchBarWidth => Math.Min(SearchBarMaxW, Math.Round(_winW * 0.94));
    private double SearchTop => Math.Round(_winH * SearchTopFraction);

    /// <summary>Constructor wiring (called once from the BarWindow constructor).</summary>
    private void InitSearch()
    {
        // The island content is clipped to the island's own rounded shape (ClipToBounds ignores
        // the corner radius); UpdateShape keeps this geometry in step with every animated frame.
        PillGrid.Clip = _pillClip;
        _searchMorph.Completed += OnSearchMorphCompleted;
        AppDiscoveryService.Instance.Loaded += OnAppsLoaded;
        Activated += (_, _) => { if (_searchPhase is SearchPhase.Opening or SearchPhase.Open) _searchHadFocus = true; };
        SearchInput.GotKeyboardFocus += (_, _) => { if (_searchPhase is SearchPhase.Opening or SearchPhase.Open) _searchHadFocus = true; };
    }

    /// <summary>Startup, at ApplicationIdle: load the app index (worker thread), lay the bar out once
    /// at its final size and warm the monitor/ranking paths, so the first Ctrl+Space runs exactly
    /// the same code as the 100th.</summary>
    private void PrewarmSearch()
    {
        if (_searchPrewarmed || _disposed || !_cfg.FeatureSearch) return;
        _searchPrewarmed = true;
        long t0 = TraceClock.ElapsedMilliseconds;

        AppDiscoveryService.Instance.RefreshInBackground();
        WindowSwitcherService.Instance.RefreshInBackground(_hwnd);
        _ = Task.Run(() => SearchRanker.MatchName("Visual Studio Code", "vsc"));
        _ = NativeMethods.GetMonitors();

        if (_searchPhase == SearchPhase.Closed && _islandState != IslandVisualState.Search)
        {
            // Hidden = measured and arranged (template applied, text shaped) but not rendered.
            SearchHost.Width = SearchBarWidth - 2;
            SearchHost.Visibility = Visibility.Hidden;
            SearchHost.UpdateLayout();
            SearchTrace("prewarm layout", ("hostW", SearchHost.ActualWidth), ("hostH", SearchHost.ActualHeight));
            SearchHost.Visibility = Visibility.Collapsed;
        }
        SearchTrace("prewarm done", ("ms", TraceClock.ElapsedMilliseconds - t0));
    }

    // ---------------------------------------------------------------- open / close

    public void ToggleSearch()
    {
        // While Opening/Open the hotkey closes; while Closing/Closed it (re)opens from the current geometry.
        if (_searchPhase is SearchPhase.Opening or SearchPhase.Open)
            CloseSearch();
        else
            OpenSearch();
    }

    public void OpenSearch()
    {
        if (!_cfg.FeatureSearch) return;
        SearchTrace("open begin", ("phase", _searchPhase), ("hostMeasured", SearchHost.IsMeasureValid));
        if (_isBuddyCardOpen) CloseBuddyCard();
        _buddyController?.EndSpeechImmediately(force: true);
        if (BuddyCardComponent is not null)
        {
            BuddyCardComponent.Visibility = Visibility.Collapsed;
            BuddyCardComponent.Opacity = 0;
            BuddyCardComponent.IsHitTestVisible = false;
        }
        if (BuddyClockRow is not null) BuddyClockRow.Visibility = Visibility.Visible;
        if (BuddySpeechText is not null) BuddySpeechText.Visibility = Visibility.Collapsed;
        SyncBuddyLayoutMode();
        OnBuddySearchOpened();
        if (_searchPhase is SearchPhase.Opening or SearchPhase.Open)
        {
            Activate();
            SearchInput.Focus();
            return;
        }

        var fg = NativeMethods.GetForegroundWindow();
        if (fg != _hwnd) _prevForegroundHwnd = fg;
        // Auto-hidden for a fullscreen app: the hotkey must still show the bar in this frame,
        // not on the next 1.5 s fullscreen check.
        if (!IsVisible) Show();
        EnsureOnCursorMonitor();
        SearchTrace("window placed (tall)");

        _searchPhase = SearchPhase.Opening;
        _searchHadFocus = false;
        SetIslandState(IslandVisualState.Search, IslandMetric.None, force: true);
        SearchTrace("views switched");

        ResetSearchContent();
        RemoveAppTag();
        SearchHost.Width = SearchBarWidth - 2;   // final layout width: content never reflows while the shape grows
        Pill.BorderBrush = SearchBorderBrush;

        _searchTargetH = SearchBarH;
        MorphSearch(SearchBarWidth, SearchBarH, SearchTop, SearchBarR, ScaledMs(SearchOpenMs), OnSearchOpened);
        SearchTrace("animation started", ("toW", SearchBarWidth), ("toH", SearchBarH), ("toTop", SearchTop));

        AppDiscoveryService.Instance.RefreshInBackground();
        WindowSwitcherService.Instance.RefreshInBackground(_hwnd);

        Activate();
        SearchInput.Focus();
        SearchTrace("open handler done", ("active", IsActive), ("focused", SearchInput.IsKeyboardFocused));
    }

    public void CloseSearch()
    {
        SearchTrace("close begin", ("phase", _searchPhase));
        if (_islandState != IslandVisualState.Search) return;
        OnBuddySearchClosed();

        // Leaving Search runs LeaveSearchMode, and the Collapsed case morphs back to the pill.
        SetIslandState(IslandVisualState.Collapsed, IslandMetric.None, force: true);

        if (_prevForegroundHwnd != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(_prevForegroundHwnd);
            _prevForegroundHwnd = IntPtr.Zero;
        }
    }

    private void OnSearchOpened()
    {
        _searchPhase = SearchPhase.Open;
        SearchTrace("open settled", ("active", IsActive), ("hadFocus", _searchHadFocus));
        // A click elsewhere during Opening was ignored; honour it now that the bar is fully open.
        if (_searchHadFocus && !IsActive) CloseSearch();
    }

    /// <summary>Called by SetIslandState when the island leaves the Search state.</summary>
    private void LeaveSearchMode(IslandVisualState next)
    {
        ResetSearchContent();
        RemoveAppTag();
        if (next == IslandVisualState.Collapsed && !_cfg.ReduceMotion)
            _searchPhase = SearchPhase.Closing;   // the Collapsed case starts the closing morph
        else
        {
            StopSearchMorph();
            FinishSearchClose(reveal: false);
        }
    }

    /// <summary>The closing morph landed on the collapsed pill.</summary>
    private void OnSearchClosed() => FinishSearchClose(reveal: true);

    private void FinishSearchClose(bool reveal)
    {
        _searchPhase = SearchPhase.Closed;
        SearchHost.Visibility = Visibility.Collapsed;
        SearchHost.Opacity = 0;
        Pill.SetResourceReference(Border.BorderBrushProperty, "WdbHairlineBrush");
        if (_warningStripActive)
        {
            WarningStrip.Visibility = Visibility.Visible;
            WarningStrip.Opacity = 1;
        }
        if (reveal && _islandState == IslandVisualState.Collapsed && !_expanded)
        {
            // The collapsed pill's text comes back only once the shape is the pill again.
            CollapsedPanel.Visibility = Visibility.Visible;
            OrbHost.Visibility = Visibility.Visible;
            Fade(CollapsedPanel, 1, 160);
            Fade(OrbHost, 1, 160);

            if (BuddyContainer is not null && App.Settings?.Current.FeatureBuddy == true)
            {
                BuddyContainer.Visibility = Visibility.Visible;
                BuddyContainer.Opacity = 1;
                BuddyContainer.IsHitTestVisible = true;
            }
            if (BuddyClockRow is not null) BuddyClockRow.Visibility = Visibility.Visible;
            if (BuddySpeechText is not null) BuddySpeechText.Visibility = Visibility.Collapsed;
            _buddyController?.CheckVisibilityAndRun();
            SyncBuddyLayoutMode();
        }
        if (_islandState != IslandVisualState.Search) SetTallWindow(false);
        SearchTrace("close settled", ("reveal", reveal));
    }

    /// <summary>Another code path is about to spring-animate the island geometry. If the search bar
    /// is still closing, hand the geometry over (one owner at a time) and finish the close.</summary>
    private void ClaimGeometryForSpring(bool reveal)
    {
        if (_searchPhase != SearchPhase.Closing) return;
        StopSearchMorph();
        FinishSearchClose(reveal);
    }

    private void EnsureOnCursorMonitor()
    {
        NativeMethods.GetCursorPos(out var pt);
        var monitors = NativeMethods.GetMonitors();
        if (monitors.Count == 0) return;
        var mon = monitors.FirstOrDefault(m =>
            pt.X >= m.Bounds.Left && pt.X < m.Bounds.Right &&
            pt.Y >= m.Bounds.Top && pt.Y < m.Bounds.Bottom) ?? monitors[0];

        _tallWindow = true;
        PlaceWindow(mon);
    }

    // ---------------------------------------------------------------- morph (the one geometry owner)

    /// <summary>Open/close durations shrink with the remaining distance, so reversing half way
    /// (hotkey or Esc during the morph) takes half the time instead of restarting.</summary>
    private int ScaledMs(int fullMs)
    {
        double open = Math.Clamp((_currentTop - PillTop) / Math.Max(1, SearchTop - PillTop), 0, 1);
        double remaining = _searchPhase == SearchPhase.Closing ? open : 1 - open;
        return (int)(fullMs * Math.Clamp(remaining, 0.35, 1));
    }

    private void MorphSearch(double w, double h, double top, double r, int ms, Action? done)
    {
        // Single owner: the spring tracks for the same properties are reset (no leftover velocity).
        _anim.Jump("w", _currentW, ApplyW);
        _anim.Jump("h", _currentH, ApplyH);
        _anim.Jump("top", _currentTop, ApplyTop);
        _anim.Jump("radius", _currentCornerRadius, ApplyCornerRadius);

        _morphRunVersion = ++_morphVersion;
        _morphDone = done;
        _mFromW = _currentW; _mFromH = _currentH; _mFromTop = _currentTop; _mFromR = _currentCornerRadius;
        _mFromOpacity = SearchHost.Opacity;
        _mToW = w; _mToH = h; _mToTop = top; _mToR = r;
        _traceLastFrameMs = -1;

        if (_cfg.ReduceMotion || ms <= 0)
        {
            _searchMorph.Stop();
            OnSearchMorphCompleted();
            return;
        }
        SuspendBlur(true);
        _searchMorph.Run(0, 1, TimeSpan.FromMilliseconds(ms), ApplySearchMorph, Easing.QuintOut);
    }

    private void StopSearchMorph()
    {
        _searchMorph.Stop();
        _morphVersion++;
        _morphDone = null;
        SuspendBlur(false);
    }

    /// <summary>The pill is opaque, so DWM blur-behind is invisible inside it; but its region is applied
    /// asynchronously and trails a fast morph as a grey box. Off while morphing, restored on landing.</summary>
    private void SuspendBlur(bool suspend)
    {
        if (suspend == _blurSuspended || !_blurOn) { _blurSuspended = suspend && _blurOn; return; }
        _blurSuspended = suspend;
        BlurHelper.Disable();
    }

    private void ApplySearchMorph(double p)
    {
        SearchTraceFrame();
        _currentW = Math.Max(20, _mFromW + (_mToW - _mFromW) * p);
        _currentH = Math.Max(10, _mFromH + (_mToH - _mFromH) * p);
        _currentTop = _mFromTop + (_mToTop - _mFromTop) * p;
        _currentCornerRadius = _mFromR + (_mToR - _mFromR) * p;
        Pill.Width = _currentW;
        Pill.Height = _currentH;
        UpdatePillMargin();
        UpdateShape();
        // Bar content appears only in the last 90 px of growth and is gone within the first quarter
        // of a close, so text is never seen squeezed against the moving edge of the shape.
        SearchHost.Opacity = _searchPhase switch
        {
            SearchPhase.Closing => _mFromOpacity * Math.Clamp(1 - 4 * p, 0, 1),
            SearchPhase.Opening or SearchPhase.Open => Math.Clamp((_currentW - (SearchBarWidth - 90)) / 60, 0, 1),
            _ => SearchHost.Opacity,
        };
    }

    private void OnSearchMorphCompleted()
    {
        if (_morphRunVersion != _morphVersion) return;   // superseded by a newer transition
        ApplySearchMorph(1);                              // exact final values
        SyncFills();
        SuspendBlur(false);
        _traceLastFrameMs = -1;
        var done = _morphDone;
        _morphDone = null;
        SearchTrace("morph completed");
        done?.Invoke();
    }

    /// <summary>Height-only change while searching (list appears / disappears).</summary>
    private void RetargetSearchHeight(double h)
    {
        if (_searchPhase is not (SearchPhase.Opening or SearchPhase.Open)) return;
        if (Math.Abs(h - _searchTargetH) < 0.5 && (_searchMorph.IsRunning || Math.Abs(h - _currentH) < 0.5)) return;
        _searchTargetH = h;
        MorphSearch(SearchBarWidth, h, SearchTop, SearchBarR, SearchResizeMs,
            _searchPhase == SearchPhase.Opening ? OnSearchOpened : null);
    }

    // ---------------------------------------------------------------- input

    /// <summary>Back to the default look: one bar, empty text, no list, no hints.</summary>
    private void ResetSearchContent()
    {
        _searchCts?.Cancel();
        _searchCts = null;
        if (SearchInput.Text.Length > 0) SearchInput.Text = "";
        ClearResults();
        SearchPlaceholder.Visibility = Visibility.Visible;
        _armedRiskyCommand = false;
        _armedCommandTitle = "";
    }

    private void ClearResults()
    {
        _searchResults.Clear();
        _pinnedRows.Clear();
        _scoredRows.Clear();
        _fileRows.Clear();
        _webRow = null;
        _resultsQuery = "";
        _selectedResultIndex = -1;
        SearchResultsList.Children.Clear();
        SearchResultsSection.Visibility = Visibility.Collapsed;
        SetGhost(null, "");
        SearchTabHint.Visibility = Visibility.Collapsed;
    }

    private void ActivateAppTag(AppInfo app)
    {
        _activeTag = app;
        SearchAppTagName.Text = app.Name;
        if (app.Icon != null)
        {
            SearchAppTagIcon.Source = app.Icon;
            SearchAppTagIcon.Visibility = Visibility.Visible;
            SearchAppTagLetter.Visibility = Visibility.Collapsed;
        }
        else
        {
            SearchAppTagLetter.Text = app.Name.Length > 0 ? char.ToUpperInvariant(app.Name[0]).ToString() : "•";
            SearchAppTagLetter.Visibility = Visibility.Visible;
            SearchAppTagIcon.Visibility = Visibility.Collapsed;
        }
        SearchAppTag.Visibility = Visibility.Visible;

        SearchInput.Text = "";
        SearchPlaceholder.Text = GetPlaceholderForApp(app);
        SearchPlaceholder.Visibility = Visibility.Visible;
        ClearResults();
        RetargetSearchHeight(SearchBarH);
    }

    private void RemoveAppTag()
    {
        _activeTag = null;
        SearchAppTag.Visibility = Visibility.Collapsed;
        SearchAppTagIcon.Source = null;
        SearchPlaceholder.Text = "Search";
    }

    private static string GetPlaceholderForApp(AppInfo app)
    {
        string name = app.Name.ToLowerInvariant();
        if (name.Contains("chrome") || name.Contains("edge") || name.Contains("firefox") || name.Contains("browser"))
            return "Search Google or enter URL";
        if (name.Contains("terminal") || name.Contains("powershell") || name.Contains("cmd"))
            return "Run command or script";
        if (name.Contains("code") || name.Contains("visual studio"))
            return "Open folder or file";
        if (name.Contains("spotify") || name.Contains("music"))
            return "Search songs or albums";
        if (name.Contains("explorer") || name.Contains("files"))
            return "Open folder or path";
        return "Type a command";
    }

    private void OnSearchInputChanged(object sender, TextChangedEventArgs e)
    {
        if (_islandState != IslandVisualState.Search) return;

        string text = SearchInput.Text;
        SearchPlaceholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _armedRiskyCommand = false;
        _armedCommandTitle = "";

        if (string.IsNullOrWhiteSpace(text))
        {
            _searchCts?.Cancel();
            _searchCts = null;
            ClearResults();
            RetargetSearchHeight(SearchBarH);
            return;
        }

        UpdateSearchResults(text);
    }

    private void OnSearchInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_islandState != IslandVisualState.Search) return;

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseSearch();
                return;

            case Key.Down or Key.Up:
                e.Handled = true;
                _armedRiskyCommand = false;
                _armedCommandTitle = "";
                if (_searchResults.Count > 0)
                {
                    int step = e.Key == Key.Down ? 1 : -1;
                    _selectedResultIndex = (_selectedResultIndex + step + _searchResults.Count) % _searchResults.Count;
                    UpdateRowSelections();
                    ScrollSelectedIntoView();
                }
                return;

            case Key.Tab:
                e.Handled = true;
                if (SelectedApp() is { } app) ActivateAppTag(app);
                return;

            case Key.Back when string.IsNullOrEmpty(SearchInput.Text) && _activeTag != null:
                e.Handled = true;
                string restored = _activeTag.Name;
                RemoveAppTag();
                SearchInput.Text = restored;
                SearchInput.CaretIndex = restored.Length;
                return;

            case Key.Enter:
                e.Handled = true;
                if (_selectedResultIndex >= 0 && _selectedResultIndex < _searchResults.Count)
                    ExecuteResult(_searchResults[_selectedResultIndex]);
                else if (!string.IsNullOrWhiteSpace(SearchInput.Text))
                    ExecuteWebSearch(SearchInput.Text.Trim());
                return;
        }
    }

    /// <summary>The app behind the selected row (Tab scopes commands to it).</summary>
    private AppInfo? SelectedApp()
    {
        if (_selectedResultIndex < 0 || _selectedResultIndex >= _searchResults.Count) return null;
        var sel = _searchResults[_selectedResultIndex];
        if (sel.Category != SearchCategory.Apps || string.IsNullOrEmpty(sel.AppId)) return null;
        return AppDiscoveryService.Instance.GetApps()
            .FirstOrDefault(a => a.AppId.Equals(sel.AppId, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- results

    private void UpdateSearchResults(string text)
    {
        string query = text.Trim();
        _pinnedRows.Clear();
        _scoredRows.Clear();
        _fileRows.Clear();
        _webRow = null;
        _resultsQuery = query;
        _searchCts?.Cancel();
        _searchCts = null;

        if (_activeTag != null)
        {
            SetGhost(null, "");
            _scoredRows.AddRange(AppCommandRegistry.GetScopedCommands(_activeTag, query).Take(SearchRanker.MaxRows));
            ComposeAndRender(useRanker: false);
            return;
        }

        // "w " prefix: only windows (an empty filter lists all, most recently used first).
        if (text.StartsWith("w ", StringComparison.OrdinalIgnoreCase))
        {
            SetGhost(null, "");
            AddWindowResults(text[2..].Trim(), SearchRanker.MaxRows, useRanker: false);
            ComposeAndRender(useRanker: false);
            return;
        }

        // ">" prefix: only commands (an empty filter lists them all).
        if (text.StartsWith('>'))
        {
            SetGhost(null, "");
            _scoredRows.AddRange(GetSystemCommands(text[1..].Trim()));
            ComposeAndRender(useRanker: false);
            return;
        }

        // 1. Timer and calculator / unit conversion rows come first.
        if (_cfg.FeatureTimers && TimerCommand.TryParseSearch(query, out var timerDuration))
        {
            var d = timerDuration;
            _pinnedRows.Add(new SearchResult
            {
                Title = $"Start timer {TimerCommand.Format(d)}",
                Subtitle = "Countdown in the island",
                Category = SearchCategory.Commands,
                FallbackLetter = "⏱",
                FallbackColor = Color.FromRgb(0xE8, 0xB0, 0x4B),
                KeyboardHint = "Start",
                Action = () => App.Timer?.Start(d, "Timer"),
            });
        }

        var (evalOk, evalRes, evalSub) = ExpressionEvaluator.TryEvaluate(query);
        if (evalOk)
        {
            string capturedRes = evalRes;
            _pinnedRows.Add(new SearchResult
            {
                Title = evalRes,
                Subtitle = evalSub,
                Category = SearchCategory.Calculator,
                FallbackLetter = "=",
                FallbackColor = Color.FromRgb(0x2F, 0xD2, 0xB0),
                KeyboardHint = "Copy",
                Action = () =>
                {
                    Clipboard.SetText(capturedRes);
                    ShowActionSuccess($"✓ Copied {capturedRes}");
                }
            });
        }

        // 2. Apps (in memory), windows (cached list) and commands, all on one relevance scale.
        var now = DateTime.UtcNow;
        foreach (var (app, score, match) in SearchRanker.RankApps(AppDiscoveryService.Instance.GetApps(), query, now))
        {
            var capturedApp = app;
            _scoredRows.Add(new SearchResult
            {
                Title = app.Name,
                Subtitle = "Application",
                Category = SearchCategory.Apps,
                Icon = app.Icon,
                FallbackLetter = app.Name.Length > 0 ? char.ToUpperInvariant(app.Name[0]).ToString() : "A",
                KeyboardHint = "Open",
                Score = score,
                MatchedIndices = match.Indices,
                AppId = app.AppId,
                ExePath = app.ExePath,
                Action = () =>
                {
                    AppDiscoveryService.Instance.RecordLaunch(capturedApp.AppId);
                    Process.Start(new ProcessStartInfo(capturedApp.ExePath) { UseShellExecute = true });
                    ShowActionSuccess($"✓ Opened {capturedApp.Name}");
                }
            });
        }
        AddWindowResults(query, SearchRanker.MaxRows, useRanker: true);
        _scoredRows.AddRange(GetSystemCommands(query));

        _webRow = CreateWebRow(query);
        ComposeAndRender(useRanker: true);

        // 3. Files from the Windows Search index: 2+ chars, 80 ms debounce, previous request cancelled.
        if (query.Length < 2) return;
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            List<SearchResult> files;
            try { files = await WindowsSearchService.SearchFilesAsync(query, token); }
            catch (OperationCanceledException) { return; }   // superseded by a newer keystroke
            if (token.IsCancellationRequested || files.Count == 0) return;

            _ = Dispatcher.BeginInvoke(() =>
            {
                if (token.IsCancellationRequested || _islandState != IslandVisualState.Search || _resultsQuery != query) return;
                _fileRows.Clear();
                _fileRows.AddRange(files);
                ComposeAndRender(useRanker: true);
            });
        });
    }

    private SearchResult CreateWebRow(string query)
    {
        bool url = SearchRanker.LooksLikeUrl(query);
        string target = url
            ? (query.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? query : "https://" + query)
            : $"https://www.google.com/search?q={Uri.EscapeDataString(query)}";
        return new SearchResult
        {
            Title = url ? $"Open {query}" : $"Search the web for \"{query}\"",
            Subtitle = url ? "Open in default browser" : "Google",
            Category = SearchCategory.Web,
            FallbackLetter = url ? "↗" : "G",
            FallbackColor = Color.FromRgb(0x42, 0x85, 0xF4),
            KeyboardHint = url ? "Open" : "Search",
            Action = () =>
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                ShowActionSuccess(url ? "✓ Opened link" : "✓ Searched the web");
            },
        };
    }

    /// <summary>Build the visible rows (max 6) from the current pieces and render them.</summary>
    private void ComposeAndRender(bool useRanker)
    {
        _searchResults.Clear();
        if (useRanker)
            _searchResults.AddRange(SearchRanker.Compose(_pinnedRows, _scoredRows.Concat(_fileRows), _webRow, _resultsQuery));
        else
            _searchResults.AddRange(_scoredRows.Take(SearchRanker.MaxRows));

        _selectedResultIndex = _searchResults.Count > 0 ? 0 : -1;   // the top hit is preselected; Enter runs it
        UpdateTopHitHints();
        RenderResultRows();
    }

    /// <summary>Ghost completion and the "Tab" hint exist only when the top hit is an app.</summary>
    private void UpdateTopHitHints()
    {
        var top = _searchResults.Count > 0 ? _searchResults[0] : null;
        bool topIsApp = _activeTag is null && top is { Category: SearchCategory.Apps };
        SearchTabHint.Visibility = topIsApp ? Visibility.Visible : Visibility.Collapsed;

        string text = SearchInput.Text;
        if (topIsApp && text.Length > 0 && top!.Title.Length > text.Length &&
            top.Title.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            SetGhost(text, top.Title[text.Length..]);
        else
            SetGhost(null, "");
    }

    private void SetGhost(string? typed, string remainder)
    {
        _ghostRemainder = remainder;
        SearchGhostText.Inlines.Clear();
        if (typed is null || remainder.Length == 0) return;
        SearchGhostText.Inlines.Add(new Run(typed) { Foreground = Brushes.Transparent });
        SearchGhostText.Inlines.Add(new Run(remainder) { Foreground = SearchGhostBrush });
    }

    private void AddWindowResults(string query, int max, bool useRanker)
    {
        var svc = WindowSwitcherService.Instance;
        IEnumerable<(WindowEntry Window, int Score, List<int>? Matched)> windows;
        if (useRanker)
        {
            var ranked = new List<(WindowEntry, int, List<int>?)>();
            foreach (var w in svc.Windows)
            {
                var t = SearchRanker.MatchName(w.Title, query);
                var p = SearchRanker.MatchName(w.ProcessName, query);
                if (!t.Matched && !p.Matched) continue;
                // Title matches win ties; recently used windows rank slightly higher.
                int score = Math.Max(t.Score, p.Matched ? p.Score - 10 : 0) + SearchRanker.WindowBias - Math.Min(w.Order, 20);
                ranked.Add((w, score, t.Matched ? t.Indices : null));
            }
            windows = ranked.OrderByDescending(r => r.Item2).Take(max);
        }
        else windows = svc.Match(query, max);

        foreach (var (w, score, indices) in windows)
        {
            var target = w.Handle;
            _scoredRows.Add(new SearchResult
            {
                Title = w.Title,
                Subtitle = w.ProcessName,
                Category = SearchCategory.Windows,
                Icon = svc.CachedIcon(w),
                FallbackLetter = w.ProcessName.Length > 0 ? char.ToUpperInvariant(w.ProcessName[0]).ToString() : "W",
                FallbackColor = Color.FromRgb(0x5E, 0x81, 0xAC),
                KeyboardHint = "Switch",
                Score = score,
                MatchedIndices = indices,
                WindowHandle = target,
                Action = () =>
                {
                    // CloseSearch would hand focus back to the previous window; switch afterwards instead.
                    _prevForegroundHwnd = IntPtr.Zero;
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                    {
                        if (!WindowActivation.BringToFront(target)) ShowActionSuccess("Could not switch to that window");
                    });
                },
            });
        }
    }

    /// <summary>The cached window list arrived after the bar opened: refresh visible results (never
    /// with empty text, so the default bar stays a single rectangle).</summary>
    private void OnWindowListRefreshed()
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                SearchTrace("window list refreshed", ("text", SearchInput.Text));
                if (_islandState == IslandVisualState.Search && _activeTag is null && !string.IsNullOrWhiteSpace(SearchInput.Text))
                    UpdateSearchResults(SearchInput.Text);
            });
        }
        catch (TaskCanceledException) { /* dispatcher shutting down */ }
    }

    /// <summary>The app index finished its first load (worker thread): re-rank what is typed.</summary>
    private void OnAppsLoaded()
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                SearchTrace("app index loaded", ("apps", AppDiscoveryService.Instance.GetApps().Count));
                if (_islandState == IslandVisualState.Search && _activeTag is null && !string.IsNullOrWhiteSpace(SearchInput.Text))
                    UpdateSearchResults(SearchInput.Text);
            });
        }
        catch (TaskCanceledException) { /* dispatcher shutting down */ }
    }

    private static void StartProcess(string file, string? args = null) =>
        Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = true });

    private List<SearchResult> GetSystemCommands(string query)
    {
        var candidates = new (string Title, string Subtitle, bool IsRisky, Action Action)[]
        {
            ("Lock PC", "Lock Windows workstation", false, () => { StartProcess("rundll32.exe", "user32.dll,LockWorkStation"); ShowActionSuccess("✓ Locked PC"); }),
            ("Sleep", "Put computer to sleep", false, () => { StartProcess("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"); ShowActionSuccess("✓ Sleep"); }),
            ("Restart", "Restart computer", true, () => { StartProcess("shutdown.exe", "/r /t 0"); ShowActionSuccess("✓ Restarting..."); }),
            ("Shut Down", "Turn off computer", true, () => { StartProcess("shutdown.exe", "/s /t 0"); ShowActionSuccess("✓ Shutting down..."); }),
            ("Task Manager", "View running processes and performance", false, () => { StartProcess("taskmgr.exe"); ShowActionSuccess("✓ Opened Task Manager"); }),
            ("Settings", "Open Dynamic Bar settings", false, () => { SettingsWindow.ShowSingleton(); ShowActionSuccess("✓ Opened Settings"); }),
            ("Toggle Island Buddy", "Show or hide Dot companion", false, () =>
            {
                bool cur = App.Settings?.Current.FeatureBuddy ?? true;
                App.Settings?.Update(s => s.FeatureBuddy = !cur);
                ShowActionSuccess(!cur ? "✓ Enabled Island Buddy" : "✓ Hidden Island Buddy");
            }),
        };

        var list = new List<SearchResult>();
        foreach (var (title, subtitle, isRisky, action) in candidates)
        {
            var match = string.IsNullOrEmpty(query) ? new NameMatch(MatchTier.Prefix, 0, null) : SearchRanker.MatchName(title, query);
            if (!match.Matched) continue;
            list.Add(new SearchResult
            {
                Title = title,
                Subtitle = subtitle,
                Category = SearchCategory.Commands,
                FallbackLetter = title[0].ToString(),
                FallbackColor = Color.FromRgb(0x8A, 0x5C, 0xF6),
                IsRisky = isRisky,
                KeyboardHint = "Run",
                Score = match.Score + SearchRanker.CommandBias,
                MatchedIndices = match.Indices,
                Action = action
            });
        }
        list.Sort((a, b) => b.Score.CompareTo(a.Score));
        return list;
    }

    private void ExecuteWebSearch(string query)
    {
        ExecuteResult(CreateWebRow(query));
    }

    private void ExecuteResult(SearchResult result)
    {
        if (result.IsRisky && (!_armedRiskyCommand || _armedCommandTitle != result.Title))
        {
            _armedRiskyCommand = true;
            _armedCommandTitle = result.Title;
            UpdateRowSelections();
            return;
        }

        _armedRiskyCommand = false;
        _armedCommandTitle = "";

        try
        {
            result.Action?.Invoke();
            OnBuddySearchFound();
        }
        catch (Exception ex)   // actions start external processes / shell handlers: report, never crash the bar
        {
            Log.Warning("Search result failed to run", ("title", result.Title), ("error", ex.Message));
            ShowActionSuccess("Could not open that");
        }

        CloseSearch();
    }

    // ---------------------------------------------------------------- rendering

    private void RenderResultRows()
    {
        SearchResultsList.Children.Clear();
        if (_searchResults.Count == 0)
        {
            SearchResultsSection.Visibility = Visibility.Collapsed;
            RetargetSearchHeight(SearchBarH);
            return;
        }

        // Headers only when two or more groups are shown.
        bool headers = _searchResults.Select(r => r.Category).Distinct().Count() >= 2;
        double listH = 0;
        int itemIndex = 0;
        foreach (var group in _searchResults.GroupBy(r => r.Category))
        {
            if (headers)
            {
                bool first = itemIndex == 0;
                SearchResultsList.Children.Add(new Border
                {
                    Height = 22,
                    Margin = new Thickness(8, first ? 2 : 6, 8, 2),
                    Child = new TextBlock
                    {
                        Text = GroupTitle(group.Key),
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = (Brush)FindResource("WdbTextMutedBrush"),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextWrapping = TextWrapping.NoWrap,
                    },
                });
                listH += first ? ResultHeaderFirstH : ResultHeaderH;
            }

            foreach (var item in group)
            {
                SearchResultsList.Children.Add(CreateResultRowBorder(item, itemIndex++));
                listH += ResultRowH;
            }
        }

        SearchResultsScroll.Height = listH;
        SearchResultsSection.Visibility = Visibility.Visible;
        double targetIslandH = SearchBarH + ResultsChromeH + listH;
        SearchTrace("rows rendered", ("rows", _searchResults.Count), ("toH", targetIslandH));
        RetargetSearchHeight(targetIslandH);
    }

    private static string GroupTitle(SearchCategory c) => c switch
    {
        SearchCategory.Calculator => "CALCULATOR",
        SearchCategory.Apps => "APPLICATIONS",
        SearchCategory.Commands => "COMMANDS",
        SearchCategory.Windows => "WINDOWS",
        SearchCategory.Files => "FILES",
        SearchCategory.Web => "WEB",
        _ => c.ToString().ToUpperInvariant()
    };

    private Border CreateResultRowBorder(SearchResult item, int index)
    {
        bool isSel = index == _selectedResultIndex;
        var rowBorder = new Border
        {
            Height = 44,
            CornerRadius = new CornerRadius(8),
            Background = isSel ? SearchRowSelectedBrush : Brushes.Transparent,
            Margin = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(10, 0, 12, 0),
            Tag = index,
            Cursor = Cursors.Hand
        };

        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 1. Icon (28px rounded)
        var iconBorder = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true
        };
        if (item.Icon != null)
        {
            iconBorder.Child = new Image
            {
                Source = item.Icon,
                Width = 22,
                Height = 22,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else
        {
            iconBorder.Background = new SolidColorBrush(item.FallbackColor ?? Color.FromRgb(70, 70, 80));
            iconBorder.Child = new TextBlock
            {
                Text = item.FallbackLetter ?? "•",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        Grid.SetColumn(iconBorder, 0);
        grid.Children.Add(iconBorder);

        // 2. Title & subtitle: one line each, ellipsis, never wider than the column.
        var textStack = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        var titleBlock = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        RenderHighlightedText(titleBlock, item.Title, item.MatchedIndices);
        textStack.Children.Add(titleBlock);
        textStack.Children.Add(new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)FindResource("WdbTextMutedBrush"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = item.Subtitle
        });
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        // 3. Right-hand hint (selected row only), fixed max width.
        var hintBlock = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)FindResource("WdbTextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            MaxWidth = 140,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = GetItemHint(item),
            Visibility = isSel ? Visibility.Visible : Visibility.Collapsed
        };
        Grid.SetColumn(hintBlock, 2);
        grid.Children.Add(hintBlock);

        rowBorder.Child = grid;

        rowBorder.MouseEnter += (s, e) =>
        {
            if (s is Border b && b.Tag is int idx)
            {
                _selectedResultIndex = idx;
                _armedRiskyCommand = false;
                _armedCommandTitle = "";
                UpdateRowSelections();
            }
        };

        rowBorder.MouseDown += (s, e) =>
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                e.Handled = true;
                ExecuteResult(item);
            }
        };

        return rowBorder;
    }

    private string GetItemHint(SearchResult item)
    {
        if (item.IsRisky)
            return _armedRiskyCommand && _armedCommandTitle == item.Title ? "Enter again to confirm" : "Enter";
        return item.KeyboardHint ?? "Enter";
    }

    private void RenderHighlightedText(TextBlock tb, string fullText, List<int>? matchedIndices)
    {
        tb.Inlines.Clear();
        var textBrush = (Brush)FindResource("WdbTextBrush");
        if (matchedIndices == null || matchedIndices.Count == 0)
        {
            tb.Inlines.Add(new Run(fullText) { Foreground = textBrush });
            return;
        }

        var matchSet = new HashSet<int>(matchedIndices);
        var accentBrush = (Brush)FindResource("WdbAccentBrush");
        int i = 0;
        while (i < fullText.Length)
        {
            bool isMatch = matchSet.Contains(i);
            int start = i;
            while (i < fullText.Length && matchSet.Contains(i) == isMatch) i++;
            string part = fullText.Substring(start, i - start);
            tb.Inlines.Add(isMatch
                ? new Run(part) { Foreground = accentBrush, FontWeight = FontWeights.Bold }
                : new Run(part) { Foreground = textBrush });
        }
    }

    private void UpdateRowSelections()
    {
        foreach (UIElement child in SearchResultsList.Children)
        {
            if (child is Border b && b.Tag is int idx)
            {
                bool isSel = idx == _selectedResultIndex;
                b.Background = isSel ? SearchRowSelectedBrush : Brushes.Transparent;
                if (b.Child is Grid grid && grid.Children.Count >= 3 && grid.Children[2] is TextBlock hintBlock)
                {
                    hintBlock.Visibility = isSel ? Visibility.Visible : Visibility.Collapsed;
                    if (isSel && idx >= 0 && idx < _searchResults.Count)
                        hintBlock.Text = GetItemHint(_searchResults[idx]);
                }
            }
        }
    }

    private void ScrollSelectedIntoView()
    {
        foreach (UIElement child in SearchResultsList.Children)
        {
            if (child is Border b && b.Tag is int idx && idx == _selectedResultIndex)
            {
                b.BringIntoView();
                break;
            }
        }
    }

    private void ShowActionSuccess(string message)
    {
        _actionSuccessTimer?.Stop();
        CollapsedTitle.Text = message;
        if (_idleMode) { IdleChipText.Text = message; IdleChip.Visibility = Visibility.Visible; }
        _actionSuccessTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        _actionSuccessTimer.Tick -= OnActionSuccessTick;
        _actionSuccessTimer.Tick += OnActionSuccessTick;
        _actionSuccessTimer.Start();
    }

    private void OnActionSuccessTick(object? sender, EventArgs e)
    {
        _actionSuccessTimer?.Stop();
        UpdateCollapsed();
    }
}
