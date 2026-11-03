# AI 余额（ai-balance）

WinIsland / WinLand 灵动岛插件：小岛上显示各家 AI 服务的账户余额，点岛体弹出大卡片看明细，
外加**今日 / 本周消耗**——余额接口只有当下快照，消耗是插件本地采样自己算的。

## 现在支持

| 供应商 | 端点 | 说明 |
|---|---|---|
| **DeepSeek** | `GET https://api.deepseek.com/user/balance` | 官方余额接口：总余额 / 赠送余额 / 充值余额 + 币种（CNY/USD）+ `is_available` |
| **魔搭 ModelScope** | `GET https://modelscope.cn/openapi/v1/magicubes/balance` | 官方「魔粒」余额快照：`total_balance` / `available_balance` / `frozen_amount`（预扣） |

魔搭只有余额快照，没有消费明细端点（`records` / `consumptions` / `usage` / `history` 都是 404），
所以卡片里只展示「总 / 可用 / 预扣」三个数。签到领魔粒是网页行为，token 体系做不了自动签到。

## 今日 / 本周消耗怎么来的

两家官方接口都**不提供消费明细**，所以插件在每次查询成功时把「此刻余额」记一条到本地 `history.json`：

- 相邻两条采样余额下降的量 = 这段时间的消耗，记到**后一条采样**所在的那一天；
- 余额上涨（充值 / 赠送）不算消耗，也不会抵消当天消耗（白天用、晚上充不会显示 0）；
- 跨午夜的区间归到新的一天；本周从**周一 00:00**、按本地时区算；
- 只保留最近 35 天、每家最多 6000 条，文件不会无限膨胀；
- 采样只在插件运行、刷新成功时产生：宿主关掉的那段时间，下次启动会以一次差额补上（算得准，只是没有中间曲线）。

小岛上每家的备注会显示「今日 -1.23」；点开大卡片能看到「今日消耗 / 本周消耗」，设置页可以清空采样。
采样精度跟着刷新间隔走，默认 5 分钟一次。

## 加一家新供应商（不用改代码）

打开插件目录里的 `providers.json`，照着已有一条复制一段，改这几个字段：

| 字段 | 作用 |
|---|---|
| `id` / `name` / `glyph` / `unit` / `decimals` | 标识与显示（`glyph` 是 Segoe Fluent Icons 字形，例如 `"\uE8C7"`） |
| `warnBelow` | 低于这个金额就在小岛上标红（0 = 不预警） |
| `keyEnvVar` | 可选：密钥也可以从环境变量读 |
| `request.url` | 余额接口地址 |
| `request.auth` | `bearer` / `header` / `query` / `none` |
| `parse.amountPath` | 余额在 JSON 里的路径，支持 `a.b[0].c` |
| `parse.currencyPath` | 可选：币种字段（CNY → ¥，USD → $） |
| `parse.successPath` | 可选：值为 false 时判为账户不可用 |
| `parse.details[]` | 大卡片里的明细行（label + path） |

改完点设置页的「重新读取」，或重启宿主。

## 密钥怎么存

设置页里填的密钥用 **Windows DPAPI（当前用户）** 加密后存到插件目录的 `keys.dat`：
明文不进宿主 `settings.json`、不进 `providers.json`、不进日志；文件拷到别的用户/机器解不开。
不想存文件就只写 `keyEnvVar`，插件会去环境变量里取。

> 注意：插件与宿主同权限运行（不是沙箱），装任何插件前都该看清来源。

## 构建

```powershell
cd plugins-src\AiBalance
dotnet build
```

构建后会自动拷到宿主开发目录的 `WinIsland\bin\<Configuration>\net10.0-windows10.0.26100.0\win-x64\plugins\ai-balance\`，
`dotnet run` 起的宿主会直接加载它。要打包成 `.lwp`：`pwsh tools/pack-plugin.ps1 -ProjectDir plugins-src\AiBalance`。

## 已知边界

- 余额是接口快照，刷新间隔到了才更新；失败时保留上一次成功的数字并在大卡片里写明错误。
- 多币种（DeepSeek 同时有 CNY 和 USD）目前只取 `balance_infos[0]`，另一币种不显示。
- 各家接口没有统一的错误体，错误文案是从 `error.message` / `message` / `msg` 里扒一句，认不出来就只看 HTTP 状态码。