using System.Windows;
using System.Windows.Media;

namespace HydroShiftRecorder
{
    /// <summary>
    /// Smooth gallery resizing. While the window edge is being dragged, the tiles keep their layout and the whole
    /// grid is scaled to the new width (cheap, done by the GPU); the real layout happens once, when you let go.
    /// Re-laying out hundreds of tiles on every pixel of a drag is what made them flicker.
    /// </summary>
    public static class GalleryScale
    {
        /// <summary>Holds the layout taken at the start of the drag and scales it to <paramref name="avail"/>.</summary>
        public static void Hold(FrameworkElement tiles, ref double heldAvail, double avail)
        {
            if (heldAvail <= 0)
            {
                heldAvail = avail;
                tiles.Width = tiles.ActualWidth;   // stops the wrap panel re-flowing the tiles meanwhile
                tiles.RenderTransformOrigin = new Point(0, 0);
            }
            double k = avail / heldAvail;
            tiles.RenderTransform = new ScaleTransform(k, k);
        }

        public static void Release(FrameworkElement tiles, ref double heldAvail)
        {
            if (heldAvail <= 0) return;
            heldAvail = 0;
            tiles.Width = double.NaN;
            tiles.RenderTransform = Transform.Identity;
        }
    }
}
