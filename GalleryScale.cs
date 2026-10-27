using System.Windows;
using System.Windows.Media;

namespace OLEDable
{
    /// <summary>
    /// Smooth gallery resizing. While the window edge is being dragged, the tiles keep their size (resizing
    /// hundreds of pictures on every pixel of a drag is what made them flicker); they simply re-flow into more or
    /// fewer columns as they fit. When you let go, they're sized once to fill the width exactly.
    /// </summary>
    public static class GalleryScale
    {
        /// <summary>Keeps the current tile size for the rest of the drag.</summary>
        public static void Hold(FrameworkElement tiles, ref double heldAvail, double avail)
        {
            if (heldAvail <= 0) heldAvail = avail;
        }

        public static void Release(FrameworkElement tiles, ref double heldAvail)
        {
            heldAvail = 0;
        }
    }
}
