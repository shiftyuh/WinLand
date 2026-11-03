using System.Text.Json;

namespace AiBalance.Balance;

/// <summary>
/// 一家供应商的声明（providers.json 里的一项）。字段设计成"描述怎么发请求、怎么从 JSON 里取数"，
/// 所以加一家新供应商通常**不用改代码**，只在 providers.json 里加一段就行。
/// </summary>
public sealed class ProviderModel
{
    /// <summary>插件内唯一 id，也是密钥的键（改 id 等于换了一把密钥）。</summary>
    public string Id { get; set; } = "";

    /// <summary>界面上显示的名字。</summary>
    public string Name { get; set; } = "";

    /// <summary>小岛/卡片上的图标：Segoe Fluent Icons 字形，例如 "\uE8C7"。</summary>
    public string? Glyph { get; set; }

    /// <summary>强调色（#RRGGBB），不填用中性色。</summary>
    public string? Accent { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>金额单位（没有 currencyPath 时用它，例如「魔粒」）。</summary>
    public string Unit { get; set; } = "";

    /// <summary>小数位。</summary>
    public int Decimals { get; set; } = 2;

    /// <summary>低于这个值就在小岛上标红（0 = 不预警）。</summary>
    public double WarnBelow { get; set; }

    /// <summary>可选：密钥也可以从环境变量读（设置页里存过密钥时以设置页为准）。</summary>
    public string? KeyEnvVar { get; set; }

    public RequestModel Request { get; set; } = new();

    public ParseModel Parse { get; set; } = new();
}

/// <summary>怎么发这个请求。</summary>
public sealed class RequestModel
{
    public string Url { get; set; } = "";

    /// <summary>bearer（Authorization: Bearer &lt;key&gt;）/ header（自定义请求头）/ query（拼在查询串里）/ none。</summary>
    public string Auth { get; set; } = "bearer";

    /// <summary>Auth = header 时的请求头名。</summary>
    public string HeaderName { get; set; } = "Authorization";

    /// <summary>Auth = query 时的参数名。</summary>
    public string QueryName { get; set; } = "api_key";

    public int TimeoutSeconds { get; set; } = 15;
}

/// <summary>怎么从返回的 JSON 里取余额。</summary>
public sealed class ParseModel
{
    /// <summary>余额字段路径，支持 <c>a.b[0].c</c>。</summary>
    public string AmountPath { get; set; } = "";

    /// <summary>可选：币种字段路径（CNY/USD…），有就把它当单位显示。</summary>
    public string? CurrencyPath { get; set; }

    /// <summary>可选：成功标志字段，值为 false 时判为账户不可用。</summary>
    public string? SuccessPath { get; set; }

    /// <summary>可选：返回值的缩放系数（单位是「分」这类接口用 0.01）。</summary>
    public double AmountScale { get; set; } = 1;

    /// <summary>大卡片里逐行显示的明细（赠送/充值、可用/预扣……）。</summary>
    public List<DetailModel> Details { get; set; } = new();
}

public sealed class DetailModel
{
    public string Label { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Suffix { get; set; }
}

/// <summary>一次查询的结果（失败也有值，界面上照常显示错误原因）。</summary>
public sealed record BalanceSnapshot
{
    public string ProviderId { get; init; } = "";
    public bool Ok { get; init; }
    public decimal Amount { get; init; }
    public string Unit { get; init; } = "";
    public int Decimals { get; init; } = 2;
    public IReadOnlyList<DetailLine> Details { get; init; } = Array.Empty<DetailLine>();
    public string? Error { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>格式化后的金额（带单位），失败时是占位符。</summary>
    public string Display => Ok ? $"{FormatAmount(Amount, Decimals)}{Unit}" : "--";

    public static string FormatAmount(decimal value, int decimals)
        => value.ToString("N" + Math.Clamp(decimals, 0, 6));
}

public sealed record DetailLine(string Label, string Value);

/// <summary>
/// 极简 JSON 路径取值：<c>data.total_balance</c> / <c>balance_infos[0].total_balance</c>。
/// 只支持对象属性与数组下标，够 providers.json 用，也不引入额外依赖。
/// </summary>
public static class JsonPath
{
    public static bool TryGet(JsonElement root, string path, out JsonElement value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(path)) return false;

        var current = root;
        foreach (var rawSegment in path.Split('.'))
        {
            var segment = rawSegment.Trim();
            if (segment.Length == 0) return false;

            // 段可能带一个或多个 [n]：name[0]、[0]、items[1][2]
            var bracket = segment.IndexOf('[');
            var name = bracket < 0 ? segment : segment[..bracket];
            if (name.Length > 0)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current)) return false;
            }

            while (bracket >= 0)
            {
                var close = segment.IndexOf(']', bracket);
                if (close < 0) return false;
                if (!int.TryParse(segment[(bracket + 1)..close], out var index)) return false;
                if (current.ValueKind != JsonValueKind.Array) return false;
                var items = current;
                if (index < 0 || index >= items.GetArrayLength()) return false;
                current = items[index];
                bracket = close + 1 < segment.Length && segment[close + 1] == '[' ? close + 1 : -1;
            }
        }

        value = current;
        return true;
    }

    /// <summary>把取到的元素变成字符串（数字/字符串/布尔都认）。</summary>
    public static string? ToText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.GetRawText(),
    };
}