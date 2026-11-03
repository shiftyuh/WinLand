using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AiBalance.Balance;

/// <summary>
/// 余额查询客户端：只发 GET，只认 JSON。所有失败都折成一句人话写进 <see cref="BalanceSnapshot.Error"/>，
/// 界面上照常显示，不会因为某家接口挂了就整块空白。
/// </summary>
public sealed class BalanceClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<BalanceSnapshot> FetchAsync(ProviderModel provider, string? apiKey, CancellationToken ct)
    {
        var timeoutSeconds = Math.Clamp(provider.Request.TimeoutSeconds, 5, 60);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var url = BuildUrl(provider.Request, apiKey);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuth(request, provider.Request, apiKey);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Fail(provider, Triage((int)response.StatusCode, body));
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!string.IsNullOrWhiteSpace(provider.Parse.SuccessPath)
                && JsonPath.TryGet(root, provider.Parse.SuccessPath, out var flag)
                && flag.ValueKind == JsonValueKind.False)
            {
                return Fail(provider, "接口报告账户当前不可用");
            }

            if (!JsonPath.TryGet(root, provider.Parse.AmountPath, out var amountElement)
                || !TryDecimal(amountElement, out var rawAmount))
            {
                return Fail(provider, "返回里没有找到余额字段（核对 providers.json 的 amountPath）");
            }

            var scale = provider.Parse.AmountScale;
            if (scale == 0) scale = 1;
            decimal amount = (decimal)scale == 1 ? rawAmount : (decimal)((double)rawAmount * scale);

            var unit = ResolveUnit(provider, root);
            var details = new List<DetailLine>();
            foreach (var detail in provider.Parse.Details)
            {
                if (string.IsNullOrWhiteSpace(detail.Path)) continue;
                if (!JsonPath.TryGet(root, detail.Path, out var valueElement)) continue;
                var text = JsonPath.ToText(valueElement);
                if (string.IsNullOrEmpty(text)) continue;
                if (TryDecimal(valueElement, out var number)) text = BalanceSnapshot.FormatAmount(number, provider.Decimals);
                details.Add(new DetailLine(detail.Label, text + (detail.Suffix ?? "")));
            }

            return new BalanceSnapshot
            {
                ProviderId = provider.Id,
                Ok = true,
                Amount = amount,
                Unit = unit,
                Decimals = provider.Decimals,
                Details = details,
                UpdatedAt = DateTimeOffset.Now,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(provider, "请求超时");
        }
        catch (HttpRequestException ex)
        {
            return Fail(provider, $"网络不通或地址不可达（{ex.Message}）");
        }
        catch (JsonException)
        {
            return Fail(provider, "返回的不是合法 JSON（可能是登录页/网关拦截）");
        }
        catch (Exception ex)
        {
            return Fail(provider, ex.Message);
        }
    }

    public void Dispose() => _http.Dispose();

    private static string BuildUrl(RequestModel request, string? apiKey)
    {
        var url = request.Url.Trim();
        if (!request.Auth.Equals("query", StringComparison.OrdinalIgnoreCase)) return url;
        if (string.IsNullOrEmpty(apiKey)) return url;
        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return url + separator + Uri.EscapeDataString(request.QueryName) + "=" + Uri.EscapeDataString(apiKey);
    }

    private static void ApplyAuth(HttpRequestMessage request, RequestModel model, string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return;
        if (model.Auth.Equals("bearer", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        }
        else if (model.Auth.Equals("header", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation(string.IsNullOrWhiteSpace(model.HeaderName) ? "Authorization" : model.HeaderName, apiKey);
        }
    }

    private static string ResolveUnit(ProviderModel provider, JsonElement root)
    {
        if (!string.IsNullOrWhiteSpace(provider.Parse.CurrencyPath)
            && JsonPath.TryGet(root, provider.Parse.CurrencyPath, out var currencyElement))
        {
            var currency = JsonPath.ToText(currencyElement);
            if (!string.IsNullOrWhiteSpace(currency))
            {
                return currency!.ToUpperInvariant() switch
                {
                    "CNY" or "RMB" => " ¥",
                    "USD" => " $",
                    _ => " " + currency,
                };
            }
        }

        return string.IsNullOrWhiteSpace(provider.Unit) ? "" : " " + provider.Unit;
    }

    private static bool TryDecimal(JsonElement element, out decimal value)
    {
        value = 0;
        var text = JsonPath.ToText(element);
        if (string.IsNullOrWhiteSpace(text)) return false;
        return decimal.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>把 HTTP 状态码 + 返回体翻成人话。各家错误体结构不一，只从里面扒一句能定位问题的文字。</summary>
    private static string Triage(int statusCode, string body)
    {
        var hint = ExtractMessage(body);
        return statusCode switch
        {
            401 => "密钥无效或已过期" + WithHint(hint),
            403 => "这把密钥没有查询余额的权限" + WithHint(hint),
            404 => "接口地址不存在（核对 providers.json 的 url）" + WithHint(hint),
            429 => "请求太频繁，稍后再试" + WithHint(hint),
            >= 500 => $"服务端出错（HTTP {statusCode}）" + WithHint(hint),
            _ => $"请求失败（HTTP {statusCode}）" + WithHint(hint),
        };
    }

    private static string WithHint(string? hint) => string.IsNullOrWhiteSpace(hint) ? "" : $"：{hint}";

    private static string? ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            foreach (var path in new[] { "error.message", "message", "msg", "error" })
            {
                if (!JsonPath.TryGet(root, path, out var element)) continue;
                var text = JsonPath.ToText(element);
                if (!string.IsNullOrWhiteSpace(text)) return Trim(text!, 120);
            }
        }
        catch (JsonException)
        {
            return Trim(body.Trim(), 80);
        }

        return null;
    }

    private static string Trim(string text, int max)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static BalanceSnapshot Fail(ProviderModel provider, string error) => new()
    {
        ProviderId = provider.Id,
        Ok = false,
        Unit = provider.Unit,
        Decimals = provider.Decimals,
        Error = error,
        UpdatedAt = DateTimeOffset.Now,
    };
}