using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CCNest.Core.Diagnostics;
using CCNest.Core.Proxy;
using CCNest.Core.Session;
using Wpf.Ui.Controls;

namespace CCNest.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is true;
        return b ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is Visibility.Visible) ^ Invert;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hasValue = value != null && !(value is string s && s.Length == 0);
        return hasValue ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class StringEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var count = value switch
        {
            int i => i,
            ICollection c => c.Count,
            IEnumerable e => e.Cast<object>().Count(),
            _ => 0,
        };
        var invert = parameter as string == "invert";
        return (count > 0) ^ invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Two-way enum ⇄ bool for radio buttons: ConverterParameter is the enum member name.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value != null && parameter is string p && string.Equals(value.ToString(), p, StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && parameter is string p ? Enum.Parse(targetType, p) : Binding.DoNothing;
}

internal enum Tone { Ok, Warn, Error, Info, Neutral, Brand }

internal static class Tones
{
    public static Tone Of(object? value) => value switch
    {
        DiagStatus.Ok => Tone.Ok,
        DiagStatus.Warning => Tone.Warn,
        DiagStatus.Error => Tone.Error,
        DiagStatus.Info => Tone.Info,
        DiagStatus.Unknown => Tone.Neutral,
        SessionState.Protected => Tone.Ok,
        SessionState.Blocked => Tone.Warn,
        SessionState.Degraded => Tone.Error,
        SessionState.Activating or SessionState.Deactivating => Tone.Brand,
        SessionState.Inactive => Tone.Neutral,
        StepStatus.Passed => Tone.Ok,
        StepStatus.Warning => Tone.Warn,
        StepStatus.Failed => Tone.Error,
        StepStatus.Running => Tone.Brand,
        StepStatus.Skipped or StepStatus.Pending => Tone.Neutral,
        ChangeState.Applied or ChangeState.Pending => Tone.Brand,
        ChangeState.Restored => Tone.Ok,
        ChangeState.RestoredPendingReboot => Tone.Warn,
        ChangeState.Conflict => Tone.Warn,
        ChangeState.RestoreFailed => Tone.Error,
        bool b => b ? Tone.Ok : Tone.Neutral,
        _ => Tone.Neutral,
    };
}

public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var soft = parameter as string == "soft";
        var key = Tones.Of(value) switch
        {
            Tone.Ok => soft ? "OkSoftBrush" : "OkBrush",
            Tone.Warn => soft ? "WarnSoftBrush" : "WarnBrush",
            Tone.Error => soft ? "ErrorSoftBrush" : "ErrorBrush",
            Tone.Info => soft ? "NeutralSoftBrush" : "InfoBrush",
            Tone.Brand => soft ? "BrandSoftBrush" : "BrandBrush",
            _ => soft ? "NeutralSoftBrush" : "NeutralBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class StatusToSymbolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        SessionState.Protected => SymbolRegular.ShieldCheckmark24,
        SessionState.Blocked => SymbolRegular.ShieldLock24,
        SessionState.Degraded => SymbolRegular.ShieldDismiss24,
        SessionState.Activating or SessionState.Deactivating => SymbolRegular.ArrowSync24,
        SessionState.Inactive => SymbolRegular.Shield24,
        _ => Tones.Of(value) switch
        {
            Tone.Ok => SymbolRegular.CheckmarkCircle24,
            Tone.Warn => SymbolRegular.Warning24,
            Tone.Error => SymbolRegular.ErrorCircle24,
            Tone.Info => SymbolRegular.Info24,
            Tone.Brand => SymbolRegular.ArrowSync24,
            _ => SymbolRegular.QuestionCircle24,
        },
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when the bound enum equals ConverterParameter (supports "A|B" alternatives).</summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value != null && parameter is string p && p.Split('|').Contains(value.ToString()) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Friendly protocol names (SOCKS5 / HTTP) for ProxyProtocol values.</summary>
public sealed class ProtocolNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ProxyProtocol.Socks5 => "SOCKS5",
        ProxyProtocol.Http => "HTTP",
        _ => value?.ToString() ?? "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
