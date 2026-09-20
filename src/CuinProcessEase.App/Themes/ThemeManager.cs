using System.Windows;

namespace CuinProcessEase.App.Themes;

/// <summary>
/// 主题管理：Dark / Light 两套画刷字典键完全一致，运行时整体替换第一个合并字典。
/// 控件样式（Controls.xaml）经 DynamicResource 引用画刷，切换即时生效。
/// </summary>
public static class ThemeManager
{
    private static readonly Uri DarkSource = new("pack://application:,,,/CuinProcessEase.App;component/Themes/Dark.xaml");
    private static readonly Uri LightSource = new("pack://application:,,,/CuinProcessEase.App;component/Themes/Light.xaml");
    private static readonly Uri ControlsSource = new("pack://application:,,,/CuinProcessEase.App;component/Themes/Controls.xaml");

    /// <summary>应用指定主题。</summary>
    /// <remarks>
    /// 布局：字典 0 = 主题画刷，字典 1 = 控件样式。
    /// 控件样式的 Trigger Setter 只能用 StaticResource（WPF 限制），
    /// 因此切换主题时同时重建样式字典——新解析的样式实例注入新画刷，
    /// 元素端以 DynamicResource 引用样式，即时换肤。
    /// </remarks>
    public static void Apply(bool dark)
    {
        System.Collections.ObjectModel.Collection<ResourceDictionary> merged =
            Application.Current.Resources.MergedDictionaries;
        Uri themeSource = dark ? DarkSource : LightSource;

        if (merged.Count > 0 && merged[0].Source == themeSource)
        {
            return;
        }

        if (merged.Count == 0)
        {
            merged.Insert(0, new ResourceDictionary { Source = themeSource });
            merged.Insert(1, new ResourceDictionary { Source = ControlsSource });
            return;
        }

        merged[0] = new ResourceDictionary { Source = themeSource };
        if (merged.Count > 1)
        {
            merged[1] = new ResourceDictionary { Source = ControlsSource };
        }
        else
        {
            merged.Add(new ResourceDictionary { Source = ControlsSource });
        }
    }
}
