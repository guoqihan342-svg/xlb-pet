using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace LubanDesktopPet;

internal sealed class TrayIconService : IDisposable
{
    private const string IconResourceName =
        "LubanDesktopPet.Assets.luban-tray.ico";

    private readonly Icon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _exitItem;
    private NotifyIcon? _notifyIcon;
    private bool _disposed;

    public TrayIconService()
    {
        using var iconStream =
            Assembly.GetExecutingAssembly().GetManifestResourceStream(
                IconResourceName)
            ?? throw new InvalidOperationException(
                "Missing embedded Luban tray icon.");
        _icon = new Icon(iconStream);

        _menu = new ContextMenuStrip
        {
            AutoSize = true,
            BackColor = Color.FromArgb(255, 253, 248),
            Font = new Font(
                "Microsoft YaHei",
                9F,
                FontStyle.Regular,
                GraphicsUnit.Point),
            Renderer = new ToolStripProfessionalRenderer(
                new CuteTrayColorTable()),
            ShowImageMargin = false,
            ShowCheckMargin = false
        };
        _menu.Padding = new Padding(5, 5, 5, 5);

        _exitItem = new ToolStripMenuItem("退出小鲁班")
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(92, 67, 72),
            Padding = new Padding(12, 7, 12, 7)
        };
        _exitItem.Click += ExitItem_Click;
        _menu.Items.Add(_exitItem);

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = _icon,
            Text = "小鲁班桌宠",
            Visible = true
        };
    }

    public event EventHandler? ExitRequested;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _exitItem.Click -= ExitItem_Click;
        if (_notifyIcon is { } notifyIcon)
        {
            // Hide first so Explorer removes the icon immediately instead of
            // leaving a stale entry until the user moves the pointer over it.
            notifyIcon.Visible = false;
            notifyIcon.ContextMenuStrip = null;
            notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _menu.Dispose();
        _icon.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ExitItem_Click(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class CuteTrayColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground =>
            Color.FromArgb(255, 253, 248);

        public override Color MenuBorder =>
            Color.FromArgb(239, 191, 137);

        public override Color MenuItemBorder =>
            Color.FromArgb(241, 172, 93);

        public override Color MenuItemSelected =>
            Color.FromArgb(255, 238, 211);

        public override Color MenuItemPressedGradientBegin =>
            Color.FromArgb(255, 225, 184);

        public override Color MenuItemPressedGradientEnd =>
            Color.FromArgb(255, 238, 211);
    }
}
