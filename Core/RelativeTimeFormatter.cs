using System;
using System.Globalization;

namespace STool.Core
{
    /// <summary>
    /// 相对时间格式化工具
    /// </summary>
    public static class RelativeTimeFormatter
    {
        public static string Format(DateTime dateTime)
        {
            var now = DateTime.Now;
            var timeSpan = now - dateTime;

            // 未来时间（可能因为时钟问题）
            if (timeSpan.TotalSeconds < 0)
                return "刚刚";

            // 1分钟内
            if (timeSpan.TotalSeconds < 60)
                return "刚刚";

            // 1小时内
            if (timeSpan.TotalMinutes < 60)
            {
                int minutes = (int)timeSpan.TotalMinutes;
                return $"{minutes}分钟前";
            }

            // 今天
            if (dateTime.Date == now.Date)
            {
                return $"今天 {dateTime:HH:mm}";
            }

            // 昨天
            if (dateTime.Date == now.Date.AddDays(-1))
            {
                return $"昨天 {dateTime:HH:mm}";
            }

            // 7天内
            if (timeSpan.TotalDays < 7)
            {
                int days = (int)timeSpan.TotalDays;
                return $"{days}天前";
            }

            // 今年
            if (dateTime.Year == now.Year)
            {
                return dateTime.ToString("M月d日 HH:mm", CultureInfo.GetCultureInfo("zh-CN"));
            }

            // 超过一年
            return dateTime.ToString("yyyy年M月d日", CultureInfo.GetCultureInfo("zh-CN"));
        }

        /// <summary>
        /// 获取完整的时间戳（用于 Tooltip）
        /// </summary>
        public static string GetFullTimestamp(DateTime dateTime)
        {
            return dateTime.ToString("yyyy年M月d日 HH:mm:ss", CultureInfo.GetCultureInfo("zh-CN"));
        }
    }
}
