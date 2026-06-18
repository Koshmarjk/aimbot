// Converters/InverseBoolConverter.cs
using System.Globalization;
using System.Windows.Data;

namespace HachBobAI;

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is bool b && !b;
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => v is bool b && !b;
}
