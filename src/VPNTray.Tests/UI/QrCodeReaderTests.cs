using System.Drawing.Imaging;
using VPNTray.Core;
using VPNTray.UI;
using ZXing;
using ZXing.Windows.Compatibility;

namespace VPNTray.Tests.UI;

public class QrCodeReaderTests
{
    private const string OtpUri = "otpauth://totp/example:zhang.san?secret=ABCDEFGHIJKLMNOP2345&issuer=example";

    [Fact]
    public void DecodeFile_RoundTripsOtpAuthUri()
    {
        var writer = new BarcodeWriter
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new ZXing.Common.EncodingOptions { Width = 300, Height = 300, Margin = 2 },
        };

        var path = Path.Combine(Path.GetTempPath(), $"vpntray-qr-{Guid.NewGuid():N}.png");
        using (var bitmap = writer.Write(OtpUri))
            bitmap.Save(path, ImageFormat.Png);

        try
        {
            var decoded = QrCodeReader.DecodeFile(path);
            Assert.Equal(OtpUri, decoded);
            Assert.Equal("ABCDEFGHIJKLMNOP2345", OtpAuthParser.TryExtractSecret(decoded));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DecodeFile_ReturnsNull_WhenNoQrInImage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vpntray-blank-{Guid.NewGuid():N}.png");
        using (var bitmap = new System.Drawing.Bitmap(120, 120))
            bitmap.Save(path, ImageFormat.Png);

        try
        {
            Assert.Null(QrCodeReader.DecodeFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
