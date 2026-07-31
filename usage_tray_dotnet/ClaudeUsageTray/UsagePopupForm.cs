namespace ClaudeUsageTray;

public sealed class UsagePopupForm : Form
{
    private TableLayoutPanel _root = null!;
    private FlowLayoutPanel _card = null!;
    private LinkLabel _settingsLink = null!;
    private LinkLabel _refreshLink = null!;
    private ThemePalette _palette = ThemePalette.Light;
    private AppTheme _currentTheme = AppTheme.System;

    public event EventHandler? RefreshRequested;
    public event EventHandler? SettingsRequested;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    public UsagePopupForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(1);
        Deactivate += (s, e) => Hide();

        BuildLayout();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DwmHelper.EnableRoundedCorners(Handle);
    }

    private void BuildLayout()
    {
        _card = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16),
            Dock = DockStyle.Fill,
        };
        Controls.Add(_card);

        _root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Color.Transparent,
        };
        _card.Controls.Add(_root);

        var footer = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Width = 260,
            Height = 24,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 10, 0, 0),
        };
        _settingsLink = MakeLinkLabel("Ajustes");
        _settingsLink.Click += (s, e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _refreshLink = MakeLinkLabel("Actualizar");
        _refreshLink.Click += (s, e) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        footer.Controls.Add(_settingsLink);
        footer.Controls.Add(_refreshLink);
        _card.Controls.Add(footer);
    }

    private LinkLabel MakeLinkLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        BackColor = Color.Transparent,
        Font = new Font("Segoe UI", 8.5f),
        Margin = new Padding(8, 4, 0, 0),
        LinkBehavior = LinkBehavior.HoverUnderline,
    };

    public void Render(IEnumerable<UsageSnapshot> snapshots, AppTheme theme)
    {
        _currentTheme = theme;
        _palette = ThemePalette.Resolve(theme);
        ApplyPalette();

        _root.Controls.Clear();
        _root.RowStyles.Clear();
        _root.RowCount = 0;

        var first = true;
        foreach (var snap in snapshots)
        {
            if (!first) AddSeparator();
            first = false;

            AddRow(new Label
            {
                Text = snap.ServiceName,
                AutoSize = true,
                ForeColor = _palette.TitleText,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 8),
            });

            if (!snap.Ok)
            {
                AddRow(new Label
                {
                    Text = snap.ErrorMessage ?? "No se pudo leer el uso",
                    AutoSize = true,
                    ForeColor = _palette.ErrorText,
                    BackColor = Color.Transparent,
                    Font = new Font("Segoe UI", 9f),
                    MaximumSize = new Size(260, 0),
                });
                continue;
            }

            foreach (var bar in snap.Bars)
            {
                AddRow(BuildBarRow(bar));
            }

            if (!string.IsNullOrEmpty(snap.ExtraLine))
            {
                AddRow(new Label
                {
                    Text = snap.ExtraLine,
                    AutoSize = true,
                    ForeColor = _palette.MutedText,
                    BackColor = Color.Transparent,
                    Font = new Font("Segoe UI", 8.5f),
                    Margin = new Padding(0, 10, 0, 0),
                });
            }
        }
    }

    private void ApplyPalette()
    {
        BackColor = _palette.RingBg;
        _card.BackColor = _palette.CardBg;
        _settingsLink.LinkColor = _palette.LinkText;
        _settingsLink.ActiveLinkColor = _palette.LinkHoverText;
        _refreshLink.LinkColor = _palette.LinkText;
        _refreshLink.ActiveLinkColor = _palette.LinkHoverText;
    }

    private Control BuildBarRow(UsageBar bar)
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Width = 260,
            Margin = new Padding(0, 0, 0, 12),
            BackColor = Color.Transparent,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = bar.Label,
            AutoSize = true,
            ForeColor = _palette.BodyText,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 9f),
        };
        var pct = new Label
        {
            Text = $"{bar.Percent}%",
            AutoSize = true,
            ForeColor = _palette.TitleText,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            Anchor = AnchorStyles.Right,
        };
        panel.Controls.Add(label, 0, 0);
        panel.Controls.Add(pct, 1, 0);

        var barView = new ProgressBarView
        {
            Width = 260,
            Percent = bar.Percent,
            TrackColor = _palette.TrackBg,
            // Rounded pill reads great on light backgrounds; on dark
            // backgrounds the anti-aliased rounded seam between fill and
            // track edge is visible and looks broken, so keep it near-square there.
            CornerRadius = ThemePalette.ResolveIsDark(_currentTheme) ? 2 : 5,
            Margin = new Padding(0, 5, 0, 3),
        };
        panel.SetColumnSpan(barView, 2);
        panel.Controls.Add(barView, 0, 1);

        if (bar.ResetAt is { } resetAt)
        {
            var reset = new Label
            {
                Text = $"{TimeFormat.Relative(resetAt)} · {resetAt:ddd dd MMM HH:mm}",
                AutoSize = true,
                ForeColor = _palette.MutedText,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 8f),
            };
            panel.SetColumnSpan(reset, 2);
            panel.Controls.Add(reset, 0, 2);
        }

        return panel;
    }

    private void AddSeparator()
    {
        AddRow(new Panel
        {
            Height = 1,
            Width = 260,
            BackColor = _palette.SeparatorColor,
            Margin = new Padding(0, 6, 0, 14),
        });
    }

    private void AddRow(Control c)
    {
        _root.RowCount++;
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.Controls.Add(c, 0, _root.RowCount - 1);
    }

    public void ShowNearCursor()
    {
        PerformLayout();
        Size = PreferredSize;

        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var cursor = Cursor.Position;

        var x = Math.Min(cursor.X, area.Right - Width - 8);
        var y = area.Bottom - Height - 8;
        Location = new Point(Math.Max(area.Left + 8, x), y);

        Show();
        Activate();
    }
}
