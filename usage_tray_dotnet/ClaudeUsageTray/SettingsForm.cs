namespace ClaudeUsageTray;

/// <summary>Plain Panel with double buffering forced on — without this, the
/// custom-painted rounded card background tears into a visible hatch/ghost
/// pattern while the window is being live-resized.</summary>
internal class DoubleBufferedPanel : Panel
{
    public DoubleBufferedPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }
}

/// <summary>Same fix as <see cref="DoubleBufferedPanel"/>, for the card grid.</summary>
internal class DoubleBufferedTableLayoutPanel : TableLayoutPanel
{
    public DoubleBufferedTableLayoutPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }
}

public sealed class SettingsForm : Form
{
    private const int PageMargin = 22;
    private const int CardPadding = 18;
    private const int MinColumnWidth = 330;
    private const int GridGap = 14;
    private const int FooterPadding = 16;
    private const int ButtonHeight = 36;

    private static readonly Color AccentColor = Color.FromArgb(255, 0, 103, 192);
    private static readonly Color PageBg = Color.FromArgb(255, 251, 251, 252);
    private static readonly Color CardBg = Color.FromArgb(255, 246, 246, 248);
    private static readonly Color CardBorder = Color.FromArgb(255, 224, 224, 228);
    private static readonly Color TitleColor = Color.FromArgb(255, 32, 32, 35);
    private static readonly Color SubtleColor = Color.FromArgb(255, 105, 105, 112);
    private static readonly Color OkGreen = Color.FromArgb(255, 46, 139, 87);

    private NumericUpDown _refreshNumeric = null!;
    private RadioButton _richMode = null!;
    private RadioButton _tooltipMode = null!;
    private readonly Button[] _themeButtons = new Button[3];
    private AppTheme _selectedTheme;
    private CheckBox _showClaude = null!;
    private CheckBox _showChatGpt = null!;
    private CheckBox _telegramEnabled = null!;
    private TextBox _telegramToken = null!;
    private CheckBox _autoStart = null!;

    private readonly long? _telegramChatId;
    private readonly Func<string, bool> _isLoggedIn;
    private readonly Action<string> _triggerLogin;
    public AppSettings Result { get; private set; }

    private Panel _scrollHost = null!;
    private DoubleBufferedTableLayoutPanel _grid = null!;
    private readonly List<Control> _cards = new();
    private int _currentColumns;

    public SettingsForm(AppSettings current, Func<string, bool> isLoggedIn, Action<string> triggerLogin)
    {
        Result = current;
        _telegramChatId = current.TelegramChatId;
        _selectedTheme = current.Theme;
        _isLoggedIn = isLoggedIn;
        _triggerLogin = triggerLogin;

        Text = "Ajustes";
        Icon = IconFactory.BuildRobotIcon();
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = PageBg;
        MinimumSize = new Size(MinColumnWidth + PageMargin * 2 + 40, 340);
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);

        // Docked bottom first so the scrolling area claims only what's left.
        Controls.Add(BuildFooter());

        // Plain Panel + AutoScroll is the one combination whose scrollable
        // extent reliably honors Padding.Bottom — a wrapping FlowLayoutPanel
        // silently ignored it here, which is why the last card used to sit
        // half behind the button bar with no way to scroll past it.
        _scrollHost = new DoubleBufferedPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(PageMargin, PageMargin, PageMargin, PageMargin + 24),
            BackColor = PageBg,
        };
        Controls.Add(_scrollHost);

        // No AutoSize here on purpose: Dock=Top + AutoSize on a
        // TableLayoutPanel is a known fight WinForms loses to AutoSize —
        // it shrinks the whole grid down to its content's preferred width
        // instead of stretching to fill the parent, no matter what Dock
        // says. Width stays purely Dock-driven; height is set by hand in
        // RelayoutGrid() using GetPreferredSize at that width.
        _grid = new DoubleBufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            BackColor = PageBg,
        };
        _scrollHost.Controls.Add(_grid);

        _cards.Add(BuildCard("General", BuildGeneralCard));
        _cards.Add(BuildCard("Actualización", BuildUpdateCard));
        _cards.Add(BuildCard("Modo de activación", BuildPopupModeCard));
        _cards.Add(BuildCard("Apariencia", BuildAppearanceCard));
        _cards.Add(BuildCard("Servicios a mostrar", BuildServicesCard));
        _cards.Add(BuildCard("Bot de Telegram", BuildTelegramCard));

        _scrollHost.Resize += (s, e) => RelayoutGrid();

        var footerHeight = ButtonHeight + FooterPadding * 2;
        var workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        var defaultWidth = Math.Min(MinColumnWidth * 2 + PageMargin * 2 + GridGap + 40, workArea.Width - 80);
        var defaultHeight = Math.Min(620 + footerHeight, workArea.Height - 100);
        ClientSize = new Size(defaultWidth, defaultHeight);

        RelayoutGrid();
    }

    /// <summary>
    /// Regroups the (already-built, stateful) cards into a real equal-width,
    /// equal-row-height grid — like an HTML table — recomputing the column
    /// count from the available width. Cards are re-parented, never rebuilt,
    /// so anything the user already typed/toggled survives a resize.
    /// </summary>
    private void RelayoutGrid()
    {
        // Reserve room for a vertical scrollbar unconditionally: recomputing
        // this every time one appears/disappears would make the column count
        // oscillate right at the boundary width.
        var available = _scrollHost.ClientSize.Width - _scrollHost.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
        if (available <= 0) return;

        var columns = Math.Max(1, (available + GridGap) / (MinColumnWidth + GridGap));
        if (columns == _currentColumns) return;
        _currentColumns = columns;

        _grid.SuspendLayout();
        _grid.Controls.Clear();
        _grid.ColumnStyles.Clear();
        _grid.RowStyles.Clear();
        _grid.ColumnCount = columns;
        _grid.RowCount = (int)Math.Ceiling(_cards.Count / (double)columns);

        for (var c = 0; c < columns; c++)
        {
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        }

        for (var i = 0; i < _cards.Count; i++)
        {
            var col = i % columns;
            var row = i / columns;
            if (col == 0) _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var isLastCol = col == columns - 1;
            var isLastRow = row == _grid.RowCount - 1;
            _cards[i].Margin = new Padding(0, 0, isLastCol ? 0 : GridGap, isLastRow ? 0 : GridGap);
            _cards[i].Dock = DockStyle.Fill;
            _grid.Controls.Add(_cards[i], col, row);
        }

        // Width: forced explicitly rather than left to Dock, since Dock's
        // own stretch-to-parent pass happens on the *next* layout cycle and
        // GetPreferredSize below needs the real width right now to measure
        // wrapped/stretched row heights correctly.
        _grid.Width = available;
        _grid.Height = _grid.GetPreferredSize(new Size(available, 0)).Height;
        _grid.ResumeLayout(true);
    }

    private Panel BuildFooter()
    {
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = ButtonHeight + FooterPadding * 2,
            Padding = new Padding(PageMargin, FooterPadding, PageMargin, FooterPadding),
            BackColor = PageBg,
        };
        footer.Paint += (s, e) =>
        {
            using var pen = new Pen(CardBorder);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };

        var okButton = new Button
        {
            Text = "Guardar",
            Size = new Size(112, ButtonHeight),
            DialogResult = DialogResult.OK,
            FlatStyle = FlatStyle.Flat,
            BackColor = AccentColor,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
            Margin = new Padding(0),
        };
        okButton.FlatAppearance.BorderSize = 0;
        okButton.Click += (s, e) => SaveAndClose();

        var cancelButton = new Button
        {
            Text = "Cancelar",
            Size = new Size(112, ButtonHeight),
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = TitleColor,
            Font = new Font("Segoe UI", 9.5f),
            Margin = new Padding(0, 0, 10, 0),
        };
        cancelButton.FlatAppearance.BorderColor = CardBorder;

        // RightToLeft flow keeps both buttons glued to the right edge at any width.
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.Transparent,
        };
        row.Controls.Add(okButton);
        row.Controls.Add(cancelButton);
        footer.Controls.Add(row);

        AcceptButton = okButton;
        CancelButton = cancelButton;
        return footer;
    }

    /// <summary>Builds one rounded card as a free-standing control (not yet placed in the grid).</summary>
    private Panel BuildCard(string title, Action<TableLayoutPanel> populate)
    {
        var card = new DoubleBufferedPanel
        {
            Padding = new Padding(CardPadding),
            BackColor = CardBg,
        };
        card.Paint += (s, e) => DrawCardChrome(card, e);

        var inner = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Color.Transparent,
        };
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(inner, new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            ForeColor = TitleColor,
            Margin = new Padding(0, 0, 0, 12),
        });
        populate(inner);

        card.Controls.Add(inner);
        return card;
    }

    private static void AddRow(TableLayoutPanel table, Control child)
    {
        table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(child, 0, table.RowCount - 1);
    }

    private static void DrawCardChrome(Panel panel, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(CardBg);
        using var path = RoundedRect(new Rectangle(0, 0, panel.Width, panel.Height), 10);
        e.Graphics.FillPath(brush, path);
        using var pen = new Pen(CardBorder);
        e.Graphics.DrawPath(pen, path);
    }

    private void BuildGeneralCard(TableLayoutPanel inner)
    {
        _autoStart = new CheckBox
        {
            Text = "Iniciar automáticamente con Windows",
            AutoSize = true,
            Checked = AutoStartHelper.IsEnabled(),
        };
        AddRow(inner, _autoStart);
    }

    private void BuildUpdateCard(TableLayoutPanel inner)
    {
        AddRow(inner, new Label
        {
            Text = "Frecuencia de actualización",
            AutoSize = true,
            ForeColor = SubtleColor,
            Font = new Font("Segoe UI", 9f),
            Margin = new Padding(0, 0, 0, 6),
        });

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var slider = new SliderControl
        {
            Minimum = 1,
            Maximum = 60,
            Value = Math.Clamp(Result.RefreshMinutes, 1, 60),
            Dock = DockStyle.Fill,
            BackColor = CardBg,
            TrackColor = CardBorder,
            Margin = new Padding(0, 3, 8, 0),
        };
        _refreshNumeric = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 60,
            Value = Math.Clamp(Result.RefreshMinutes, 1, 60),
            Width = 58,
            TextAlign = HorizontalAlignment.Center,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0),
        };
        var suffix = new Label
        {
            Text = "min",
            AutoSize = true,
            ForeColor = SubtleColor,
            Anchor = AnchorStyles.None,
            Margin = new Padding(6, 0, 0, 0),
        };

        slider.ValueChanged += (s, e) => { if (_refreshNumeric.Value != slider.Value) _refreshNumeric.Value = slider.Value; };
        _refreshNumeric.ValueChanged += (s, e) => { if (slider.Value != (int)_refreshNumeric.Value) slider.Value = (int)_refreshNumeric.Value; };

        row.Controls.Add(slider, 0, 0);
        row.Controls.Add(_refreshNumeric, 1, 0);
        row.Controls.Add(suffix, 2, 0);
        AddRow(inner, row);
    }

    private void BuildPopupModeCard(TableLayoutPanel inner)
    {
        _richMode = new RadioButton
        {
            Text = "Ventana flotante (recomendado)",
            AutoSize = true,
            Checked = Result.PopupMode == PopupMode.Rich,
            Margin = new Padding(0, 0, 0, 6),
        };
        _tooltipMode = new RadioButton
        {
            Text = "Tooltip sencillo",
            AutoSize = true,
            Checked = Result.PopupMode == PopupMode.Tooltip,
        };
        AddRow(inner, _richMode);
        AddRow(inner, _tooltipMode);
    }

    private void BuildAppearanceCard(TableLayoutPanel inner)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 34,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
        };
        for (var i = 0; i < 3; i++) row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));

        string[] labels = { "Sistema", "Claro", "Oscuro" };
        for (var i = 0; i < 3; i++)
        {
            var theme = (AppTheme)i;
            var btn = new Button
            {
                Text = labels[i],
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f),
                Margin = new Padding(i == 0 ? 0 : 3, 0, i == 2 ? 0 : 3, 0),
            };
            btn.FlatAppearance.BorderColor = CardBorder;
            btn.Click += (s, e) => SelectTheme(theme);
            row.Controls.Add(btn, i, 0);
            _themeButtons[i] = btn;
        }

        StyleThemeButtons();
        AddRow(inner, row);
    }

    private void SelectTheme(AppTheme theme)
    {
        _selectedTheme = theme;
        StyleThemeButtons();
    }

    private void StyleThemeButtons()
    {
        for (var i = 0; i < _themeButtons.Length; i++)
        {
            var btn = _themeButtons[i];
            if (btn is null) continue;
            var selected = (int)_selectedTheme == i;
            btn.BackColor = selected ? AccentColor : Color.White;
            btn.ForeColor = selected ? Color.White : TitleColor;
            btn.Font = new Font("Segoe UI", 9f, selected ? FontStyle.Bold : FontStyle.Regular);
        }
    }

    private void BuildServicesCard(TableLayoutPanel inner)
    {
        _showClaude = new CheckBox { Text = "Claude", AutoSize = true, Checked = Result.ShowClaude, Anchor = AnchorStyles.Left };
        _showChatGpt = new CheckBox { Text = "ChatGPT", AutoSize = true, Checked = Result.ShowChatGpt, Anchor = AnchorStyles.Left };

        AddRow(inner, BuildServiceRow(_showClaude, "Claude"));
        AddRow(inner, BuildServiceRow(_showChatGpt, "ChatGPT"));
    }

    private Control BuildServiceRow(CheckBox toggle, string providerName)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 6),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        row.Controls.Add(toggle, 0, 0);
        row.Controls.Add(BuildLoginStatus(providerName), 1, 0);
        return row;
    }

    private Control BuildLoginStatus(string providerName)
    {
        if (_isLoggedIn(providerName))
        {
            return new Label
            {
                Text = "✓ Sesión iniciada",
                AutoSize = true,
                ForeColor = OkGreen,
                Font = new Font("Segoe UI", 8.5f),
                Anchor = AnchorStyles.Right,
                Margin = new Padding(8, 3, 0, 0),
            };
        }

        var link = new LinkLabel
        {
            Text = "Vincular",
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5f),
            Anchor = AnchorStyles.Right,
            Margin = new Padding(8, 3, 0, 0),
        };
        link.Click += (s, e) => _triggerLogin(providerName);
        return link;
    }

    private void BuildTelegramCard(TableLayoutPanel inner)
    {
        _telegramEnabled = new CheckBox
        {
            Text = "Activar bot de Telegram",
            AutoSize = true,
            Checked = Result.TelegramEnabled,
            Margin = new Padding(0, 0, 0, 6),
        };
        AddRow(inner, _telegramEnabled);

        AddRow(inner, new Label
        {
            Text = "Escríbele /uso al bot después de guardar para vincular tu chat.",
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = SubtleColor,
            Font = new Font("Segoe UI", 8.5f),
            Margin = new Padding(0, 0, 0, 8),
        });

        _telegramToken = new TextBox
        {
            Text = Result.TelegramBotToken ?? "",
            Dock = DockStyle.Top,
            PlaceholderText = "Token de @BotFather",
        };
        AddRow(inner, _telegramToken);

        _telegramEnabled.CheckedChanged += (s, e) => _telegramToken.Enabled = _telegramEnabled.Checked;
        _telegramToken.Enabled = _telegramEnabled.Checked;

        if (Result.TelegramChatId is not null)
        {
            AddRow(inner, new Label
            {
                Text = "Chat vinculado ✓",
                AutoSize = true,
                ForeColor = OkGreen,
                Font = new Font("Segoe UI", 8.5f),
                Margin = new Padding(0, 8, 0, 0),
            });
        }
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d - 1, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d - 1, bounds.Bottom - d - 1, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d - 1, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void SaveAndClose()
    {
        AutoStartHelper.SetEnabled(_autoStart.Checked);

        var newToken = string.IsNullOrWhiteSpace(_telegramToken.Text) ? null : _telegramToken.Text.Trim();
        var tokenChanged = newToken != Result.TelegramBotToken;

        Result = new AppSettings
        {
            RefreshMinutes = (int)_refreshNumeric.Value,
            PopupMode = _richMode.Checked ? PopupMode.Rich : PopupMode.Tooltip,
            Theme = _selectedTheme,
            ShowClaude = _showClaude.Checked,
            ShowChatGpt = _showChatGpt.Checked,
            TelegramEnabled = _telegramEnabled.Checked,
            TelegramBotToken = newToken,
            TelegramChatId = tokenChanged ? null : _telegramChatId,
        };
    }
}
