using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MediaFoundationPlugin;

public partial class MediaFoundationOutputSettingsView : UserControl
{
    public MediaFoundationOutputSettingsView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
