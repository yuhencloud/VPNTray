using System.Drawing;
using System.Runtime.InteropServices;
using ZXing.Windows.Compatibility;

namespace VPNTray.UI;

/// <summary>Decodes QR codes from an image file or a full-screen capture.</summary>
public static class QrCodeReader
{
    /// <summary>Decodes the QR code in an image file. Returns its text, or null if none found.</summary>
    public static string? DecodeFile(string path)
    {
        using var bitmap = new Bitmap(path);
        return Decode(bitmap);
    }

    /// <summary>Captures the entire virtual screen (all monitors) and decodes any QR code on it.</summary>
    public static string? DecodeScreen()
    {
        int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int h = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        using var bitmap = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bitmap))
            g.CopyFromScreen(x, y, 0, 0, bitmap.Size);
        return Decode(bitmap);
    }

    private static string? Decode(Bitmap bitmap)
    {
        var reader = new BarcodeReader
        {
            AutoRotate = true,
            Options = new ZXing.Common.DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats = new[] { ZXing.BarcodeFormat.QR_CODE },
            },
        };
        return reader.Decode(bitmap)?.Text;
    }

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
