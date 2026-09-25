using Avalonia;
using Avalonia.Input;
using SkiaSharp;

namespace Awb.App.Controls;

/// <summary>The brand mark: a revolver cylinder seen end-on. Spins a notch on hover.</summary>
public sealed class CylinderMark : SkiaControl
{
    private double _angle;
    private double _target;
    private DateTime _last;

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _target += 60;
        _last = DateTime.UtcNow;
        Animate();
    }

    private void Animate()
    {
        var top = Avalonia.Controls.TopLevel.GetTopLevel(this);
        top?.RequestAnimationFrame(_ =>
        {
            var now = DateTime.UtcNow;
            var dt = (now - _last).TotalSeconds;
            _last = now;
            _angle += (_target - _angle) * Math.Min(1, dt * 10);
            if (Math.Abs(_target - _angle) < 0.2) _angle = _target;
            InvalidateVisual();
            if (_angle != _target) Animate();
        });
    }

    protected override Frame CreateFrame(Rect bounds, bool dark) => new MarkFrame { Bounds = bounds, Dark = dark, Angle = (float)_angle };

    private sealed class MarkFrame : Frame
    {
        public float Angle { get; init; }

        public override void Draw(SKCanvas c)
        {
            float s = (float)Math.Min(Bounds.Width, Bounds.Height) / 32f;
            c.Translate((float)Bounds.Width / 2f, (float)Bounds.Height / 2f);
            c.Scale(s);
            using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, Color = Palette.GoldLine(Dark) };
            c.DrawCircle(0, 0, 14, ring);
            c.RotateDegrees(Angle);
            using var gold = new SKPaint { IsAntialias = true, Color = Palette.Gold(Dark) };
            using var hub = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, Color = Palette.Gold(Dark) };
            c.DrawCircle(0, 0, 6.4f, hub);
            c.DrawCircle(0, 0, 1.7f, gold);
            for (int i = 0; i < 6; i++)
            {
                double a = i * Math.PI / 3;
                c.DrawCircle((float)(9.4 * Math.Cos(a)), (float)(9.4 * Math.Sin(a)), 1.7f, gold);
            }
        }
    }
}
