using System.Drawing.Drawing2D;

namespace PadSwap;

/// <summary>Draws a controller in Xbox layout and lights up whatever the game is currently receiving.</summary>
internal sealed class PadView : Control
{
    // Everything is drawn in this logical space and scaled to the control size.
    private const float LogicalWidth = 520;
    private const float LogicalHeight = 250;

    private static readonly Color BodyColor = Color.FromArgb(38, 42, 51);
    private static readonly Color IdleColor = Color.FromArgb(74, 80, 94);
    private static readonly Color PressedColor = Color.FromArgb(46, 160, 67);
    private static readonly Color TextColor = Color.FromArgb(230, 233, 239);

    private PadState _state;

    public PadView()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    /// <summary>Shows the state as the game sees it: buttons after the remap, sticks and triggers as read.</summary>
    public void Show(PadState state)
    {
        if (_state == state) return;
        _state = state;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(BackColor);

        float scale = Math.Min(Width / LogicalWidth, Height / LogicalHeight);
        graphics.TranslateTransform((Width - LogicalWidth * scale) / 2, (Height - LogicalHeight * scale) / 2);
        graphics.ScaleTransform(scale, scale);

        using var bodyBrush = new SolidBrush(BodyColor);
        using (GraphicsPath body = RoundedRectangle(new RectangleF(20, 48, 480, 190), 70))
            graphics.FillPath(bodyBrush, body);

        using var font = new Font("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
        using var smallFont = new Font("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel);

        // Triggers fill with how far they are pressed; shoulders are plain buttons.
        DrawTrigger(graphics, new RectangleF(70, 4, 90, 16), _state.LeftTrigger, "LT", smallFont);
        DrawTrigger(graphics, new RectangleF(360, 4, 90, 16), _state.RightTrigger, "RT", smallFont);
        DrawPill(graphics, new RectangleF(60, 26, 110, 18), PadButton.LeftShoulder, "LB", smallFont);
        DrawPill(graphics, new RectangleF(350, 26, 110, 18), PadButton.RightShoulder, "RB", smallFont);

        DrawStick(graphics, 115, 110, _state.LeftX, _state.LeftY, PadButton.LeftThumb);
        DrawStick(graphics, 330, 180, _state.RightX, _state.RightY, PadButton.RightThumb);

        DrawPill(graphics, new RectangleF(60 + 115, 164, 26, 26), PadButton.DpadUp, "", smallFont);
        DrawPill(graphics, new RectangleF(60 + 115, 164 + 52, 26, 26), PadButton.DpadDown, "", smallFont);
        DrawPill(graphics, new RectangleF(60 + 89, 164 + 26, 26, 26), PadButton.DpadLeft, "", smallFont);
        DrawPill(graphics, new RectangleF(60 + 141, 164 + 26, 26, 26), PadButton.DpadRight, "", smallFont);

        DrawPill(graphics, new RectangleF(205, 100, 38, 18), PadButton.Back, "Back", smallFont);
        DrawPill(graphics, new RectangleF(277, 100, 38, 18), PadButton.Start, "Start", smallFont);
        DrawCircle(graphics, 260, 74, 15, PadButton.Guide, "", font);

        DrawCircle(graphics, 405, 142, 17, PadButton.A, "A", font);
        DrawCircle(graphics, 440, 108, 17, PadButton.B, "B", font);
        DrawCircle(graphics, 370, 108, 17, PadButton.X, "X", font);
        DrawCircle(graphics, 405, 74, 17, PadButton.Y, "Y", font);
    }

    private bool IsPressed(PadButton button) => ((PadButton)_state.Buttons & button) != 0;

    private void DrawCircle(Graphics graphics, float centerX, float centerY, float radius, PadButton button, string text, Font font)
    {
        using var brush = new SolidBrush(IsPressed(button) ? PressedColor : IdleColor);
        var bounds = new RectangleF(centerX - radius, centerY - radius, radius * 2, radius * 2);
        graphics.FillEllipse(brush, bounds);
        DrawCentered(graphics, text, font, bounds);
    }

    private void DrawPill(Graphics graphics, RectangleF bounds, PadButton button, string text, Font font)
    {
        using var brush = new SolidBrush(IsPressed(button) ? PressedColor : IdleColor);
        using GraphicsPath path = RoundedRectangle(bounds, 6);
        graphics.FillPath(brush, path);
        DrawCentered(graphics, text, font, bounds);
    }

    private static void DrawTrigger(Graphics graphics, RectangleF bounds, byte value, string text, Font font)
    {
        using var idleBrush = new SolidBrush(IdleColor);
        using var pressedBrush = new SolidBrush(PressedColor);
        using GraphicsPath path = RoundedRectangle(bounds, 6);
        graphics.FillPath(idleBrush, path);

        GraphicsState saved = graphics.Save();
        graphics.SetClip(path);
        graphics.FillRectangle(pressedBrush, bounds.X, bounds.Y, bounds.Width * value / 255f, bounds.Height);
        graphics.Restore(saved);
        DrawCentered(graphics, text, font, bounds);
    }

    private void DrawStick(Graphics graphics, float centerX, float centerY, short x, short y, PadButton click)
    {
        const float wellRadius = 30;
        const float capRadius = 17;
        const float travel = 12;

        using var wellBrush = new SolidBrush(Color.FromArgb(24, 27, 33));
        graphics.FillEllipse(wellBrush, centerX - wellRadius, centerY - wellRadius, wellRadius * 2, wellRadius * 2);

        // Screen Y grows downwards, stick Y grows upwards.
        float capX = centerX + x / 32768f * travel;
        float capY = centerY - y / 32768f * travel;
        using var capBrush = new SolidBrush(IsPressed(click) ? PressedColor : IdleColor);
        graphics.FillEllipse(capBrush, capX - capRadius, capY - capRadius, capRadius * 2, capRadius * 2);
    }

    private static void DrawCentered(Graphics graphics, string text, Font font, RectangleF bounds)
    {
        if (text.Length == 0) return;
        using var brush = new SolidBrush(TextColor);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(text, font, brush, bounds, format);
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
