using Microsoft.UI.Xaml.Data;
using System;

namespace GameLauncher
{
    public class IntToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is int intValue)
            {
                return $"{intValue}次";
            }
            return "0次";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is string stringValue && int.TryParse(stringValue.Replace("次", ""), out int result))
            {
                return result;
            }
            return 0;
        }
    }
}