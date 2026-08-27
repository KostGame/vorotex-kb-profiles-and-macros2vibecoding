using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Vorotex.K15.VisualTestRig;

public static class ManagedBgraBitmap
{
    public static Bitmap Create(byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        var rowBytes = checked(width * 4); var requiredBytes = checked(rowBytes * height);
        if (pixels.Length < requiredBytes) throw new ArgumentException("Managed BGRA buffer is smaller than the requested image.", nameof(pixels));
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb); BitmapData? data = null;
        try
        {
            data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            if (data.Stride == rowBytes) Marshal.Copy(pixels, 0, data.Scan0, requiredBytes);
            else
            {
                for (var row = 0; row < height; row++)
                {
                    Marshal.Copy(pixels, row * rowBytes, IntPtr.Add(data.Scan0, row * data.Stride), rowBytes);
                }
            }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
        finally { if (data is not null) bitmap.UnlockBits(data); }
    }
}
