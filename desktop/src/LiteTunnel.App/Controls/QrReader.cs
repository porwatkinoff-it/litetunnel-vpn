using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ZXing;
using ZXing.Common;

namespace LiteTunnel.App.Controls;

/// <summary>Finds a QR code in a picture file.</summary>
public static class QrReader
{
    /// <summary>The text of the code; null when the picture has none. Throws when the file is not a picture.</summary>
    public static string? Decode(Stream image)
    {
        using var bitmap = new Bitmap(image);
        var size = bitmap.PixelSize;
        int stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }
        var format = bitmap.Format == PixelFormat.Rgba8888 ? RGBLuminanceSource.BitmapFormat.RGBA32 : RGBLuminanceSource.BitmapFormat.BGRA32;
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE], TryHarder = true, TryInverted = true },
        };
        return reader.Decode(new RGBLuminanceSource(pixels, size.Width, size.Height, format))?.Text;
    }
}
