using System.Globalization;
using Avalonia.Data.Converters;

namespace Compositor.App.ViewModels;

public static class Converters
{
    public static readonly IValueConverter VisibleToOpacity = new FuncValueConverter<bool, double>(visible => visible ? 1 : 0.25);

    /// <summary>Two-way: true when the bound tool is the parameter; setting true selects it (setting false is ignored).</summary>
    public static readonly IValueConverter ToolIs = new ToolIsConverter();

    private sealed class ToolIsConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is EditorTool tool && parameter is EditorTool wanted && tool == wanted;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true && parameter is EditorTool wanted ? wanted : Avalonia.Data.BindingOperations.DoNothing;
    }
}
