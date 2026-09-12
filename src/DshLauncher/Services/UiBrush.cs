namespace DshLauncher.Services;

/// <summary>
/// 从 App.xaml 令牌表取画刷（变更集 132「A2 颜色令牌化收尾」起：C# 里不再写颜色字面量）。
/// 静态字段/属性初始化路径也可能调用它，因此对 <see cref="System.Windows.Application.Current"/> 为空做兜底；
/// 令牌缺失时返回透明画刷而不是抛异常，避免把一个配色问题升级成启动崩溃。
/// </summary>
internal static class UiBrush
{
    public static System.Windows.Media.Brush Get(string tokenKey)
    {
        if (System.Windows.Application.Current?.TryFindResource(tokenKey) is System.Windows.Media.Brush brush)
        {
            return brush;
        }

        return System.Windows.Media.Brushes.Transparent;
    }
}
