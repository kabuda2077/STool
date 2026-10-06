using System;
using System.Drawing;
using System.IO;

namespace STool.Core;

public static class AppIcons
{
    public static Icon LoadTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "STool.ico");
        if (File.Exists(iconPath))
        {
            return new Icon(iconPath);
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
        {
            var extractedIcon = Icon.ExtractAssociatedIcon(processPath);
            if (extractedIcon != null)
            {
                return extractedIcon;
            }
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}
