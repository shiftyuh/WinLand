using System.Diagnostics;
using Microsoft.UI.Xaml;
using WinIsland.Core;
using AiBalance.Balance;
using AiBalance.Views;

namespace AiBalance;

/// <summary>
/// 插件入口。必须是 public sealed 且有公共无参构造函数，一个包里只允许一个这样的类型。
///
/// 结构：providers.json 决定「查谁、怎么查」，KeyStore 决定「拿什么密钥」，BalanceClient 负责请求，
/// 视图只负责显示。所以接一家新供应商通常只在 providers.json 里加一段，不动这里。
/// </summary>
public sealed class AiBalancePlugin : IslandPluginBase
{
    private const int DefaultRefreshMinutes = 5;
    private const int MaxRefreshMinutes = 24 * 60;
    private const double CompactWidth = 236;
    private const double CompactHeight = 40;
    private const double RowHeight = 30;

    private ProvidersFile _providers = null!;
    private KeyStore _keys = null!;
    private BalanceClient _client = null!;
    private HistoryStore _history = null!;
    private BalanceIslandView _view = null!;
    private BalanceSpotlightView? _spotlight;
    private IslandLiveContent _content = null!;
    private IDisposable? _timer;

    /// <summary>每家供应商最近一次的结果；没有记录时按「还没查过」显示。</summary>
    private readonly Dictionary<string, BalanceSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取数串行化：定时器、手动刷新、设置页测试撞在一起时排队，不并发轰炸接口。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _lastRefresh;
    private bool _refreshing;
    private bool _stopped;

    public string ProvidersFilePath => _providers.FilePath;

    public string? ProvidersLoadError => _providers.LoadError;

    public IReadOnlyList<ProviderModel> Providers => _providers.Providers;

    public int RefreshMinutes => Math.Clamp(Settings.Get("refreshMinutes", DefaultRefreshMinutes), 1, MaxRefreshMinutes);

    public string StatusText { get; private set; } = "还没有查询";

    public bool HasStoredKey(string providerId) => _keys.Get(providerId) != null;

    protected override Task OnInitializeAsync()
    {
        _stopped = false;

        _providers = new ProvidersFile(PluginDirectory);
        _keys = new KeyStore(PluginDirectory);
        _client = new BalanceClient();
        Context.Register(_client);
        _history = new HistoryStore(PluginDirectory);

        _view = new BalanceIslandView(Theme);
        _view.Apply(Rows(), FooterText());

        Context.Theme.Changed += OnThemeChanged;
        Context.Register(new ActionDisposable(() => Context.Theme.Changed -= OnThemeChanged));

        _content = BuildContent();
        SetContent(_content);

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            Manifest.Id, Manifest.Name, Manifest.IconGlyph ?? "\uE8C7",
            () => new BalanceSettingsPage(Context, this), order: 120));

        Context.Register(Context.OnSettingsChanged("priority", () =>
        {
            _content = BuildContent();
            UpdateContent(_content);
        }));

        ApplyRefreshInterval();

        Log.Info($"AI 余额插件已启动：{_providers.Providers.Count} 家供应商，每 {RefreshMinutes} 分钟刷新"
                 + (_providers.LoadError is { } error ? $"（配置有误：{error}）" : ""));
        _ = RefreshAsync("首次加载");
        return Task.CompletedTask;
    }

    protected override Task OnShutdownAsync()
    {
        _stopped = true;
        _timer?.Dispose();
        _timer = null;
        _history?.Save();
        SetContent(null);
        Log.Info("AI 余额插件已停用");
        return Task.CompletedTask;
    }

    // ---- 设置页与聚光卡会用到的操作 ----

    /// <summary>立即查一次全部启用的供应商。</summary>
    public async Task RefreshNowAsync() => await RefreshAsync("手动刷新");

    /// <summary>重新读 providers.json（设置页的「重新读取」）。</summary>
    public void ReloadProviders()
    {
        _providers.Reload();
        _content = BuildContent();
        UpdateContent(_content);
        _view.Apply(Rows(), FooterText());
        _spotlight?.Apply(Rows(), FooterText());
        ApplyRefreshInterval();
        _ = RefreshAsync("配置重载");
    }

    public void SaveKey(string providerId, string? key) => _keys.Set(providerId, key);

    /// <summary>本地采样文件的位置（设置页显示用）。</summary>
    public string HistoryFilePath => _history.FilePath;

    /// <summary>本地采样概况：「2 家 / 137 条 / 最早 11-01」。</summary>
    public string HistorySummary
    {
        get
        {
            var summary = _history.Summary();
            if (summary.Samples == 0) return "还没有采样（插件每次刷新成功会记一条）";
            return $"{summary.Providers} 家 · {summary.Samples} 条"
                   + (summary.Since is { } since ? $" · 最早 {since:MM-dd HH:mm}" : "");
        }
    }

    /// <summary>清空本地采样（设置页的「清空」）。今日/本周消耗会一起归零。</summary>
    public bool ClearHistory()
    {
        var cleared = _history.Clear();
        ApplyToUi();
        return cleared;
    }

    public void SetRefreshMinutes(int minutes)
    {
        Settings.Set("refreshMinutes", Math.Clamp(minutes, 1, MaxRefreshMinutes));
        ApplyRefreshInterval();
    }

    /// <summary>设置页的「测试」：只查这一家，结果不写进岛的缓存。</summary>
    public async Task<BalanceSnapshot> TestProviderAsync(string providerId)
    {
        var provider = _providers.Providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider == null)
        {
            return new BalanceSnapshot { ProviderId = providerId, Error = "providers.json 里没有这家供应商" };
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await _client.FetchAsync(provider, ResolveKey(provider), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void OpenProvidersFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _providers.FilePath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开 providers.json 失败：{ex.Message}");
        }
    }

    public void OpenHistoryFile()
    {
        try
        {
            _history.Save();
            if (File.Exists(_history.FilePath))
            {
                Process.Start(new ProcessStartInfo { FileName = _history.FilePath, UseShellExecute = true });
            }
            else
            {
                // 还没有采样：打开插件目录而不是弹错
                Process.Start(new ProcessStartInfo { FileName = PluginDirectory, UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"打开本地采样文件失败：{ex.Message}");
        }
    }

    // ---- 取数 ----

    private string? ResolveKey(ProviderModel provider)
    {
        var stored = _keys.Get(provider.Id);
        if (!string.IsNullOrWhiteSpace(stored)) return stored;
        if (string.IsNullOrWhiteSpace(provider.KeyEnvVar)) return null;
        var fromEnv = Environment.GetEnvironmentVariable(provider.KeyEnvVar);
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
    }

    private async Task RefreshAsync(string reason)
    {
        if (_stopped) return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopped) return;
            _refreshing = true;
            RunOnUI(() =>
            {
                StatusText = "正在查询…";
                _spotlight?.SetBusy(true);
            });

            var providers = _providers.Providers.Where(p => p.Enabled).ToList();
            if (providers.Count == 0)
            {
                _refreshing = false;
                RunOnUI(ApplyToUi);
                return;
            }

            var results = await Task.WhenAll(providers.Select(async provider =>
            {
                var snapshot = await _client.FetchAsync(provider, ResolveKey(provider), CancellationToken.None).ConfigureAwait(false);
                return (provider.Id, snapshot);
            })).ConfigureAwait(false);

            foreach (var (id, snapshot) in results)
            {
                _snapshots[id] = snapshot;
                if (!snapshot.Ok) continue;   // 失败不采样：错误值（0/占位）会污染消耗曲线
                var provider = providers.First(p => p.Id == id);
                _history.Record(id, snapshot.Amount, snapshot.UpdatedAt == default ? DateTimeOffset.Now : snapshot.UpdatedAt);
            }

            _history.Save();
            _lastRefresh = DateTimeOffset.Now;
            _refreshing = false;
            RunOnUI(ApplyToUi);

            var failed = results.Count(r => !r.snapshot.Ok);
            Log.Info($"余额刷新完成（{reason}）：{results.Length - failed} 家成功"
                     + (failed > 0 ? $"，{failed} 家失败" : ""));
        }
        catch (Exception ex)
        {
            _refreshing = false;
            Log.Error($"刷新余额时出错（{reason}）", ex);
            RunOnUI(() =>
            {
                StatusText = "刷新失败：" + ex.Message;
                _spotlight?.SetBusy(false);
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- 视图 ----

    private void ApplyToUi()
    {
        var rows = Rows();
        _view.Apply(rows, FooterText());

        // 供应商数量可能变了（改了 providers.json）：展开高度跟着重算，尺寸仍只走 ComputeTotalSize 那条路
        _content = BuildContent();
        UpdateContent(_content);

        _spotlight?.Apply(rows, FooterText());
        _spotlight?.SetBusy(false);

        var ok = rows.Count(r => r.Snapshot.Ok);
        var bad = rows.Count - ok;
        StatusText = rows.Count == 0
            ? "providers.json 里没有启用的供应商"
            : $"{ok} 家正常" + (bad > 0 ? $"，{bad} 家失败" : "") + $" · {FooterText()}";
    }

    private void OnThemeChanged()
    {
        // 主题变化（UI 线程）：按新主题重刷烘死的画刷
        var rows = Rows();
        _view.Apply(rows, FooterText());
        _spotlight?.RefreshTheme();
        _spotlight?.Apply(rows, FooterText());
    }

    private IslandLiveContent BuildContent()
    {
        var count = Math.Max(1, _providers.Providers.Count(p => p.Enabled));
        // 40（小岛一行）+ 每行 30 + 底部更新时间一行；再留一点内边距
        var expandedHeight = CompactHeight + count * RowHeight + 26;
        return new IslandLiveContent
        {
            Priority = Settings.Get("priority", 70),
            OwnerLabel = Manifest.Name,
            OwnerGlyph = Manifest.IconGlyph,
            OwnerAccent = Windows.UI.Color.FromArgb(255, 0x4D, 0x6B, 0xFE),
            MorphView = _view,
            CompactSize = new Windows.Foundation.Size(CompactWidth, CompactHeight),
            ExpandedSize = new Windows.Foundation.Size(340, expandedHeight),
            OnTap = OpenSpotlight,
        };
    }

    /// <summary>当前要显示的行：启用的供应商 + 它最近一次结果（没查过就给一个「还没数据」的占位）。</summary>
    private IReadOnlyList<ProviderBalance> Rows()
    {
        var rows = new List<ProviderBalance>();
        foreach (var provider in _providers.Providers.Where(p => p.Enabled))
        {
            if (!_snapshots.TryGetValue(provider.Id, out var snapshot))
            {
                snapshot = new BalanceSnapshot
                {
                    ProviderId = provider.Id,
                    Unit = provider.Unit,
                    Decimals = provider.Decimals,
                    Error = "还没查到数据",
                };
            }

            rows.Add(new ProviderBalance(
                provider,
                snapshot,
                _history.GetUsage(provider.Id, DateTimeOffset.Now, provider.Decimals)));
        }

        return rows;
    }

    private string FooterText()
    {
        if (_lastRefresh is null) return _refreshing ? "正在查询…" : "等待首次查询";
        return $"{_lastRefresh:HH:mm} 更新 · 每 {RefreshMinutes} 分钟";
    }

    private void OpenSpotlight()
    {
        _spotlight ??= new BalanceSpotlightView(Theme, RefreshNowAsync);
        _spotlight.RefreshTheme();
        _spotlight.Apply(Rows(), FooterText());
        _spotlight.SetBusy(_refreshing);

        Context.Island.OpenSpotlight(new IslandSpotlight
        {
            Content = _spotlight,
            Size = BalanceSpotlightView.PreferredSize,
            OnClosed = () =>
            {
                // 关闭后宿主会把岛体内容放回来；下一拍刷新照常写岛视图
            },
        });

        // 打开时顺手拉一次，避免看到几分钟前的旧数字
        _ = RefreshAsync("打开大卡片");
    }

    private void ApplyRefreshInterval()
    {
        _timer?.Dispose();
        var interval = TimeSpan.FromMinutes(RefreshMinutes);
        _timer = Context.CreateTimer(interval, true, () => _ = RefreshAsync("定时刷新"));
    }
}