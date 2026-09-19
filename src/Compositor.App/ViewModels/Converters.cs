using Avalonia.Data.Converters;

namespace Compositor.App.ViewModels;

public static class Converters
{
    public static readonly IValueConverter VisibleToOpacity = new FuncValueConverter<bool, double>(visible => visible ? 1 : 0.25);
}
