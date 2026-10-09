using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;

namespace Beesly;

public sealed record TouchControl(string Name, string State, string Action);
public sealed record TouchButton(int X, int Y, int Width, int Height, string Label, string Action = "",
    string? State = null, string? Icon = null, string? Color = null, int? Page = null);

public sealed class TouchRenderer : IDisposable
{
    public const int ControlsPerPage = 3;
    public static readonly string[] Colors = ["warm-white", "cold-white", "purple", "red", "light-blue"];
    private static readonly string[] ColorLabels = ["Warm\nwhite", "Cold\nwhite", "Purple", "Red", "Light\nblue"];
    private static readonly string[] ColorValues = ["ffe0a3", "e5f2ff", "a020f0", "ff3333", "64beff"];
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 128 });
    private readonly SKTypeface small;
    private readonly SKTypeface title;
    private readonly SKBitmap controlIcon;
    private readonly SKBitmap powerIcon;

    public TouchRenderer()
    {
        var root = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "xp");
        small = SKTypeface.FromFile(System.IO.Path.Combine(root, "fonts", "tahoma.ttf"));
        title = SKTypeface.FromFile(System.IO.Path.Combine(root, "fonts", "trebucbd.ttf"));
        controlIcon = SKBitmap.Decode(System.IO.Path.Combine(root, "control-panel.ico"))
            ?? throw new InvalidOperationException("Cannot load the control-panel icon.");
        powerIcon = SKBitmap.Decode(System.IO.Path.Combine(root, "power.ico"))
            ?? throw new InvalidOperationException("Cannot load the power icon.");
    }

    public static IReadOnlyList<TouchButton> Layout(TouchView view, IReadOnlyList<TouchControl> controls, int page, int pages)
    {
        var buttons = new List<TouchButton>();
        for (var i = 0; i < controls.Count; i++)
        {
            var control = controls[i];
            var width = 276 / controls.Count;
            buttons.Add(view.Type == "multiple"
                ? new(14 + i * width, 48, width - 6, 68, control.Name, control.Action, control.State)
                : new(106, 43, 87, 73, control.Name, control.Action, control.State, view.Type));
        }
        if (pages > 1)
        {
            if (page > 0) buttons.Add(new(14, 30, 70, 16, "< Previous", Page: page - 1));
            if (page + 1 < pages) buttons.Add(new(214, 30, 70, 16, "Next >", Page: page + 1));
        }
        if (view.Swatch.Enable)
            for (var i = 0; i < Colors.Length; i++)
                buttons.Add(new(14 + i * 55, 129, 50, 27, ColorLabels[i], "&color=" + Colors[i], Color: ColorValues[i]));
        else if (view.Type == "aircon")
            for (var i = 0; i < 5; i++)
                buttons.Add(new(14 + i * 55, 129, 50, 27, $"{18 + i * 2} °C", $"&temperature={18 + i * 2}"));
        buttons.Add(new(271, 5, 21, 21, "Close", "Init:Services", Icon: "close"));
        return buttons;
    }

    public byte[]? Get(string key) => cache.TryGetValue(key, out byte[]? png) ? png : null;

    public string Render(string caption, IReadOnlyList<TouchButton> buttons)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { style = 4, caption, buttons }))));
        if (Get(key) is not null) return key;
        using var bitmap = new SKBitmap(298, 168, SKColorType.Rgb888x, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bitmap);
        using var drawing = new WindowDrawing(canvas, small, title);
        drawing.Frame(caption, controlIcon);
        var appliance = buttons.FirstOrDefault(b => b.Icon is "lamp" or "aircon");
        if (appliance is not null)
        {
            drawing.GroupBox("Power", 12, 38, 285, 121);
            drawing.Icon(powerIcon, 24, 56, 20);
            drawing.Text(appliance.State switch { "on" => "Turned on", "off" => "Turned off", _ => "Unavailable" }, 22, 84, 82);
            drawing.Text(appliance.Icon == "lamp" ? "Tap lamp" : "Tap AC", 212, 61, 70);
            drawing.Text("to toggle", 212, 77, 70);
        }
        foreach (var button in buttons) drawing.Button(button);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        cache.Set(key, png.ToArray(), new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(15) });
        return key;
    }

    // Inclusive pixel coordinates preserve the original XP artwork at the phone's native resolution.
    private sealed class WindowDrawing(SKCanvas canvas, SKTypeface small, SKTypeface title) : IDisposable
    {
        private readonly SKPaint paint = new() { IsAntialias = false };

        private void Ink(string color, bool outline = false, int thickness = 1)
        {
            paint.Color = SKColor.Parse(color);
            paint.Style = outline ? SKPaintStyle.Stroke : SKPaintStyle.Fill;
            paint.StrokeWidth = thickness;
        }

        public void Rectangle(string color, int x1, int y1, int x2, int y2, bool outline = false)
        {
            Ink(color, outline);
            if (outline) canvas.DrawRect(x1 + .5f, y1 + .5f, x2 - x1, y2 - y1, paint);
            else canvas.DrawRect(x1, y1, x2 - x1 + 1, y2 - y1 + 1, paint);
        }

        public void Line(string color, int thickness, params float[] points)
        {
            Ink(color, outline: true, thickness);
            using var path = new SKPath();
            path.MoveTo(points[0] + .5f, points[1] + .5f);
            for (var i = 2; i < points.Length; i += 2) path.LineTo(points[i] + .5f, points[i + 1] + .5f);
            canvas.DrawPath(path, paint);
        }

        private void Ellipse(string color, int x1, int y1, int x2, int y2, string? border = null)
        {
            Ink(color);
            canvas.DrawOval(new SKRect(x1, y1, x2 + 1, y2 + 1), paint);
            if (border is null) return;
            Ink(border, outline: true);
            canvas.DrawOval(new SKRect(x1 + .5f, y1 + .5f, x2 + .5f, y2 + .5f), paint);
        }

        private void Rounded(string color, int x, int y, int width, int height, int radius, string border)
        {
            Ink(color);
            canvas.DrawRoundRect(new SKRect(x, y, x + width, y + height), radius, radius, paint);
            Ink(border, outline: true);
            canvas.DrawRoundRect(new SKRect(x + .5f, y + .5f, x + width - .5f, y + height - .5f), radius, radius, paint);
        }

        public void Icon(SKBitmap bitmap, int x, int y, int size)
        {
            using var image = SKImage.FromBitmap(bitmap);
            canvas.DrawImage(image, new SKRect(x, y, x + size, y + size), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        private void Gradient(SKRect bounds, string[] colors, float[]? positions = null, bool horizontal = false)
        {
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(bounds.Left, bounds.Top),
                horizontal ? new SKPoint(bounds.Right, bounds.Top) : new SKPoint(bounds.Left, bounds.Bottom),
                colors.Select(SKColor.Parse).ToArray(), positions, SKShaderTileMode.Clamp);
            Ink("ffffff"); paint.Shader = shader;
            canvas.DrawRect(bounds, paint);
            paint.Shader = null;
        }

        public void GroupBox(string label, int x1, int y1, int x2, int y2)
        {
            Rounded("ece9d8", x1 + 1, y1 + 1, x2 - x1 + 1, y2 - y1 + 1, 3, "ffffff");
            Ink("d0d0bf", outline: true);
            canvas.DrawRoundRect(new SKRect(x1 + .5f, y1 + .5f, x2 + .5f, y2 + .5f), 3, 3, paint);
            Rectangle("ece9d8", x1 + 7, y1 - 5, x1 + 45, y1 + 7);
            Text(label, x1 + 10, y1 - 6, 50, "0046d5");
        }

        public void Frame(string caption, SKBitmap icon)
        {
            canvas.Clear(SKColor.Parse("ece9d8"));
            // Luna uses a sculpted blue caption and a three-pixel frame, not a flat blue rectangle.
            Rounded("0054e3", 0, 0, 298, 168, 6, "00138c");
            Rectangle("ece9d8", 3, 29, 294, 164);
            Line("0831d9", 1, 1, 29, 1, 165, 296, 165, 296, 29);
            Line("166aee", 1, 2, 29, 2, 164);
            Line("001ea0", 1, 295, 29, 295, 165);
            Line("0029b4", 1, 3, 166, 294, 166);
            using var clip = new SKRoundRect(new SKRect(1, 1, 297, 29), 6);
            canvas.Save(); canvas.ClipRoundRect(clip);
            Gradient(new SKRect(1, 1, 297, 29),
                ["0058ee", "3593ff", "288eff", "0369fc", "0262ee", "0057e5", "0050e2", "0037b2"],
                [0, .12f, .20f, .35f, .52f, .73f, .90f, 1]);
            canvas.Restore();
            Line("6aa6ff", 1, 6, 1, 291, 1);
            Line("3c8cff", 1, 4, 2, 293, 2);
            Line("0855dd", 1, 3, 27, 294, 27);
            Line("003cbe", 1, 3, 28, 294, 28);
            Icon(icon, 8, 7, 16);
            Text(caption, 30, 7, 234, "0a329b", heading: true);
            Text(caption, 29, 6, 234, "ffffff", heading: true);
        }

        private void CloseButton(TouchButton b)
        {
            using var clip = new SKRoundRect(new SKRect(b.X + 1, b.Y + 1, b.X + b.Width - 1, b.Y + b.Height - 1), 2);
            canvas.Save(); canvas.ClipRoundRect(clip);
            Gradient(clip.Rect, ["f5baa5", "e98b70", "dd5838", "c53313"], [0, .22f, .6f, 1]);
            // The left highlight is part of XP's glass-like caption button edge.
            Gradient(new SKRect(b.X + 1, b.Y + 2, b.X + 4, b.Y + b.Height - 2), ["ffd1bb", "ee8866", "d94e2a"]);
            canvas.Restore();
            Ink("ffffff", outline: true);
            canvas.DrawRoundRect(new SKRect(b.X + .5f, b.Y + .5f, b.X + b.Width - .5f, b.Y + b.Height - .5f), 3, 3, paint);
            Line("bd3316", 1, b.X + 3, b.Y + 19, b.X + 18, b.Y + 19, b.X + 19, b.Y + 18, b.X + 19, b.Y + 3);
            Line("92301d", 2, b.X + 6, b.Y + 6, b.X + 15, b.Y + 15);
            Line("92301d", 2, b.X + 15, b.Y + 6, b.X + 6, b.Y + 15);
            Line("ffffff", 2, b.X + 5, b.Y + 5, b.X + 14, b.Y + 14);
            Line("ffffff", 2, b.X + 14, b.Y + 5, b.X + 5, b.Y + 14);
        }

        private void ButtonFace(TouchButton b)
        {
            var disabled = b.State == "unavailable";
            var bounds = new SKRect(b.X + .5f, b.Y + .5f, b.X + b.Width - .5f, b.Y + b.Height - .5f);
            using var clip = new SKRoundRect(bounds, 3);
            canvas.Save(); canvas.ClipRoundRect(clip);
            Gradient(bounds, disabled ? ["f5f4ea", "ecebe1"] : ["ffffff", "f8f8f4", "f1f0ea", "e4e1d6"],
                disabled ? null : [0, .25f, .8f, 1]);
            canvas.Restore();
            Ink(disabled ? "c9c7ba" : "003c74", outline: true);
            canvas.DrawRoundRect(bounds, 3, 3, paint);
            Line("ffffff", 1, b.X + 3, b.Y + 1, b.X + b.Width - 4, b.Y + 1);
            Line(disabled ? "e2dfd3" : "d6d0c1", 1, b.X + 2, b.Y + b.Height - 3, b.X + b.Width - 3, b.Y + b.Height - 3);
            Line(disabled ? "e2dfd3" : "c9c2b2", 1, b.X + 3, b.Y + b.Height - 2, b.X + b.Width - 4, b.Y + b.Height - 2);
            Line("e4e0d5", 1, b.X + b.Width - 2, b.Y + 3, b.X + b.Width - 2, b.Y + b.Height - 4);
        }

        public void Button(TouchButton b)
        {
            if (b.Icon == "close") { CloseButton(b); return; }
            ButtonFace(b);
            if (b.Icon is "lamp" or "aircon")
            {
                // Fit the artwork's original design bounds inside the actual button.
                canvas.Save();
                canvas.Translate(b.X, b.Y);
                canvas.Scale(b.Width / 87f, b.Height / 81f);
                canvas.Translate(-106, -43);
                paint.IsAntialias = true;
                if (b.Icon == "lamp") Lamp(b.State == "on");
                else Aircon(b.State == "on");
                canvas.Restore();
                paint.IsAntialias = false;
            }
            else if (b.State is { } state)
            {
                var center = b.X + b.Width / 2;
                var text = state == "unavailable" ? "a1a192" : "000000";
                Text(b.Label, center, b.Y + 12, b.Width - 6, text, centered: true);
                Indicator(center, b.Y + 34, state);
                Text(char.ToUpperInvariant(state[0]) + state[1..], center, b.Y + 54, b.Width - 6, text, centered: true);
            }
            else
            {
                if (b.Color is { } color)
                {
                    Rectangle(color, b.X + 3, b.Y + 5, b.X + 8, b.Y + 21);
                    Rectangle("999999", b.X + 3, b.Y + 5, b.X + 8, b.Y + 21, outline: true);
                }
                var lines = b.Label.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                    Text(lines[i], b.X + (b.Color is null ? b.Width / 2 : b.Width / 2 + 4),
                        lines.Length == 2 ? b.Y + 7 + i * 11 : b.Y + b.Height / 2,
                        b.Color is null ? b.Width - 4 : 38, centered: true);
            }
        }

        private void Indicator(int x, int y, string state)
        {
            Ellipse("ffffff", x - 9, y - 8, x + 9, y + 10);
            using var shader = SKShader.CreateRadialGradient(new SKPoint(x - 3, y - 4), 16,
                state == "on" ? [SKColor.Parse("c1f28e"), SKColor.Parse("43b842"), SKColor.Parse("198532")]
                    : [SKColor.Parse("edf1f5"), SKColor.Parse("a6b6c8"), SKColor.Parse("758ca5")],
                [0, .55f, 1], SKShaderTileMode.Clamp);
            Ink("ffffff"); paint.Shader = shader;
            canvas.DrawCircle(x, y, 8, paint); paint.Shader = null;
            Ink(state == "on" ? "3b8d3a" : "879bad", outline: true);
            canvas.DrawCircle(x, y, 8, paint);
            if (state == "unavailable") Line("7c8790", 1, x - 3, y, x + 3, y);
        }

        private void Lamp(bool on)
        {
            if (on)
            {
                using var glow = SKShader.CreateRadialGradient(new SKPoint(149, 75), 34,
                    [SKColor.Parse("fff1b1"), SKColor.Parse("fff6d8"), SKColors.Transparent],
                    [0, .75f, 1], SKShaderTileMode.Clamp);
                Ink("ffffff"); paint.Shader = glow;
                canvas.DrawOval(new SKRect(115, 44, 183, 112), paint); paint.Shader = null;
            }
            Ellipse("d8d4c9", 131, 113, 170, 120);
            var color = on ? "b68523" : "748599";
            Line(color, 5, 149, 79, 149, 113);
            Line(on ? "fff1a4" : "dce4ed", 1, 148, 81, 148, 111);
            Ellipse(color, 130, 110, 168, 117);
            Ellipse(on ? "e4b342" : "a3b2c4", 132, 110, 166, 114);
            using var shade = new SKPath();
            shade.MoveTo(131, 50); shade.LineTo(167, 50); shade.LineTo(180, 80); shade.LineTo(118, 80); shade.Close();
            canvas.Save(); canvas.ClipPath(shade);
            Gradient(new SKRect(118, 50, 181, 81), on ? ["ffed9c", "eac250", "d9a32e"] : ["e6edf4", "b2c2d3", "94a6bb"]);
            Gradient(new SKRect(124, 54, 128, 74), on ? ["fff3c0", "f7df85"] : ["f1f5fa", "cdd8e5"]);
            canvas.Restore();
            Ink(color, outline: true); canvas.DrawPath(shade, paint);
            Line(on ? "fff8cd" : "f2f6fa", 1, 133, 52, 165, 52);
            Line(on ? "bc891d" : "7b91aa", 2, 120, 78, 178, 78);
            Line(on ? "f9df81" : "c8d5e3", 1, 119, 80, 179, 80);
        }

        private void Aircon(bool on)
        {
            Rounded("d4d2ca", 115, 61, 73, 34, 4, "d4d2ca");
            using var clip = new SKRoundRect(new SKRect(113, 58, 186, 92), 4);
            canvas.Save(); canvas.ClipRoundRect(clip);
            Gradient(clip.Rect, ["ffffff", "f1f6fa", "d6e1eb"], [0, .5f, 1]);
            canvas.Restore();
            Ink("728ba4", outline: true);
            canvas.DrawRoundRect(new SKRect(113.5f, 58.5f, 185.5f, 91.5f), 4, 4, paint);
            Line("ffffff", 1, 117, 60, 181, 60);
            Rectangle("55738d", 118, 80, 180, 87);
            foreach (var y in new[] { 81, 84, 87 }) Line("b5c5d5", 1, 120, y, 179, y);
            Rectangle(on ? "3fac36" : "999999", 174, 69, 178, 72);
            Line(on ? "a8f27e" : "bfc6cc", 1, 174, 69, 177, 69);
            if (on)
                foreach (var x in new[] { 132, 148, 164 })
                {
                    Line("7fc5ed", 2, x, 97, x - 3, 103, x + 2, 111);
                    Line("399bde", 1, x, 97, x - 3, 103, x + 2, 111);
                }
        }

        public void Text(string text, int x, int y, int width, string color = "111111", bool centered = false, bool heading = false)
        {
            using var font = new SKFont(heading ? title : small, heading ? 13 : 11)
            {
                Edging = SKFontEdging.Alias, Hinting = SKFontHinting.Full, Subpixel = false,
            };
            var display = text;
            while (display.Length > 0 && font.MeasureText(display) > width)
                display = display.Length <= 2 ? "" : display[..^2] + "…";
            var metrics = font.Metrics;
            var baseline = centered ? y - (metrics.Ascent + metrics.Descent) / 2 : y + Math.Ceiling(-metrics.Ascent);
            Ink(color);
            canvas.DrawText(display, centered ? x - font.MeasureText(display) / 2 : x, (float)Math.Round(baseline), font, paint);
        }

        public void Dispose() => paint.Dispose();
    }

    public void Dispose()
    {
        cache.Dispose(); small.Dispose(); title.Dispose(); controlIcon.Dispose(); powerIcon.Dispose();
    }
}
