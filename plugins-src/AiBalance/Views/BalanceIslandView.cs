using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace AiBalance.Views;

/// <summary>
/// 岛上的余额视图。同一棵可视树承载两态：
///   * 小岛：每家一个「图标 + 金额」，够了就一行放下；
///   * 展开：每家一行（名字 + 金额 + 状态），底部一行更新时间。
///
/// 插件集里的两条硬规矩：
///   1. 形态动画**逐帧直接赋值**，绝不用 Storyboard —— 动态程序集里属性路径动画解析不出类型，
///      会在 tick 上抛 COMException，宿主拦不住（见 WeatherIslandView 的注释）；
///   2. 所有元素一开始就在树里，靠 Height=0 + Opacity 藏，不用 Visibility.Collapsed。
/// </summary>
public sealed class BalanceIslandView : UserControl, IMorphView
{
    private const double CompactGlyphSize = 13;
    private const double CompactAmountSize = 15;
    private const double ExpandedAmountSize = 17;
    private const double CompactHeight = 40;
    private const double RowHeight = 30;
    private const double FooterHeight = 18;
    private const double BackAmplitude = 0.45;

    private readonly IIslandTheme _theme;
    private readonly DispatcherQueueTimer? _morphTimer;

    private readonly StackPanel _compactRow;
    private readonly StackPanel _expandedPanel;
    private readonly List<ProviderRow> _rows = new();
    private readonly TextBlock _footer;

    private DateTimeOffset _morphStart;
    private TimeSpan _morphDuration = TimeSpan.FromMilliseconds(333);
    private double _morphFrom;
    private double _morphTarget;
    private double _progress;

    private double _expandedHeight = RowHeight + FooterHeight;

    public BalanceIslandView(IIslandTheme theme)
    {
        _theme = theme;

        _compactRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _expandedPanel = new StackPanel
        {
            Height = 0,
            Opacity = 0,
            Spacing = 4,
            Padding = new Thickness(0, 6, 0, 0),
        };

        _footer = new TextBlock
        {
            FontSize = 10.5,
            Foreground = Hint(140),
            Margin = new Thickness(0, 2, 0, 0),
        };
        _expandedPanel.Children.Add(_footer);

        Content = new StackPanel
        {
            Padding = new Thickness(14, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _compactRow, _expandedPanel },
        };

        _morphTimer = DispatcherQueue?.CreateTimer();
        if (_morphTimer is not null)
        {
            _morphTimer.Interval = TimeSpan.FromMilliseconds(16);
            _morphTimer.IsRepeating = true;
            _morphTimer.Tick += (_, _) => OnMorphTick();
        }

        Unloaded += (_, _) => _morphTimer?.Stop();
    }

    public UIElement View => this;

    /// <summary>展开态需要的高度（宿主按声明的 ExpandedSize 夹取，这里跟着供应商数量走）。</summary>
    public double ExpandedHeight => _expandedHeight;

    /// <summary>小岛只需要一行：第一家的金额 + 其余家的计数，宽度别超 250。</summary>
    public static double CompactWidth => 236;

    /// <summary>把一批查询结果铺到界面上（UI 线程）。</summary>
    public void Apply(IReadOnlyList<ProviderBalance> balances, string footerText)
    {
        EnsureRowCount(balances.Count);

        _compactRow.Children.Clear();
        for (var i = 0; i < balances.Count; i++)
        {
            var item = balances[i];
            var row = _rows[i];
            row.Apply(item, _theme.IsLight, ExpandedAmountSize);

            if (i > 0)
            {
                _compactRow.Children.Add(new TextBlock
                {
                    Text = "·",
                    FontSize = CompactAmountSize - 2,
                    Foreground = Hint(110),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            _compactRow.Children.Add(new TextBlock
            {
                Text = (item.Provider.Glyph ?? "\uE8C7"),
                FontFamily = GlyphFont,
                FontSize = CompactGlyphSize,
                Foreground = item.Snapshot.Ok ? Hint(200) : Hint(150),
                VerticalAlignment = VerticalAlignment.Center,
            });
            _compactRow.Children.Add(new TextBlock
            {
                Text = CompactText(item),
                FontSize = CompactAmountSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = item.Snapshot.Ok ? Hint(240) : Hint(150),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        if (balances.Count == 0)
        {
            _compactRow.Children.Add(new TextBlock
            {
                Text = "AI 余额 · 未配置",
                FontSize = 12.5,
                Foreground = Hint(150),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        _footer.Text = footerText;
        _footer.Foreground = Hint(140);
    }

    public void AnimateToExpanded(TimeSpan duration) => StartMorph(1, duration);

    public void AnimateToCompact(TimeSpan duration) => StartMorph(0, duration);

    /// <summary>岛体换主题：代码搭的视图颜色烘在画刷里，就地按新配色重刷一遍。</summary>
    internal void RefreshTheme(IReadOnlyList<ProviderBalance> balances, string footerText) => Apply(balances, footerText);

    private static FontFamily GlyphFont { get; } = new("Segoe Fluent Icons");

    /// <summary>小岛一行放不下太多字：金额留一位小数，错误折成「!」。</summary>
    private static string CompactText(ProviderBalance item)
    {
        if (!item.Snapshot.Ok) return "!";
        var decimals = item.Provider.Decimals <= 0 ? 0 : 1;
        return item.Snapshot.Amount.ToString("N" + decimals);
    }

    private void EnsureRowCount(int count)
    {
        while (_rows.Count > count)
        {
            _expandedPanel.Children.Remove(_rows[^1].Root);
            _rows.RemoveAt(_rows.Count - 1);
        }

        while (_rows.Count < count)
        {
            var row = new ProviderRow(_theme);
            _rows.Add(row);
            // 明细行插在 footer 之前
            _expandedPanel.Children.Insert(_expandedPanel.Children.Count - 1, row.Root);
        }

        _expandedHeight = count * RowHeight + FooterHeight;
    }

    private void StartMorph(double target, TimeSpan duration)
    {
        _morphFrom = _progress;
        _morphTarget = target;
        _morphDuration = duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1);
        _morphStart = DateTimeOffset.UtcNow;

        if (_morphTimer is null)
        {
            ApplyMorph(target);
            return;
        }

        _morphTimer.Start();
    }

    private void OnMorphTick()
    {
        var elapsed = (DateTimeOffset.UtcNow - _morphStart).TotalMilliseconds;
        var durationMs = Math.Max(1, _morphDuration.TotalMilliseconds);
        var t = Math.Clamp(elapsed / durationMs, 0, 1);

        ApplyMorph(_morphFrom + (_morphTarget - _morphFrom) * BackEaseOut(t));

        if (t >= 1)
        {
            _morphTimer?.Stop();
            ApplyMorph(_morphTarget);
        }
    }

    /// <summary>BackEase(EaseOut, A) = 1 + (A+1)(t-1)³ + A(t-1)²，与宿主那条曲线等价。</summary>
    private static double BackEaseOut(double t)
    {
        var d = t - 1;
        return 1 + (BackAmplitude + 1) * d * d * d + BackAmplitude * d * d;
    }

    private void ApplyMorph(double progress)
    {
        _progress = progress;
        var height = Math.Max(0, _expandedHeight * progress);
        _expandedPanel.Height = height;
        _expandedPanel.Opacity = Math.Clamp(progress, 0, 1);
    }

    private SolidColorBrush Hint(byte alpha) => new(_theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255));

    /// <summary>一家供应商在展开态的一行：图标 + 名字 + 金额 + 状态备注。</summary>
    private sealed class ProviderRow
    {
        private readonly IIslandTheme _theme;

        public StackPanel Root { get; }

        private readonly TextBlock _glyph;
        private readonly TextBlock _name;
        private readonly TextBlock _amount;
        private readonly TextBlock _note;

        public ProviderRow(IIslandTheme theme)
        {
            _theme = theme;
            _glyph = new TextBlock
            {
                FontFamily = GlyphFont,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 20,
            };
            _name = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 92,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            };
            _amount = new TextBlock
            {
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 96,
                TextAlignment = TextAlignment.Right,
            };
            _note = new TextBlock
            {
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            };

            Root = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Height = RowHeight,
                Children = { _glyph, _name, _amount, _note },
            };
        }

        public void Apply(ProviderBalance item, bool lightIsland, double amountSize)
        {
            var snapshot = item.Snapshot;
            _glyph.Text = string.IsNullOrWhiteSpace(item.Provider.Glyph) ? "\uE8C7" : item.Provider.Glyph;
            _name.Text = item.Provider.Name;
            _amount.FontSize = amountSize;
            _amount.Text = snapshot.Ok
                ? snapshot.Amount.ToString("N" + Math.Clamp(item.Provider.Decimals, 0, 4)) + snapshot.Unit
                : "--";
            var usage = item.Usage;
            var lowBalance = snapshot.Ok && item.Provider.WarnBelow > 0 && (double)snapshot.Amount < item.Provider.WarnBelow;
            _note.Text = !snapshot.Ok
                ? (snapshot.Error ?? "还没有数据")
                : lowBalance
                    ? "低于预警线"
                    : usage is { HasAny: true }
                        ? "今日 " + usage.TodayText
                        : "正常";

            var warn = lowBalance;
            _amount.Foreground = Solid(warn ? Windows.UI.Color.FromArgb(255, 0xEF, 0x6C, 0x5F) : Neutral(240));
            _glyph.Foreground = Solid(Neutral(200));
            _name.Foreground = Solid(Neutral(200));
            _note.Foreground = Solid(warn ? Windows.UI.Color.FromArgb(220, 0xEF, 0x6C, 0x5F) : Neutral(140));
        }

        private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
            ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
            : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);

        private static SolidColorBrush Solid(Windows.UI.Color color) => new(color);
    }
}

/// <summary>视图片用的一对「供应商 + 它的最新结果 + 本地采样算出的消耗」。</summary>
public sealed record ProviderBalance(
    Balance.ProviderModel Provider,
    Balance.BalanceSnapshot Snapshot,
    Balance.UsageStats? Usage = null);