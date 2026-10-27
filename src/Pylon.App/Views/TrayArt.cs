using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Pylon.App.Views;

/// <summary>
/// The tray and window icon, drawn from the pylon glyph at startup (there's no .ico pipeline yet; Phase 6 adds one like
/// Postern's). Teal normally, pink for Pylon (Dev) as Postern (Dev) is, and amber with a dot while there are events
/// the widget hasn't shown.
/// </summary>
internal static class TrayArt
{
    static WindowIcon? _normal;
    static WindowIcon? _unread;

    /// <summary>The icon's colour: teal, or pink for Pylon (Dev).</summary>
    public static IBrush Colour => Pylon.Broker.Profile.IsDev ? Ui.Brush("#F472B6") : Ui.Accent;

    static IBrush Base => Colour;

    public static WindowIcon WindowIcon(bool unread = false) => unread
        ? _unread ??= Render(Ui.Warning, dot: true)
        : _normal ??= Render(Base, dot: false);

    static WindowIcon Render(IBrush colour, bool dot)
    {
        const int size = 64;
        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
        using (var context = bitmap.CreateDrawingContext())
        {
            using (context.PushTransform(Matrix.CreateScale(size / 24.0, size / 24.0)))
                context.DrawGeometry(colour, null, Icons.Geometry(Icons.Pylon));
            if (dot)
                context.DrawEllipse(Ui.Danger, new Pen(Ui.Sunken, 3), new Point(size - 13, 13), 11, 11);
        }
        var stream = new MemoryStream();
        bitmap.Save(stream);
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}
