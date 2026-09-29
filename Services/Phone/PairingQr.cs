using System.Windows.Media.Imaging;
using QRCoder;

namespace UsageNotch.Services.Phone;

public static class PairingQr
{
    /// <summary>Dark modules on white with a quiet zone: the contrast phone cameras read most reliably from a screen.</summary>
    public static BitmapImage Render(string text, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule, [9, 17, 31, 255], [255, 255, 255, 255], true);
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = new MemoryStream(png); image.EndInit();
        image.Freeze();
        return image;
    }
}
