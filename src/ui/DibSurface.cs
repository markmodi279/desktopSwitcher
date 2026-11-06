using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DesktopSwitcher
{
    /// <summary>
    /// A back buffer GDI+ draws into and GDI copies out of, with no conversion between: a
    /// 32bpp top-down DIB section selected into a memory DC, and a premultiplied GDI+ Bitmap
    /// laid over the very same bits.
    ///
    /// Every surface that moves draws through one. The modern panel had this from the start,
    /// because UpdateLayeredWindow reads a DC. The strip and the classic panel drew into a
    /// plain Bitmap and copied it to the window with Graphics.DrawImage, and that copy was
    /// the expensive part of every frame, not the drawing: measured live at 125%, a classic
    /// panel frame was 10.7 ms of a 15 ms budget, 7.4 of it in DrawImage converting and
    /// pushing pixels to the window DC. BitBlt from this surface is 0.05 ms, and the drawing
    /// itself also got faster - 3.1 ms into a non-premultiplied Bitmap, 1.0 into this one -
    /// because premultiplied is the format GDI+ composites in natively.
    ///
    /// Grown, never shrunk. A height ease climbs a few pixels a frame, so a buffer that had
    /// to match the window exactly would be reallocated on every frame of a slide. Callers
    /// draw and copy only the part they are using.
    /// </summary>
    sealed class DibSurface : IDisposable
    {
        IntPtr _dc, _dib, _oldBitmap, _bits;
        Bitmap _bitmap;
        int _width, _height;

        /// <summary>The memory DC the DIB is selected into: what BitBlt and UpdateLayeredWindow read.</summary>
        public IntPtr Dc { get { return _dc; } }

        /// <summary>GDI+'s view of the same pixels, premultiplied. Draw through Graphics.FromImage.</summary>
        public Bitmap Bitmap { get { return _bitmap; } }

        /// <summary>
        /// Makes the surface at least this big, keeping it if it already is. Throws when GDI
        /// will not give a DIB section, which is a machine out of GDI handles; the callers'
        /// paint paths already log and carry on.
        /// </summary>
        public void Ensure(int width, int height)
        {
            if (_bitmap != null && _width >= width && _height >= height) return;

            // Grow to cover both dimensions, so a wider request does not lose height.
            width = Math.Max(width, _width);
            height = Math.Max(height, _height);
            Release();

            var bi = new Native.BITMAPINFOHEADER();
            bi.biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
            bi.biWidth = width;
            bi.biHeight = -height;     // negative: top-down, so row 0 is the top, as GDI+ expects
            bi.biPlanes = 1;
            bi.biBitCount = 32;

            _dc = Native.CreateCompatibleDC(IntPtr.Zero);
            _dib = Native.CreateDIBSection(_dc, ref bi, 0, out _bits, IntPtr.Zero, 0);
            if (_dc == IntPtr.Zero || _dib == IntPtr.Zero || _bits == IntPtr.Zero)
            {
                Release();
                throw new InvalidOperationException("no DIB section for a back buffer");
            }

            _oldBitmap = Native.SelectObject(_dc, _dib);
            _bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, _bits);
            _width = width;
            _height = height;
        }

        /// <summary>Copies the top-left width x height to (0, 0) of a window's DC.</summary>
        public void CopyTo(IntPtr hdc, int width, int height)
        {
            Native.BitBlt(hdc, 0, 0, width, height, _dc, 0, 0, Native.SRCCOPY);
        }

        void Release()
        {
            // The Bitmap only borrows the bits, so it goes before the DIB that owns them.
            if (_bitmap != null) { _bitmap.Dispose(); _bitmap = null; }
            if (_dc != IntPtr.Zero && _oldBitmap != IntPtr.Zero) Native.SelectObject(_dc, _oldBitmap);
            if (_dib != IntPtr.Zero) Native.DeleteObject(_dib);
            if (_dc != IntPtr.Zero) Native.DeleteDC(_dc);
            _dc = _dib = _oldBitmap = _bits = IntPtr.Zero;
            _width = _height = 0;
        }

        public void Dispose()
        {
            Release();
        }
    }
}
