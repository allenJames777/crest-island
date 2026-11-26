using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Crest.Tray;

/// <summary>
/// The tray icon and the one menu Crest has (Show/Hide island, Settings, Start with Windows, Quit).
/// The same menu opens from the tray and from a right-click on the island.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _showHide;
    private readonly Forms.ToolStripMenuItem _startup;
    private readonly MenuHost _host = new();
    private readonly Drawing.Icon _drawnIcon;
    private readonly IntPtr _iconHandle;

    public TrayIcon()
    {
        _showHide = new Forms.ToolStripMenuItem("Hide island", null, (_, _) => ShowHideClicked?.Invoke());
        var settings = new Forms.ToolStripMenuItem("Settings...", null, (_, _) => SettingsClicked?.Invoke());
        _startup = new Forms.ToolStripMenuItem("Start with Windows", null, (_, _) => StartupClicked?.Invoke());
        var quit = new Forms.ToolStripMenuItem("Quit", null, (_, _) => QuitClicked?.Invoke());
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.AddRange([_showHide, settings, _startup, new Forms.ToolStripSeparator(), quit]);
        _menu.Closed += (_, _) => OnMenuClosed();

        (_drawnIcon, _iconHandle) = DrawIcon();
        _icon = new Forms.NotifyIcon
        {
            Icon = _drawnIcon,
            Text = "Crest",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => SettingsClicked?.Invoke();
    }

    public event Action? ShowHideClicked;
    public event Action? SettingsClicked;
    public event Action? StartupClicked;
    public event Action? QuitClicked;

    public void Update(bool islandVisible, bool startWithWindows)
    {
        _showHide.Text = islandVisible ? "Hide island" : "Show island";
        _startup.Checked = startWithWindows;
    }

    /// <summary>
    /// Opens the menu at the cursor (right-click on the island). The island window is never active, so the menu is
    /// owned by a hidden window brought to the front first; that is what lets it close on a click elsewhere or Esc.
    /// </summary>
    public void ShowMenuAtCursor()
    {
        _previousForeground = GetForegroundWindow();
        _host.Prepare();
        SetForegroundWindow(_host.Handle);
        _menu.Show(_host, _host.PointToClient(Forms.Cursor.Position));
    }

    /// <summary>Raised after the menu closes, however it closed.</summary>
    public event Action? MenuClosed;

    private IntPtr _previousForeground;

    /// <summary>
    /// The hidden owner window took the foreground to open the menu; hide it and give the foreground back to the
    /// app the user was in, unless something else (such as the Settings window) has come forward since.
    /// </summary>
    private void OnMenuClosed()
    {
        // Run after the clicked item's action, so a window that item opened (Settings) is already in front.
        var previous = _previousForeground;
        _previousForeground = IntPtr.Zero;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            if (_host.Visible)
            {
                bool hostInFront = GetForegroundWindow() == _host.Handle;
                _host.Hide();
                if (hostInFront && previous != IntPtr.Zero) SetForegroundWindow(previous);
            }
            MenuClosed?.Invoke();
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _host.Dispose();
        _drawnIcon.Dispose();
        DestroyIcon(_iconHandle);
    }

    /// <summary>A small purple pill with a light dot, readable on light and dark taskbars.</summary>
    private static (Drawing.Icon Icon, IntPtr Handle) DrawIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var pill = new Drawing.Drawing2D.GraphicsPath();
            var r = new Drawing.RectangleF(2, 9, 28, 14);
            float d = r.Height;
            pill.AddArc(r.X, r.Y, d, d, 90, 180);
            pill.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            pill.CloseFigure();
            using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x7C, 0x5C, 0xFF));
            g.FillPath(fill, pill);
            using var dot = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xF5, 0xF5, 0xF7));
            g.FillEllipse(dot, 20, 13, 6, 6);
        }
        IntPtr handle = bitmap.GetHicon();
        return (Drawing.Icon.FromHandle(handle), handle);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>An invisible 1 x 1 tool window that owns the menu when it opens from the island.</summary>
    private sealed class MenuHost : Forms.Form
    {
        public MenuHost()
        {
            FormBorderStyle = Forms.FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = Forms.FormStartPosition.Manual;
            Location = new Drawing.Point(-32000, -32000);
            Size = new Drawing.Size(1, 1);
            Opacity = 0;
        }

        protected override Forms.CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                p.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW: never in Alt+Tab
                return p;
            }
        }

        public void Prepare()
        {
            if (!Visible) Show();
        }
    }
}
