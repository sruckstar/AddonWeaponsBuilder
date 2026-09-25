using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using SkiaSharp;

namespace Awb.App.Controls;

/// <summary>
/// A control that paints itself directly with SkiaSharp. Subclasses snapshot their state
/// into an immutable <see cref="Frame"/> on the UI thread; the frame is rendered on the
/// render thread through Avalonia's Skia API lease.
/// </summary>
public abstract class SkiaControl : Control
{
    /// <summary>Immutable drawing state handed to the render thread.</summary>
    protected abstract class Frame
    {
        public required Rect Bounds { get; init; }
        public required bool Dark { get; init; }
        public abstract void Draw(SKCanvas canvas);
    }

    protected abstract Frame CreateFrame(Rect bounds, bool dark);

    protected bool IsDarkTheme => ActualThemeVariant != ThemeVariant.Light;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        context.Custom(new DrawOperation(CreateFrame(bounds, IsDarkTheme)));
    }

    protected SkiaControl()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    private sealed class DrawOperation(Frame frame) : ICustomDrawOperation
    {
        public Rect Bounds => frame.Bounds;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease is null) return;
            using var api = lease.Lease();
            var canvas = api.SkCanvas;
            int save = canvas.Save();
            try
            {
                frame.Draw(canvas);
            }
            finally
            {
                canvas.RestoreToCount(save);
            }
        }
    }

    // ---- shared palette (gunmetal + brushed gold) ----------------------------

    protected static class Palette
    {
        public static SKColor Gold(bool dark) => dark ? new SKColor(0xd8, 0xb4, 0x5a) : new SKColor(0xa6, 0x80, 0x2c);
        public static SKColor GoldBright(bool dark) => dark ? new SKColor(0xec, 0xcb, 0x74) : new SKColor(0xc3, 0x9a, 0x3a);
        public static SKColor GoldDeep(bool dark) => dark ? new SKColor(0xb8, 0x93, 0x3f) : new SKColor(0x8a, 0x6a, 0x22);
        public static SKColor GoldLine(bool dark) => dark ? new SKColor(0xd8, 0xb4, 0x5a, 92) : new SKColor(0xa6, 0x80, 0x2c, 102);
        public static readonly SKColor SteelTop = new(0x3a, 0x41, 0x50);
        public static readonly SKColor SteelBottom = new(0x1b, 0x1f, 0x27);
        public static readonly SKColor Chamber = new(0x0e, 0x10, 0x16);
        public static readonly SKColor Flash = new(0xff, 0xb8, 0x4a);
    }
}
