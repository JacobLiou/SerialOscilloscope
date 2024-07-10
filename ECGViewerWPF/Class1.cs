using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ECGViewerWPF
{
    internal class Class1
    {
        public static void SetWritableBitmap(int PhotoWidth, int PhotoHeight)
        {
            PropertyInfo dpiXProperty = typeof(SystemParameters).GetProperty("DpiX", BindingFlags.NonPublic | BindingFlags.Static);
            PropertyInfo dpiYProperty = typeof(SystemParameters).GetProperty("Dpi", BindingFlags.NonPublic | BindingFlags.Static);
            int dpiX = (int)dpiXProperty.GetValue(null);
            int dpiY = (int)dpiYProperty.GetValue(null);
            WriteableBitmap WBitmap = new WriteableBitmap(PhotoWidth, PhotoHeight, dpiX, dpiY, PixelFormats.Bgr24, BitmapPalettes.Halftone256);

            //位图区域刷新和失效  

            //Bitmap vs WriteableBitmap
            WBitmap.Lock();
            WBitmap.AddDirtyRect(new Int32Rect(0, 0, PhotoWidth, PhotoHeight));
            WBitmap.Unlock();
        }
    }
}
