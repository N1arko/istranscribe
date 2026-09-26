using System.Windows.Markup;
using WpfBinding = System.Windows.Data.Binding;
using WpfBindingMode = System.Windows.Data.BindingMode;

namespace IsTranscribe.App.Strings;

[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new WpfBinding($"[{Key}]")
        {
            Source = LocalizationManager.Instance,
            Mode = WpfBindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}
