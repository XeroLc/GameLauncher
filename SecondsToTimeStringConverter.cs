using Microsoft.UI.Xaml.Data;
using System;

namespace GameLauncher
{
    public class SecondsToTimeStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is long seconds)
            {
                if (seconds < 60)
                {
                    return $"{seconds}秒";
                }
                else if (seconds < 3600)
                {
                    var minutes = seconds / 60;
                    return $"{minutes}分钟";
                }
                else
                {
                    var hours = seconds / 3600;
                    if (hours >= 24)
                    {
                        var days = hours / 24;
                        return $"{days}天";
                    }
                    return $"{hours}小时";
                }
            }
            return "0小时";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is string stringValue)
            {
                if (stringValue.Contains("天"))
                {
                    if (double.TryParse(stringValue.Replace("天", ""), out double days))
                    {
                        return (long)(days * 24 * 3600);
                    }
                }
                else if (stringValue.Contains("小时"))
                {
                    if (double.TryParse(stringValue.Replace("小时", ""), out double hours))
                    {
                        return (long)(hours * 3600);
                    }
                }
                else if (stringValue.Contains("分钟"))
                {
                    if (double.TryParse(stringValue.Replace("分钟", ""), out double minutes))
                    {
                        return (long)(minutes * 60);
                    }
                }
                else if (stringValue.Contains("秒"))
                {
                    if (long.TryParse(stringValue.Replace("秒", ""), out long secs))
                    {
                        return secs;
                    }
                }
            }
            return 0L;
        }
    }
}