using Microsoft.UI.Xaml;
using NetTrans.Kernel;
using Windows.UI;

namespace NetTrans.Converters;

/// <summary>Static x:Bind function-binding helpers, e.g. {x:Bind conv:BindingHelpers.Visible(Item.IsDone)}.</summary>
public static class BindingHelpers
{
    /// <summary>Parses "#RRGGBB" (a leading '#' is optional).</summary>
    public static Color ColorFromHex(string hex)
    {
        hex = hex.TrimStart('#');
        byte r = Convert.ToByte(hex[..2], 16);
        byte g = Convert.ToByte(hex[2..4], 16);
        byte b = Convert.ToByte(hex[4..6], 16);
        return Color.FromArgb(255, r, g, b);
    }

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisibleIfEqual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisibleIfNotNull(object? value) => value is null ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisibleIfNull(object? value) => value is null ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisibleIfAny(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisibleIfNone(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public static bool Equal(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    public static bool Not(bool value) => !value;
    public static bool IsPriority(TaskPriority value, string name) => value.ToString().Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The full text of a nav title: "下载" plus the dimmed detail run.</summary>
    public static string Join(string a, string b) => $"{a} {b}";
}
