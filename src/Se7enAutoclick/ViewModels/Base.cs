using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;

namespace Se7enAutoclick.ViewModels;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>Convertisseurs utilisés par la fenêtre.</summary>
public static class Converters
{
    public static readonly IValueConverter BoolToVisibility = new Fn(v => v is true ? Visibility.Visible : Visibility.Collapsed);
    public static readonly IValueConverter InverseBoolToVisibility = new Fn(v => v is true ? Visibility.Collapsed : Visibility.Visible);
    public static readonly IValueConverter NotEmptyToVisibility = new Fn(v => v is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed);
    public static readonly IValueConverter Not = new Fn(v => v is not true, v => v is not true);
    public static readonly IValueConverter ZeroToVisibility = new Fn(v => v is 0 ? Visibility.Visible : Visibility.Collapsed);

    /// <summary>Bouton radio lié à une valeur d'énumération : ConverterParameter = nom de la valeur.</summary>
    public static readonly IValueConverter EnumIs = new EnumConverter();

    /// <summary>Visible si l'énumération vaut ConverterParameter (plusieurs valeurs séparées par « | »).</summary>
    public static readonly IValueConverter EnumToVisibility = new EnumVisibility();

    private sealed class Fn(Func<object?, object> to, Func<object?, object>? back = null) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => to(value);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            back is null ? Binding.DoNothing : back(value);
    }

    private sealed class EnumConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value?.ToString() == parameter as string;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
    }

    private sealed class EnumVisibility : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            (parameter as string ?? "").Split('|').Contains(value?.ToString()) ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
