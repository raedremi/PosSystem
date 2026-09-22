using System.Runtime.InteropServices;

namespace RasidSync;

internal static class TrayIconFactory
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>ينشئ دائرة ملونة صغيرة توضّح حالة المزامنة.</summary>
    public static Icon Create(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var shadow = new SolidBrush(Color.FromArgb(55, Color.Black));
            using var brush = new SolidBrush(color);
            graphics.FillEllipse(shadow, 5, 6, 23, 23);
            graphics.FillEllipse(brush, 4, 4, 23, 23);
            graphics.DrawEllipse(Pens.White, 5, 5, 21, 21);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
