using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace DesktopTodo.Themes;

internal sealed record CardFonts(string Name, FontFamily Heading, FontFamily Body)
{
    internal static readonly CardFonts Sans = new("A · 清润黑体", new("Microsoft YaHei UI"), new("DengXian, Microsoft YaHei UI"));
    internal static readonly CardFonts Song = new("B · 书页宋体", new("SimSun, Microsoft YaHei UI"), new("Microsoft YaHei UI"));
    internal static readonly CardFonts Kai = new("C · 手记楷体", new("KaiTi, Microsoft YaHei UI"), new("FangSong, Microsoft YaHei UI"));

    internal static readonly Lazy<Dictionary<string, FontFamily>> Installed = new(() =>
        Fonts.SystemFontFamilies.DistinctBy(f => f.Source, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(f => f.Source, StringComparer.OrdinalIgnoreCase));

    internal static string DisplayName(FontFamily font) =>
        font.FamilyNames.TryGetValue(XmlLanguage.GetLanguage("zh-CN"), out var name) ? name : font.Source;
}

internal sealed record CardTheme(
    string Surface, string Header, string Row, string Field, string Ink, string Muted,
    string Line, string Accent, string OnAccent, CardFonts? Typography = null,
    double Radius = 6, double RowGap = 3, bool Ruled = false)
{
    internal static readonly Dictionary<string, CardTheme> All = new()
    {
        ["松墨工坊"] = new("#F3F6F5", "#E1EEE9", "#FBFDFB", "#FDFFFE",
            "#20342E", "#526A61", "#CCDCD5", "#276753", "#FFFFFF", Radius: 12, RowGap: 6),
        ["雾海玻璃"] = new("#1A3045", "#A616273B", "#73203A50", "#D9182D41",
            "#FAFDFF", "#D5E8F1", "#739EC5D6", "#A6EADA", "#14382F", Radius: 12, RowGap: 5),
        ["杏仁纸笺"] = new("#FFF8E9", "#F6EBD6", "#00FFFFFF", "#FFFCF5",
            "#392B22", "#715E4D", "#CEC0A8", "#91472E", "#FFFFFF", CardFonts.Song, 0, 0, true),
        ["午夜终端"] = new("#14191B", "#202829", "#202728", "#101618",
            "#EAF2EF", "#B1C2BA", "#3C4D49", "#B4E489", "#182313", Radius: 7, RowGap: 3),
        ["山茶花园"] = new("#FBF5F4", "#F1DCDF", "#FFFAF8", "#FFFFFF",
            "#38262F", "#6C5260", "#D9BBC3", "#873B57", "#FFFFFF", CardFonts.Kai, Radius: 9, RowGap: 4),
        ["素色画布"] = Solid("#DCEBE5"),
    };

    internal static string Canonical(string name) => name switch
    {
        "暗黑" or "黑色" or "暗紫" => "午夜终端",
        "深蓝" or "雾海玻璃（半透明）" => "雾海玻璃",
        "墨绿" or "亮白" => "松墨工坊",
        _ => All.ContainsKey(name) ? name : "松墨工坊"
    };

    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);

    internal static CardTheme Solid(string color)
    {
        var background = Parse(color);
        double luminance = .2126 * background.ScR + .7152 * background.ScG + .0722 * background.ScB;
        string ink = luminance > .179 ? "#000000" : "#FFFFFF";
        return new(color, color, color, color, ink, ink, luminance > .179 ? "#38000000" : "#55FFFFFF",
            ink, color, Radius: 12, RowGap: 5);
    }

    internal CardTheme WithOpacity(int opacity)
    {
        double fraction = Math.Clamp(opacity, 25, 100) / 100d;
        string Fade(string value)
        {
            var color = Parse(value);
            color.A = (byte)Math.Round(color.A * fraction);
            return color.ToString();
        }
        return this with { Surface = Fade(Surface), Header = Fade(Header), Row = Fade(Row), Field = Fade(Field) };
    }

    internal CardFonts DefaultFonts => Typography ?? CardFonts.Sans;

    internal void Apply(ResourceDictionary resources, string? fontName)
    {
        void SetBrush(string key, string value)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            resources[key] = brush;
        }

        SetBrush("CardBg", Surface);
        SetBrush("TitleBg", Header);
        SetBrush("RowBg", Row);
        SetBrush("InputBg", Field);
        SetBrush("TextColor", Ink);
        SetBrush("MutedInk", Muted);
        SetBrush("BorderColor", Line);
        SetBrush("Accent", Accent);
        SetBrush("OnAccent", OnAccent);
        if (Surface != Header)
        {
            var baseColor = Parse(Surface);
            var tint = Parse(Header);
            tint.A = baseColor.A;
            var wash = new LinearGradientBrush(tint, baseColor, 85);
            wash.Freeze();
            resources["CardBg"] = wash;
        }
        FontFamily? selected = null;
        if (!string.IsNullOrEmpty(fontName)) CardFonts.Installed.Value.TryGetValue(fontName, out selected);
        resources["HeadingFont"] = selected ?? DefaultFonts.Heading;
        resources["BodyFont"] = selected ?? DefaultFonts.Body;
        resources["RowCorners"] = new CornerRadius(Radius);
        resources["RowMargin"] = new Thickness(0, 0, 0, RowGap);
        resources["RowEdge"] = Ruled ? new Thickness(0, 0, 0, 1) : new Thickness(1);
    }
}
