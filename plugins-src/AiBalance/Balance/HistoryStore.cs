using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AiBalance.Balance;

/// <summary>一段时间的消耗统计（由本地采样推出来，官方接口只给余额快照）。</summary>
public sealed record UsageStats
{
    /// <summary>今天（本地时区）的消耗，≤0 表示今天还没掉过余额。</summary>
    public decimal TodaySpent { get; init; }

    /// <summary>本周（周一 00:00 起）的消耗。</summary>
    public decimal WeekSpent { get; init; }

    /// <summary>这条统计对应的小数位（跟供应商的 decimals 一致）。</summary>
    public int Decimals { get; init; }

    /// <summary>本地最早一条采样的时间（null = 还没采过）。</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>今天的采样条数（太少说明今天刚启动，消耗数还不完整）。</summary>
    public int SamplesToday { get; init; }

    public bool HasAny => Since is not null;

    public string TodayText => TodaySpent > 0 ? "-" + BalanceSnapshot.FormatAmount(TodaySpent, Decimals) : "—";

    public string WeekText => WeekSpent > 0 ? "-" + BalanceSnapshot.FormatAmount(WeekSpent, Decimals) : "—";
}

/// <summary>
/// 本地余额采样：每次查询成功就把「此刻的余额」记一条，靠相邻两条的差值算消耗。
/// 为什么必须自己采样：DeepSeek / 魔搭的余额接口都只有当下快照，没有消费明细端点。
///
/// 规则：
///   * 差值 &gt; 0（余额下降）= 消耗，记到**后一条采样**所在的本地日期；
///   * 差值 &lt; 0（余额上升）= 充值/赠送，不计消耗，也不会抵消当天的消耗（否则「白天用、晚上充」会显示 0）；
///   * 跨午夜的区间归到新的一天，所以「昨天 23:50 → 今天 00:10」这段算今天的消耗；
///   * 只留最近 35 天、每人最多 <see cref="MaxSamples"/> 条，文件不会无限长。
/// 采样只在插件运行、且刷新成功时产生 —— 关掉宿主的那段时间消耗要等到下次启动才被看见（差额仍然算得准）。
/// </summary>
public sealed class HistoryStore
{
    private const int MaxSamples = 6000;
    private const int KeepDays = 35;
    private const string FileName = "history.json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly Dictionary<string, ProviderHistory> _providers = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    public HistoryStore(string pluginDirectory)
    {
        _path = Path.Combine(pluginDirectory, FileName);
        Load();
    }

    public string FilePath => _path;

    /// <summary>记一条余额采样（只应在查询成功后调用）。</summary>
    public void Record(string providerId, decimal amount, DateTimeOffset at)
    {
        if (!_providers.TryGetValue(providerId, out var history))
        {
            history = new ProviderHistory();
            _providers[providerId] = history;
        }

        // 同一分钟内重复查（手动刷新连点）不重复记：每小时最多 12 条、至少隔 60 秒
        if (history.Samples.Count > 0)
        {
            var last = history.Samples[^1];
            if ((at - last.At).TotalSeconds < 60 && last.Amount == amount) return;
        }

        history.Samples.Add(new Sample { At = at, Amount = amount });
        _dirty = true;
        Prune(history, at);
    }

    /// <summary>把内存里的采样写盘（一次刷新周期调一次即可）。</summary>
    public void Save()
    {
        if (!_dirty) return;
        try
        {
            var model = new FileModel
            {
                Version = 1,
                Providers = _providers
                    .Where(pair => pair.Value.Samples.Count > 0)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            };

            var json = JsonSerializer.Serialize(model, WriteOptions);
            // 先写临时文件再替换：写到一半被杀也不会把历史文件弄坏
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, _path, overwrite: true);
            _dirty = false;
        }
        catch
        {
            // 写不进去不影响这次刷新，下次再试
        }
    }

    /// <summary>算某个供应商的今日 / 本周消耗。</summary>
    public UsageStats GetUsage(string providerId, DateTimeOffset now, int decimals)
    {
        if (!_providers.TryGetValue(providerId, out var history) || history.Samples.Count == 0)
        {
            return new UsageStats { Decimals = decimals };
        }

        var samples = history.Samples;
        var today = now.Date;
        var weekStart = StartOfWeek(today);
        decimal todaySpent = 0, weekSpent = 0;
        int samplesToday = 0;

        for (var i = 0; i < samples.Count; i++)
        {
            var current = samples[i];
            if (current.At.Date == today) samplesToday++;
            if (i == 0) continue;

            var previous = samples[i - 1];
            var spent = previous.Amount - current.Amount;
            if (spent <= 0) continue;   // 充值/赠送/持平都不算消耗

            var day = current.At.Date;
            if (day == today) todaySpent += spent;
            if (day >= weekStart) weekSpent += spent;
        }

        return new UsageStats
        {
            TodaySpent = todaySpent,
            WeekSpent = weekSpent,
            Decimals = decimals,
            Since = samples[0].At,
            SamplesToday = samplesToday,
        };
    }

    /// <summary>采样概况（设置页显示）：几家、共几条、最早一条的时间。</summary>
    public (int Providers, int Samples, DateTimeOffset? Since) Summary()
    {
        var withSamples = _providers.Where(pair => pair.Value.Samples.Count > 0).ToList();
        if (withSamples.Count == 0) return (0, 0, null);

        var samples = withSamples.Sum(pair => pair.Value.Samples.Count);
        var since = withSamples.Min(pair => pair.Value.Samples[0].At);
        return (withSamples.Count, samples, since);
    }

    /// <summary>清空所有采样（设置页的「清空本地采样」）。返回是否真的删掉了东西。</summary>
    public bool Clear()
    {
        var had = _providers.Count > 0;
        _providers.Clear();
        if (!had) return false;
        _dirty = true;
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch
        {
            // 删不掉就留一份空文件下次覆盖，不影响功能
        }

        _dirty = false;
        return true;
    }

    private static DateTime StartOfWeek(DateTime today)
    {
        // 周一为一周起点（中文习惯）；Sunday = 0 → 回退 6 天
        var diff = ((int)today.DayOfWeek + 6) % 7;
        return today.AddDays(-diff);
    }

    private static void Prune(ProviderHistory history, DateTimeOffset now)
    {
        var cutoff = now.AddDays(-KeepDays);
        var samples = history.Samples;
        if (samples.Count == 0) return;

        var firstKept = 0;
        while (firstKept < samples.Count - 1 && samples[firstKept].At < cutoff) firstKept++;
        if (firstKept > 0) samples.RemoveRange(0, firstKept);

        // 超出上限时，隔一条丢一条（保留首末），让曲线还能看出趋势
        while (samples.Count > MaxSamples)
        {
            samples.RemoveAt(1);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path, Encoding.UTF8);
            var model = JsonSerializer.Deserialize<FileModel>(json, ReadOptions);
            if (model?.Providers == null) return;

            foreach (var (id, history) in model.Providers)
            {
                if (history?.Samples is not { Count: > 0 }) continue;
                history.Samples = history.Samples
                    .Where(sample => sample.At > DateTimeOffset.MinValue)
                    .OrderBy(sample => sample.At)
                    .ToList();
                _providers[id] = history;
            }
        }
        catch
        {
            // 文件坏了就当没有历史：下次刷新从当下重新开始采
        }
    }

    private sealed class FileModel
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, ProviderHistory>? Providers { get; set; }
    }

    private sealed class ProviderHistory
    {
        public List<Sample> Samples { get; set; } = new();
    }

    private sealed class Sample
    {
        /// <summary>采样时刻（本地时区的带偏移时间）。</summary>
        public DateTimeOffset At { get; set; }

        /// <summary>当时的余额。</summary>
        public decimal Amount { get; set; }
    }
}