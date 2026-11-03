using System.Text;
using System.Text.Json;

namespace AiBalance.Balance;

/// <summary>
/// providers.json 的读写。文件是唯一真相：
///   * 第一次启动时如果不存在，就写一份带 DeepSeek / 魔搭 两条预设的种子文件；
///   * 之后插件只读不写（用户加的注释和格式都留着）；
///   * 读到一半坏了就保留上一次成功加载的那份，绝不回写半成品。
/// </summary>
public sealed class ProvidersFile
{
    public const string FileName = "providers.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private List<ProviderModel> _providers = new();

    public ProvidersFile(string pluginDirectory)
    {
        FilePath = Path.Combine(pluginDirectory, FileName);
        EnsureSeed();
        Reload();
    }

    public string FilePath { get; }

    public IReadOnlyList<ProviderModel> Providers => _providers;

    /// <summary>最近一次读取失败的原因（null = 正常）。界面会把它显示在设置页里。</summary>
    public string? LoadError { get; private set; }

    public void Reload()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                LoadError = "providers.json 不见了";
                return;
            }

            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            var model = JsonSerializer.Deserialize<ProvidersFileModel>(json, Options);
            if (model?.Providers == null || model.Providers.Count == 0)
            {
                LoadError = "providers.json 里没有有效的 providers 数组";
                return;
            }

            var valid = model.Providers.Where(p => !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Request.Url)).ToList();
            if (valid.Count == 0)
            {
                LoadError = "providers 里每一项都缺 id 或 request.url";
                return;
            }

            _providers = valid;
            LoadError = null;
        }
        catch (Exception ex)
        {
            LoadError = "读取失败（沿用上一次成功的配置）：" + ex.Message;
        }
    }

    private void EnsureSeed()
    {
        try
        {
            if (File.Exists(FilePath)) return;
            File.WriteAllText(FilePath, SeedJson, new UTF8Encoding(false));
        }
        catch
        {
            // 插件目录不可写（极少见）：不影响已有配置的运行
        }
    }

    private sealed class ProvidersFileModel
    {
        public List<ProviderModel>? Providers { get; set; }
    }

    /// <summary>
    /// 种子配置。两条预设都按官方文档实测过的字段写：
    ///   * DeepSeek：GET /user/balance → is_available + balance_infos[]（字符串金额 + 币种）；
    ///   * 魔搭：GET /openapi/v1/magicubes/balance → data.total_balance / available_balance / frozen_amount，
    ///     frozen_amount 是「预扣」（进行中的任务暂扣），官方只给余额快照，没有消费明细端点。
    /// </summary>
    private const string SeedJson = """
{
  // 想加新的官方供应商：照着复制一段改 url / amountPath 即可，插件重启或点「重新读取」后生效。
  // 密钥在「设置 → 插件 → AI 余额」里填（用 DPAPI 加密存在插件目录），也可以只写 keyEnvVar 走环境变量。
  "providers": [
    {
      "id": "deepseek",
      "name": "DeepSeek",
      "glyph": "\uE8C7",
      "accent": "#4D6BFE",
      "enabled": true,
      "unit": "¥",
      "decimals": 2,
      "warnBelow": 10,
      "keyEnvVar": "DEEPSEEK_API_KEY",
      "request": {
        "url": "https://api.deepseek.com/user/balance",
        "auth": "bearer",
        "timeoutSeconds": 15
      },
      "parse": {
        "amountPath": "balance_infos[0].total_balance",
        "currencyPath": "balance_infos[0].currency",
        "successPath": "is_available",
        "details": [
          { "label": "赠送余额", "path": "balance_infos[0].granted_balance" },
          { "label": "充值余额", "path": "balance_infos[0].topped_up_balance" }
        ]
      }
    },
    {
      "id": "modelscope",
      "name": "魔搭 魔粒",
      "glyph": "\uE99A",
      "accent": "#6C5CE7",
      "enabled": true,
      "unit": "魔粒",
      "decimals": 0,
      "warnBelow": 20,
      "keyEnvVar": "MODELSCOPE_API_KEY",
      "request": {
        "url": "https://modelscope.cn/openapi/v1/magicubes/balance",
        "auth": "bearer",
        "timeoutSeconds": 15
      },
      "parse": {
        "amountPath": "data.total_balance",
        "successPath": "success",
        "details": [
          { "label": "可用", "path": "data.available_balance" },
          { "label": "预扣", "path": "data.frozen_amount" }
        ]
      }
    }
  ]
}
""";
}