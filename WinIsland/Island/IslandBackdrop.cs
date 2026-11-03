using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using WinIsland.Core;
using WinRT;

namespace WinIsland.Island;

/// <summary>
/// 窗口级系统材质（Desktop Acrylic / Mica）。
///
/// 不用内置的 MicaBackdrop / DesktopAcrylicBackdrop 的原因：它们把 SystemBackdropConfiguration
/// 的 IsInputActive 绑到窗口激活状态，而灵动岛窗口是 WS_EX_NOACTIVATE、永远不激活 —— 材质会被
/// 系统按「非激活」降级成纯色（深色主题下就是 #2C2C2C 那种灰）。这里直接使用控制器，自己提供配置
/// 并把 IsInputActive 固定为 true，才能拿到真正的材质外观。
///
/// 明暗主题同样由这份配置决定：材质是系统画的，浅色主题必须显式告诉它，否则浅色壁纸上会糊一层
/// 深色磨砂（那正是「没适配浅色/深色」的样子）。主题变化可以就地改配置，不必重建控制器。
///
/// 玻璃外观额外给 Acrylic 控制器调色（TintColor / TintOpacity / TintLuminosityOpacity）：
/// 默认 Acrylic 是偏灰的磨砂，调成低不透明度、带一点底色的「液态玻璃」才透得出来。
/// 从玻璃切回 Fluent 时控制器必须重建 —— 调色是挂在控制器实例上的，就地复用会把玻璃的 tint 带过去。
/// </summary>
internal sealed class IslandBackdrop : IDisposable
{
    private readonly Window _window;
    private SystemBackdropConfiguration? _configuration;
    private DesktopAcrylicController? _acrylic;
    private MicaController? _mica;

    /// <summary>当前 Acrylic 控制器是按哪种风格调色的（null = 还没建）。</summary>
    private IslandStyleKind? _acrylicStyle;

    public IslandBackdrop(Window window) => _window = window;

    public static bool IsSupported(IslandMaterialKind material)
        => material == IslandMaterialKind.Mica
            ? MicaController.IsSupported()
            : DesktopAcrylicController.IsSupported();

    /// <summary>
    /// 挂上指定材质与明暗主题。系统不支持该材质时返回 false 且保持现状；
    /// 材质没变时只更新主题（主题切换不该重建控制器，那会闪一下）。
    /// </summary>
    public bool TryApply(IslandMaterialKind material, bool light, IslandStyleKind style)
    {
        if (!IsSupported(material)) return false;

        CompositorProvider.EnsureDispatcherQueue();
        _configuration ??= new SystemBackdropConfiguration { IsInputActive = true };
        _configuration.Theme = light ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark;

        var target = _window.As<ICompositionSupportsSystemBackdrop>();
        if (material == IslandMaterialKind.Mica)
        {
            if (_mica != null)
            {
                _acrylicStyle = null;
                return true;
            }

            var controller = new MicaController { Kind = MicaKind.Base };
            controller.AddSystemBackdropTarget(target);
            controller.SetSystemBackdropConfiguration(_configuration);
            Replace(acrylic: null, mica: controller);
            _acrylicStyle = null;
        }
        else
        {
            // 玻璃需要调色，Fluent 用控制器默认外观：两者的 tint 不同，风格一变就重建
            if (_acrylic != null && _acrylicStyle == style)
            {
                // 同一风格下只有明暗可能变（玻璃的 tint 也要跟着换），就地重调色，不重建控制器
                ApplyAcrylicTint(_acrylic, style, light);
                return true;
            }

            if (_acrylic != null) Replace(acrylic: null, mica: null);

            var controller = new DesktopAcrylicController();
            ApplyAcrylicTint(controller, style, light);
            controller.AddSystemBackdropTarget(target);
            controller.SetSystemBackdropConfiguration(_configuration);
            Replace(acrylic: controller, mica: null);
            _acrylicStyle = style;
        }

        return true;
    }

    /// <summary>玻璃的 Acrylic 调色；Fluent 不碰（保持控制器默认，视觉与改动前一致）。</summary>
    private static void ApplyAcrylicTint(DesktopAcrylicController controller, IslandStyleKind style, bool light)
    {
        if (style != IslandStyleKind.AppleGlass) return;

        if (light)
        {
            controller.TintColor = Windows.UI.Color.FromArgb(255, 247, 247, 252);
            controller.TintOpacity = 0.3f;
            controller.FallbackColor = Windows.UI.Color.FromArgb(255, 238, 238, 244);
        }
        else
        {
            controller.TintColor = Windows.UI.Color.FromArgb(255, 18, 18, 24);
            controller.TintOpacity = 0.42f;
            controller.FallbackColor = Windows.UI.Color.FromArgb(255, 26, 26, 32);
        }
    }

    public void Clear() => Replace(acrylic: null, mica: null);

    public void Dispose()
    {
        Clear();
        _configuration = null;
    }

    /// <summary>新材质先挂上、旧材质后释放，切换过程中不会出现「两个都不在」的空档。</summary>
    private void Replace(DesktopAcrylicController? acrylic, MicaController? mica)
    {
        var oldAcrylic = _acrylic;
        var oldMica = _mica;
        _acrylic = acrylic;
        _mica = mica;
        oldAcrylic?.Dispose();
        oldMica?.Dispose();
    }
}