using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using AStockWidget.Models;
using AStockWidget.Services;

namespace AStockWidget;

public sealed partial class MainForm : Form
{
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    private readonly AppConfig _config;
    private readonly StockDatabase _stockDatabase;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly MarketCalendar _marketCalendar;
    private readonly TickDbWebSocketClient _webSocket;
    private readonly TickDbRestClient _restClient;
    private readonly List<string> _watchCodes = new();
    private readonly Dictionary<string, StockInfo> _watchStocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TickerQuote> _quotes = new(StringComparer.Ordinal);

    private WebView2 _webView = null!;
    private bool _uiReady;
    private bool _connected;
    private bool _marketOpen;
    private bool _snapshotInFlight;
    private bool _snapshotWarningShown;
    private int _scale = 1;
    private int _searchExtraHeight;
    private System.Windows.Forms.Timer? _marketTimer;
    private System.Windows.Forms.Timer? _snapshotTimer;

    public MainForm()
    {
        _config = AppConfig.Load();
        _stockDatabase = new StockDatabase();
        _settingsStore = new SettingsStore();
        _settings = _settingsStore.Load();
        _marketCalendar = new MarketCalendar();

        _watchCodes.AddRange(_settings.WatchCodes.Distinct(StringComparer.Ordinal));
        foreach (var code in _watchCodes)
        {
            if (_stockDatabase.FindByCode(code) is { } info)
            {
                _watchStocks[code] = info;
            }
            else
            {
                _watchStocks[code] = new StockInfo { Code = code, Name = code, Market = "" };
            }
        }

        _webSocket = new TickDbWebSocketClient(_config.ApiKey, _config.WebSocketUrl);
        _webSocket.SetSymbols(_watchCodes.Select(ToTickSymbol));
        _webSocket.QuoteReceived += OnQuoteReceived;
        _webSocket.ConnectionChanged += OnConnectionChanged;
        _webSocket.ErrorOccurred += OnWebSocketError;

        _restClient = new TickDbRestClient(_config.ApiKey);

        InitializeComponentFromCode();
    }

    private void InitializeComponentFromCode()
    {
        Text = "A股实时行情";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        BackColor = Color.White;
        MinimumSize = new Size(260, 180);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Visible = true
        };
        Controls.Add(_webView);

        Load += MainForm_Load;
        FormClosing += MainForm_FormClosing;
    }

    private async void MainForm_Load(object? sender, EventArgs e)
    {
        ApplyDpiScale();
        RestoreInitialLocationAndSize();

        try
        {
            await _webView.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "无法初始化 WebView2。请确认已安装 WebView2 Runtime（Windows 11 通常自带）。\n\n详细信息：" + ex.Message,
                "A股实时行情",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
            return;
        }

        var core = _webView.CoreWebView2!;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.SetVirtualHostNameToFolderMapping(
            "appassets.local",
            Path.Combine(AppContext.BaseDirectory, "Web"),
            CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnWebMessageReceived;
        core.Navigate("https://appassets.local/index.html");

        _marketOpen = _marketCalendar.IsMarketOpen();

        _marketTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _marketTimer.Tick += (_, _) => UpdateMarketState();
        _marketTimer.Start();

        _snapshotTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _snapshotTimer.Tick += (_, _) => _ = RefreshSnapshotsAsync();
        _snapshotTimer.Start();

        // Fire-and-forget: the WebSocket client manages its own reconnect loop.
        // Only start after the user has configured an API key.
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            _ = _webSocket.StartAsync();
            _ = RefreshSnapshotsAsync();
        }
    }

    private void ApplyDpiScale()
    {
        using var graphics = CreateGraphics();
        _scale = Math.Max(1, (int)Math.Round(graphics.DpiX / 96.0));
    }

    private void RestoreInitialLocationAndSize()
    {
        var size = CalculateDesiredSize();
        Size = size;

        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);

        if (_settings.WindowX is { } x && _settings.WindowY is { } y)
        {
            Location = new Point(x, y);
        }
        else
        {
            // Default: bottom-right corner.
            Location = new Point(
                workArea.Right - size.Width - 24,
                workArea.Bottom - size.Height - 24);
        }

        EnsureOnScreen();
    }

    private Size CalculateDesiredSize()
    {
        var screenHeight = Screen.PrimaryScreen?.WorkingArea.Height ?? 800;
        var width = 360 * _scale;
        var headerHeight = 52 * _scale;
        var searchHeight = 56 * _scale;
        // 72px row + 4px bottom margin, plus stock-list vertical padding below.
        var rowSlotHeight = 76 * _scale;
        var listPadding = 12 * _scale;
        var footerHeight = 34 * _scale;

        // Normal mode: grow with content, but never exceed 70% of the screen.
        // Below that limit all rows are shown without inner scrolling.
        var contentHeight = headerHeight + searchHeight
                          + Math.Max(1, _watchCodes.Count) * rowSlotHeight
                          + listPadding + footerHeight;

        var maxNormalHeight = (int)(screenHeight * 0.7);
        var height = Math.Clamp(contentHeight, 220 * _scale, maxNormalHeight);

        // While the search dropdown is open, extend the window only enough for the
        // dropdown itself to be visible. The dropdown overlays the stock list, so a
        // large watchlist does not need to grow the window at all.
        if (_searchExtraHeight > 0)
        {
            // CSS layout: titlebar 52px + search-results top:52px inside .search-wrap.
            var searchDropdownTop = headerHeight + 52 * _scale;
            var searchRequiredHeight = searchDropdownTop + _searchExtraHeight * _scale + 12 * _scale;
            var searchCapHeight = Math.Min((int)(screenHeight * 0.85), screenHeight - 16);
            height = Math.Max(height, searchRequiredHeight);
            height = Math.Min(height, searchCapHeight);
        }

        return new Size(width, height);
    }

    private void RecalcSize()
    {
        var newSize = CalculateDesiredSize();
        if (Size != newSize)
        {
            Size = newSize;
        }
        EnsureOnScreen();
    }

    private void EnsureOnScreen()
    {
        var screen = Screen.FromPoint(Location).WorkingArea;

        if (Left < screen.Left)
        {
            Left = screen.Left;
        }
        if (Top < screen.Top)
        {
            Top = screen.Top;
        }
        if (Left + Width > screen.Right)
        {
            Left = Math.Max(screen.Left, screen.Right - Width);
        }
        if (Top + Height > screen.Bottom)
        {
            Top = Math.Max(screen.Top, screen.Bottom - Height);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.TryGetWebMessageAsString();
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

            switch (type)
            {
                case "ready":
                    _uiReady = true;
                    SendFullState();
                    break;

                case "search":
                    var query = root.TryGetProperty("query", out var qProp) ? qProp.GetString() ?? "" : "";
                    SendSearchResults(query);
                    break;

                case "searchExpand":
                    _searchExtraHeight = root.TryGetProperty("extraHeight", out var hProp) && hProp.TryGetInt32(out var extra)
                        ? Math.Max(0, extra)
                        : 0;
                    RecalcSize();
                    break;

                case "searchCollapse":
                    if (_searchExtraHeight != 0)
                    {
                        _searchExtraHeight = 0;
                        RecalcSize();
                    }
                    break;

                case "add":
                    var addCode = root.TryGetProperty("code", out var addProp) ? addProp.GetString() ?? "" : "";
                    AddStock(addCode);
                    break;

                case "remove":
                    var removeCode = root.TryGetProperty("code", out var removeProp) ? removeProp.GetString() ?? "" : "";
                    RemoveStock(removeCode);
                    break;

                case "move":
                    BeginDrag();
                    break;

                case "minimize":
                    WindowState = FormWindowState.Minimized;
                    break;

                case "close":
                    Close();
                    break;
            }
        }
        catch
        {
            // Malformed web messages are ignored.
        }
    }

    private void SendSearchResults(string query)
    {
        var items = _stockDatabase.Search(query, 20)
            .Select(s => new
            {
                code = s.Code,
                name = s.Name,
                market = s.Market
            });

        PostToWeb(new { type = "searchResult", items });
    }

    private void AddStock(string rawCode)
    {
        var code = rawCode.Trim().ToUpperInvariant();
        if (code.Length == 0 || _watchCodes.Contains(code))
        {
            return;
        }

        var info = _stockDatabase.FindByCode(code);
        if (info is null)
        {
            PostToWeb(new { type = "notice", message = $"未找到股票代码：{rawCode}" });
            return;
        }

        _watchCodes.Add(code);
        _watchStocks[code] = info;
        _settings.WatchCodes = _watchCodes.ToList();
        _settingsStore.Save(_settings);
        _webSocket.SetSymbols(_watchCodes.Select(ToTickSymbol));
        _ = RefreshSnapshotsAsync();
        RecalcSize();
        SendFullState();
    }

    private void RemoveStock(string rawCode)
    {
        var code = rawCode.Trim().ToUpperInvariant();
        if (!_watchCodes.Remove(code))
        {
            return;
        }

        _watchStocks.Remove(code);
        _settings.WatchCodes = _watchCodes.ToList();
        _settingsStore.Save(_settings);
        _webSocket.SetSymbols(_watchCodes.Select(ToTickSymbol));
        RecalcSize();
        SendFullState();
    }

    private void SendFullState()
    {
        var stocks = _watchCodes.Select(code =>
        {
            _watchStocks.TryGetValue(code, out var info);
            info ??= new StockInfo { Code = code, Name = code };
            _quotes.TryGetValue(code, out var quote);

            return new
            {
                code,
                name = info.Name,
                market = info.Market,
                price = quote?.LastPrice ?? "",
                change = quote?.PriceChange24h ?? "",
                changePercent = quote?.PriceChangePercent24h ?? "",
                high = quote?.High24h ?? "",
                low = quote?.Low24h ?? "",
                volume = quote?.Volume24h ?? "",
                hasQuote = quote is not null
            };
        }).ToList();

        PostToWeb(new
        {
            type = "init",
            stocks,
            marketOpen = _marketOpen,
            connected = _connected,
            apiKeyConfigured = !string.IsNullOrWhiteSpace(_config.ApiKey),
            apiKeyMissing = string.IsNullOrWhiteSpace(_config.ApiKey)
        });
    }

    private void OnQuoteReceived(TickerQuote quote)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<TickerQuote>(OnQuoteReceived), quote);
            return;
        }

        // TickDB may return either "002266.SZ" or bare "002266"; normalize to
        // the bare code used by the local watchlist.
        var code = NormalizeCode(quote.Symbol);
        quote.Symbol = code;

        _quotes[code] = quote;
        if (_watchCodes.Contains(code))
        {
            PostToWeb(new
            {
                type = "quote",
                quote = new
                {
                    symbol = code,
                    price = quote.LastPrice,
                    change = quote.PriceChange24h ?? "",
                    changePercent = quote.PriceChangePercent24h ?? "",
                    high = quote.High24h ?? "",
                    low = quote.Low24h ?? "",
                    volume = quote.Volume24h ?? ""
                }
            });
        }
    }

    private void OnConnectionChanged(bool connected)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<bool>(OnConnectionChanged), connected);
            return;
        }

        _connected = connected;
        PostToWeb(new { type = "connection", connected });
    }

    private void OnWebSocketError(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(OnWebSocketError), message);
            return;
        }

        PostToWeb(new { type = "notice", message = "连接失败：" + message });
    }

    private async Task RefreshSnapshotsAsync()
    {
        if (string.IsNullOrWhiteSpace(_config.ApiKey) || _snapshotInFlight)
        {
            return;
        }

        _snapshotInFlight = true;
        try
        {
            var symbols = _watchCodes.Select(ToTickSymbol).Distinct(StringComparer.Ordinal).ToList();
            if (symbols.Count == 0)
            {
                return;
            }

            var quotes = await _restClient.GetQuotesAsync(symbols);
            ApplyRestQuotes(quotes);
            _snapshotWarningShown = false;
        }
        catch (Exception ex)
        {
            if (!_snapshotWarningShown)
            {
                _snapshotWarningShown = true;
                PostToWeb(new { type = "notice", message = "快照获取失败：" + ex.Message });
            }
        }
        finally
        {
            _snapshotInFlight = false;
        }
    }

    private void ApplyRestQuotes(IReadOnlyList<TickerQuote> quotes)
    {
        foreach (var quote in quotes)
        {
            var code = NormalizeCode(quote.Symbol);
            quote.Symbol = code;

            _quotes[code] = quote;
            if (!_watchCodes.Contains(code))
            {
                continue;
            }

            PostToWeb(new
            {
                type = "quote",
                quote = new
                {
                    symbol = code,
                    price = quote.LastPrice,
                    change = quote.PriceChange24h ?? "",
                    changePercent = quote.PriceChangePercent24h ?? "",
                    high = quote.High24h ?? "",
                    low = quote.Low24h ?? "",
                    volume = quote.Volume24h ?? ""
                }
            });
        }
    }

    private void UpdateMarketState()
    {
        var open = _marketCalendar.IsMarketOpen();
        if (open == _marketOpen)
        {
            return;
        }

        _marketOpen = open;
        PostToWeb(new { type = "marketState", marketOpen = open });
    }

    private void PostToWeb(object payload)
    {
        if (!_uiReady || _webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        }
        catch
        {
            // WebView may be navigating; ignore transient failures.
        }
    }

    private string ToTickSymbol(string code)
    {
        if (_watchStocks.TryGetValue(code, out var info) && !string.IsNullOrEmpty(info.Market))
        {
            return $"{code}.{info.Market}";
        }

        return code;
    }

    private static string NormalizeCode(string symbol)
    {
        var index = symbol.IndexOf('.');
        return index > 0 ? symbol[..index] : symbol;
    }

    private void BeginDrag()
    {
        if (WindowState != FormWindowState.Normal)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        _settings.WindowX = Location.X;
        _settings.WindowY = Location.Y;
        _settingsStore.Save(_settings);
        _marketTimer?.Dispose();
        _snapshotTimer?.Dispose();
        _webSocket.Dispose();
        _restClient.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}