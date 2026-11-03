using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace AiBalance;

/// <summary>
/// 插件设置页（代码搭的，宿主按 <see cref="SettingsPageDescriptor"/> 把它塞进设置导航）。
/// 四块内容：刷新节奏、每家的密钥（DPAPI 加密落盘 + 测试）、providers.json 的位置与重载、立即刷新与状态。
///
/// 注意：设置页是**普通窗口**里的内容，不是岛上的内容，所以这里不碰 IIslandTheme；
/// 也别去 Application.Current.Resources 里找宿主的 SettingsCardStyle —— 插件包只保证拿到
/// WinUI 自带控件，自绘卡片最稳。
/// </summary>
public sealed class BalanceSettingsPage : UserControl
{
    private static readonly int[] Intervals = { 1, 5, 15, 30, 60, 180 };

    private readonly IPluginContext _context;
    private readonly AiBalancePlugin _plugin;
    private readonly TextBlock _status = Hint("");
    private readonly TextBlock _providersStatus = Hint("");
    private readonly TextBlock _historyStatus = Hint("");
    private readonly ComboBox _intervalBox;
    private readonly StackPanel _keyPanel;

    private bool _loading = true;

    public BalanceSettingsPage(IPluginContext context, AiBalancePlugin plugin)
    {
        _context = context;
        _plugin = plugin;

        var title = new TextBlock
        {
            Text = "AI 余额",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
        };

        var subtitle = Hint("各家 AI 服务的账户余额：小岛显示金额，点岛体看明细。密钥只保存在本机（DPAPI 加密）。");

        // ---- 刷新节奏 ----
        _intervalBox = new ComboBox { Width = 170, VerticalAlignment = VerticalAlignment.Center };
        foreach (var minutes in Intervals)
        {
            _intervalBox.Items.Add(minutes switch { 60 => "1 小时", 180 => "3 小时", _ => $"{minutes} 分钟" });
        }
        _intervalBox.SelectionChanged += OnIntervalChanged;

        var intervalCard = Card("刷新间隔", "太频繁容易被接口限流；余额变化本来就慢，5 分钟够用", _intervalBox);

        // ---- 密钥 ----
        _keyPanel = new StackPanel { Spacing = 12 };
        var keyCard = new Border
        {
            Style = CardStyle(),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "API 密钥", FontWeight = FontWeights.SemiBold },
                    Hint("DeepSeek 用 sk- 开头的 API Key；魔搭用「访问令牌」（ms- 开头）。留空表示改用环境变量（providers.json 的 keyEnvVar）。"),
                    _keyPanel,
                },
            },
        };

        // ---- providers.json ----
        _providersStatus = Hint("");

        var reloadButton = new Button { Content = "重新读取" };
        reloadButton.Click += (_, _) =>
        {
            _plugin.ReloadProviders();
            RebuildKeyRows();
            UpdateStatus();
        };

        var openButton = new Button { Content = "打开文件" };
        openButton.Click += (_, _) => _plugin.OpenProvidersFile();

        var pathText = new TextBlock
        {
            Text = _plugin.ProvidersFilePath,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };

        var fileRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fileRow.Children.Add(reloadButton);
        fileRow.Children.Add(openButton);

        var fileCard = new Border
        {
            Style = CardStyle(),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "供应商配置", FontWeight = FontWeights.SemiBold },
                    Hint("providers.json 是唯一真相：加一家新供应商就是往里加一段（url + 取值路径），不用改代码。"),
                    pathText,
                    fileRow,
                    _providersStatus,
                },
            },
        };

        // ---- 立即刷新 + 状态 ----
        var refreshNow = new Button { Content = "立即刷新全部" };
        refreshNow.Click += async (_, _) =>
        {
            refreshNow.IsEnabled = false;
            _status.Text = "正在查询…";
            try
            {
                await _plugin.RefreshNowAsync();
            }
            finally
            {
                refreshNow.IsEnabled = true;
                UpdateStatus();
            }
        };

        _status = Hint("");

        // ---- 本地采样：今日/本周消耗的来源 ----
        _historyStatus = Hint("");

        var clearHistory = new Button { Content = "清空采样" };
        clearHistory.Click += (_, _) =>
        {
            var cleared = _plugin.ClearHistory();
            UpdateStatus();
            if (!cleared) _status.Text = "本来就没有采样";
        };

        var openHistory = new Button { Content = "打开历史文件" };
        openHistory.Click += (_, _) => _plugin.OpenHistoryFile();

        var historyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        historyRow.Children.Add(clearHistory);
        historyRow.Children.Add(openHistory);

        var historyCard = new Border
        {
            Style = CardStyle(),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "本地采样（今日 / 本周消耗）", FontWeight = FontWeights.SemiBold },
                    Hint("余额接口只有当下快照，消耗是插件自己记的：每次查询成功记一条余额，相邻两条的下降量累加到「后一条」的日期。"
                         + "充值/赠送（余额上涨）不算消耗。只留最近 35 天，按本地时区、周一为一周起点。"),
                    _historyStatus,
                    historyRow,
                },
            },
        };

        var actionCard = new Border
        {
            Style = CardStyle(),
            Child = new StackPanel { Spacing = 8, Children = { refreshNow, _status } },
        };

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 12,
                Padding = new Thickness(0, 0, 0, 20),
                Children = { title, subtitle, intervalCard, keyCard, historyCard, fileCard, actionCard },
            },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        RebuildKeyRows();
        UpdateStatus();
        _loading = false;
    }

    private void RebuildKeyRows()
    {
        _keyPanel.Children.Clear();

        foreach (var provider in _plugin.Providers)
        {
            var stored = _plugin.HasStoredKey(provider.Id);
            var envName = provider.KeyEnvVar ?? "";
            var envSet = envName.Length > 0 && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envName));

            var box = new PasswordBox
            {
                PasswordRevealMode = PasswordRevealMode.Peek,
                PlaceholderText = envSet ? $"留空 = 用环境变量 {envName}" : "粘贴密钥（保存后本机加密）",
                Width = 300,
                Password = stored ? "__stored__" : "",
            };

            var status = Hint(stored ? "已保存（本机加密）" : envSet ? $"来自环境变量 {envName}" : "未设置");

            var save = new Button { Content = "保存" };
            save.Click += (_, _) =>
            {
                if (box.Password == "__stored__")
                {
                    status.Text = "已保存（未改动）";
                    return;
                }

                _plugin.SaveKey(provider.Id, box.Password);
                if (string.IsNullOrWhiteSpace(box.Password))
                {
                    status.Text = "已清除";
                }
                else
                {
                    status.Text = "已保存（本机加密）";
                    box.Password = "__stored__";
                }
            };

            var test = new Button { Content = "测试" };
            test.Click += async (_, _) =>
            {
                test.IsEnabled = false;
                status.Text = "测试中…";
                try
                {
                    var snapshot = await _plugin.TestProviderAsync(provider.Id);
                    status.Text = snapshot.Ok ? $"连接正常：{snapshot.Display}" : $"失败：{snapshot.Error}";
                }
                finally
                {
                    test.IsEnabled = true;
                }
            };

            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(new TextBlock { Text = provider.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, Width = 120, VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(Hint(provider.Id));

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(box);
            row.Children.Add(save);
            row.Children.Add(test);
            row.Children.Add(status);

            _keyPanel.Children.Add(new StackPanel { Spacing = 4, Children = { header, row } });
        }

        if (_plugin.Providers.Count == 0)
        {
            _keyPanel.Children.Add(Hint("providers.json 里还没有供应商。"));
        }
    }

    private void UpdateStatus()
    {
        var error = _plugin.ProvidersLoadError;
        _providersStatus.Text = error is null
            ? $"已加载 {_plugin.Providers.Count} 家供应商：{string.Join("、", _plugin.Providers.Select(p => p.Name))}"
            : error;
        _status.Text = _plugin.StatusText;
        _historyStatus.Text = _plugin.HistorySummary;

        var index = Array.IndexOf(Intervals, _plugin.RefreshMinutes);
        if (index >= 0 && _intervalBox.SelectedIndex != index) _intervalBox.SelectedIndex = index;
    }

    private void OnIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _intervalBox.SelectedIndex < 0) return;
        _plugin.SetRefreshMinutes(Intervals[_intervalBox.SelectedIndex]);
        UpdateStatus();
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x7A, 0x7A, 0x7A)),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Style CardStyle()
    {
        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Windows.UI.Color.FromArgb(16, 128, 128, 128))));
        style.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(8)));
        style.Setters.Add(new Setter(Border.PaddingProperty, new Thickness(14, 12, 14, 12)));
        return style;
    }

    private static Border Card(string title, string hint, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeights.SemiBold },
                Hint(hint),
            },
        };

        grid.Children.Add(left);
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);

        return new Border { Style = CardStyle(), Child = grid };
    }
}