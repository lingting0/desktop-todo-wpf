using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DesktopTodo.Config;

namespace DesktopTodo.Themes;

public partial class SkinPicker : UserControl
{
    private sealed record FontOption(string Id, string Label);
    private readonly AppConfig config;
    private readonly Action refresh;
    internal event Action<bool>? ColorDialogOpen;
    internal bool Changed { get; private set; }

    internal SkinPicker(AppConfig config, Action refresh)
    {
        this.config = config;
        this.refresh = refresh;
        InitializeComponent();
        OpacitySlider.Value = config.BackgroundOpacity;
        Choices.ItemsSource = CardTheme.All.Select(entry => new {
            Name = entry.Key, Swatch = entry.Key == "素色画布" ? config.SolidColor : entry.Value.Accent });
        Choices.SelectedValuePath = "Name";
        Choices.SelectedValue = config.Theme;
        FontList.ItemsSource = CardFonts.Installed.Value.Values
            .Select(font => new FontOption(font.Source, CardFonts.DisplayName(font)))
            .OrderBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase)
            .Prepend(new FontOption("", "跟随皮肤")).ToList();
        FontList.SelectedValue = CardFonts.Installed.Value.TryGetValue(config.FontFamilyName ?? "", out var saved)
            ? saved.Source : "";
        UpdateFontCaption();
        Changed = false;
    }

    private void SelectSkin(object sender, SelectionChangedEventArgs args)
    {
        if (Choices.SelectedValue is not string name) return;
        config.Theme = name;
        ColorButton.Visibility = name == "素色画布" ? Visibility.Visible : Visibility.Collapsed;
        UpdateFontCaption();
        refresh();
        Changed = true;
    }

    private void UpdateFontCaption()
    {
        var selected = FontList.SelectedItem as FontOption;
        string label = selected is { Id.Length: > 0 } ? selected.Label
            : CardTheme.All[CardTheme.Canonical(config.Theme)].DefaultFonts.Name;
        FontCaption.Text = $"字体 · {label}";
        FontCaption.ToolTip = selected is { Id.Length: > 0 } ? label : $"跟随皮肤 · {label}";
    }

    private void SelectFont(object sender, SelectionChangedEventArgs args)
    {
        if (!IsLoaded || FontList.SelectedItem is not FontOption option) return;
        config.FontFamilyName = option.Id;
        UpdateFontCaption();
        refresh();
        Changed = true;
    }

    private void ChangeOpacity(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        int value = (int)args.NewValue;
        if (!IsLoaded || config.BackgroundOpacity == value) return;
        config.BackgroundOpacity = value;
        refresh();
        Changed = true;
    }

    private void ChooseColor(object sender, RoutedEventArgs args)
    {
        using var picker = new System.Windows.Forms.ColorDialog { FullOpen = true,
            Color = System.Drawing.ColorTranslator.FromHtml(config.SolidColor) };
        ColorDialogOpen?.Invoke(true);
        try
        {
            if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            config.SolidColor = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
            refresh();
            Changed = true;
            Choices.Items.Refresh();
        }
        finally { ColorDialogOpen?.Invoke(false); }
    }
}
