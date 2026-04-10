namespace xProtoView.Services;

public static class WindowLayoutHelper
{
    // 恢复窗口大小与位置。
    public static void ApplyLayout(Form form, WindowLayoutConfig? layout)
    {
        if (layout is null || layout.Width is null || layout.Height is null)
        {
            return;
        }

        var width = Math.Max(form.MinimumSize.Width, layout.Width.Value);
        var height = Math.Max(form.MinimumSize.Height, layout.Height.Value);
        var bounds = new Rectangle(
            layout.Left ?? form.Left,
            layout.Top ?? form.Top,
            width,
            height);

        if (!IsOnAnyScreen(bounds))
        {
            return;
        }

        form.StartPosition = FormStartPosition.Manual;
        form.Bounds = bounds;
        form.WindowState = layout.IsMaximized ? FormWindowState.Maximized : FormWindowState.Normal;
    }

    // 抓取当前窗口布局。
    public static WindowLayoutConfig CaptureLayout(Form form)
    {
        var bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
        return new WindowLayoutConfig
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = form.WindowState == FormWindowState.Maximized
        };
    }

    private static bool IsOnAnyScreen(Rectangle bounds)
    {
        return Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds));
    }
}
