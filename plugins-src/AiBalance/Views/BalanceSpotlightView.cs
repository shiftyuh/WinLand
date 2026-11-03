using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace AiBalance.Views;

/// <summary>
/// 「超级展开」聚光卡：点岛体后宿主从岛体位置飞入的一张余额大卡片。
///
///   * 必须是**独立于岛视图**的另一棵可视树（每个窗口一棵树），实例由插件缓存复用；
///   * 飞入/飞回、遮罩、圆角、层级全由宿主负责，这里只管最终形态；
///   * 配色跟岛体主题走；刷新按钮回调插件重新取数，卡片自己原地更新。
/// </summary>
public sealed class BalanceSpotlightView : UserControl
{
    /// <summary>插件请求的期望尺寸（宿主会夹到显示器工作区内）。</summary>
    public static readonly Windows.Foundation.Size PreferredSize = new(620, 460);

    private readonly IIslandTheme _theme;
    private readonly Func<Task> _onRefresh;

    // 中性色共享画刷：主题一变只改 Color，所有引用当场跟着变
    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();
    private readonly SolidColorBrush _cardBrush = new();
    private readonly SolidColorBrush _warnBrush = new(Windows.UI.Color.FromArgb(255, 0xEF, 0x6C, 0x5F));

    private readonly StackPanel _list;
    private readonly TextBlock _header;
    private readonly TextBlock _footer;
    private readonly Button _refreshButton;
    private readonly ProgressRing _busy;

    public BalanceSpotlightView(IIslandTheme theme, Func<Task> onRefresh)
    {
        _theme = theme;
        _onRefresh = onRefresh;

        _header = new TextBlock
        {
            Text = "AI 余额",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
        };

        _refreshButton = new Button
        {
            Content = "刷新",
            FontSize = 12,
            Padding = new Thickness(12, 5, 12, 5),
        };
        _refreshButton.Click += async (_, _) => { await _onRefresh(); };

        _busy = new ProgressRing { Width = 16, Height = 16, IsActive = false, Visibility = Visibility.Collapsed };

        var headerRow = new Grid { ColumnSpacing = 10 };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.Children.Add(_header);
        Grid.SetColumn(_busy, 1);
        headerRow.Children.Add(_busy);
        Grid.SetColumn(_refreshButton, 2);
        headerRow.Children.Add(_refreshButton);

        _list = new StackPanel { Spacing = 10, Padding = new Thickness(0, 12, 0, 0) };

        _footer = new TextBlock
        {
            FontSize = 11,
            Foreground = _faintBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var root = new StackPanel
        {
            Padding = new Thickness(4),
            Children = { headerRow, _list, _footer },
        };

        var scroll = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
        };

        Content = scroll;
        RefreshTheme();
    }

    /// <summary>刷新期间转圈并禁用按钮。</summary>
    public void SetBusy(bool busy)
    {
        _busy.IsActive = busy;
        _busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _refreshButton.IsEnabled = !busy;
    }

    public void Apply(IReadOnlyList<ProviderBalance> balances, string footerText)
    {
        _list.Children.Clear();

        if (balances.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "还没有配置任何供应商。打开 providers.json 添加，或在设置页里检查文件是否可读。",
                Foreground = _mutedBrush,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        foreach (var item in balances)
        {
            _list.Children.Add(BuildCard(item));
        }

        _footer.Text = footerText;
    }

    private UIElement BuildCard(ProviderBalance item)
    {
        var snapshot = item.Snapshot;

        var glyph = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.Provider.Glyph) ? "\uE8C7" : item.Provider.Glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 22,
            Foreground = _mutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 34,
        };

        var name = new TextBlock
        {
            Text = item.Provider.Name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var amount = new TextBlock
        {
            Text = snapshot.Ok
                ? snapshot.Amount.ToString("N" + Math.Clamp(item.Provider.Decimals, 0, 6)) + snapshot.Unit
                : "--",
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = snapshot.Ok
                ? (item.Provider.WarnBelow > 0 && (double)snapshot.Amount < item.Provider.WarnBelow ? _warnBrush : _textBrush)
                : _warnBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
        };
        var amountColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) };
        var titleColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };

        var top = new Grid { ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(titleColumn);
        top.ColumnDefinitions.Add(amountColumn);
        top.Children.Add(glyph);
        Grid.SetColumn(name, 1);
        top.Children.Add(name);
        Grid.SetColumn(amount, 2);
        top.Children.Add(amount);

        var lines = new StackPanel { Spacing = 3, Padding = new Thickness(34, 4, 0, 0) };
        if (snapshot.Ok)
        {
            foreach (var detail in snapshot.Details)
            {
                lines.Children.Add(new TextBlock
                {
                    Text = $"{detail.Label}：{detail.Value}",
                    FontSize = 11.5,
                    Foreground = _faintBrush,
                });
            }

            if (item.Usage is { HasAny: true } usage)
            {
                lines.Children.Add(new TextBlock
                {
                    Text = $"今日消耗 {usage.TodayText}{snapshot.Unit}   ·   本周 {usage.WeekText}{snapshot.Unit}",
                    FontSize = 11.5,
                    Foreground = _mutedBrush,
                    Margin = new Thickness(0, 4, 0, 0),
                });

                if (usage.SamplesToday < 2)
                {
                    lines.Children.Add(new TextBlock
                    {
                        Text = "今天刚攒了几条采样，数字会随刷新补齐；宿主没开的那段时间要等下次启动才看得到差额。",
                        FontSize = 10.5,
                        Foreground = _faintBrush,
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
            }
            else
            {
                lines.Children.Add(new TextBlock
                {
                    Text = "本地采样刚开始：再刷一次就能算今日 / 本周消耗。",
                    FontSize = 11,
                    Foreground = _faintBrush,
                    Margin = new Thickness(0, 4, 0, 0),
                });
            }

            if (item.Provider.WarnBelow > 0 && (double)snapshot.Amount < item.Provider.WarnBelow)
            {
                lines.Children.Add(new TextBlock
                {
                    Text = $"低于预警线 {item.Provider.WarnBelow:0.##}",
                    FontSize = 11.5,
                    Foreground = _warnBrush,
                });
            }
        }
        else
        {
            lines.Children.Add(new TextBlock
            {
                Text = snapshot.Error ?? "查询失败",
                FontSize = 11.5,
                Foreground = _warnBrush,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return new Border
        {
            Background = _cardBrush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Child = new StackPanel { Children = { top, lines } },
        };
    }

    /// <summary>主题变化：只改共享画刷的 Color，然后用最近一次数据重铺（文字颜色都引用这些画刷）。</summary>
    public void RefreshTheme()
    {
        _textBrush.Color = Neutral(240);
        _mutedBrush.Color = Neutral(190);
        _faintBrush.Color = Neutral(130);
        _cardBrush.Color = _theme.IsLight
            ? Windows.UI.Color.FromArgb(255, 0xF3, 0xF3, 0xF3)
            : Windows.UI.Color.FromArgb(255, 0x1C, 0x1C, 0x1C);
        _header.Foreground = _textBrush;
        _footer.Foreground = _faintBrush;
    }

    private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);
}