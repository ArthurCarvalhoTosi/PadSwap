namespace PadSwap;

/// <summary>Settings window: live view of what the game receives and an editor for the button map.</summary>
internal sealed class SettingsForm : Form
{
    private const int RefreshIntervalMilliseconds = 33;

    // Order shown in the editor, with the labels the user sees.
    private static readonly (PadButton Button, string Label)[] Buttons =
    [
        (PadButton.A, "A"), (PadButton.B, "B"), (PadButton.X, "X"), (PadButton.Y, "Y"),
        (PadButton.LeftShoulder, "LB"), (PadButton.RightShoulder, "RB"),
        (PadButton.LeftThumb, "L3"), (PadButton.RightThumb, "R3"),
        (PadButton.Back, "Back"), (PadButton.Start, "Start"), (PadButton.Guide, "Guide"),
        (PadButton.DpadUp, "Direcional ↑"), (PadButton.DpadDown, "Direcional ↓"),
        (PadButton.DpadLeft, "Direcional ←"), (PadButton.DpadRight, "Direcional →"),
    ];

    private readonly Remapper _remapper;
    private readonly PadView _padView = new() { Dock = DockStyle.Top, Height = 250, BackColor = Color.FromArgb(24, 27, 33) };
    private readonly Label _liveLabel = new() { Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.MiddleCenter };
    private readonly ComboBox _modeBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly Dictionary<PadButton, ComboBox> _targetBoxes = [];
    private readonly Label _savedLabel = new() { AutoSize = true, ForeColor = Color.FromArgb(46, 160, 67), Margin = new Padding(12, 9, 0, 0) };
    private bool _modeChosen;
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = RefreshIntervalMilliseconds };

    public SettingsForm(Remapper remapper, Icon icon)
    {
        _remapper = remapper;

        Text = "PadSwap";
        Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(600, 640);
        Font = new Font("Segoe UI", 9);

        _modeBox.Items.AddRange(["Modo Xbox", "Modo PlayStation"]);
        _modeBox.SelectedIndex = (int)PadKind.Xbox;
        _modeBox.SelectedIndexChanged += (_, _) => ShowEntries(ButtonMap.ReadEntries(SelectedKind));
        _modeBox.SelectionChangeCommitted += (_, _) => _modeChosen = true;

        // Controls docked to the top stack in reverse order of being added.
        Controls.Add(BuildEditor());
        Controls.Add(_liveLabel);
        Controls.Add(_padView);

        ShowEntries(ButtonMap.ReadEntries(SelectedKind));
        _refreshTimer.Tick += (_, _) => RefreshLiveView();
        _refreshTimer.Start();
        RefreshLiveView();
    }

    private PadKind SelectedKind => (PadKind)_modeBox.SelectedIndex;

    private Control BuildEditor()
    {
        var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 10, 12, 0) };
        header.Controls.Add(new Label { Text = "Mapa de botões do", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        header.Controls.Add(_modeBox);

        // Two side-by-side groups of "physical button -> what the game receives".
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new Padding(12, 6, 12, 0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));

        int rows = (Buttons.Length + 1) / 2;
        for (int index = 0; index < Buttons.Length; index++)
        {
            (PadButton button, string label) = Buttons[index];
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            foreach ((_, string targetLabel) in Buttons) box.Items.Add(targetLabel);
            _targetBoxes[button] = box;

            int column = index < rows ? 0 : 2;
            int row = index < rows ? index : index - rows;
            grid.Controls.Add(new Label { Text = label + "  →", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(3, 7, 3, 0) }, column, row);
            grid.Controls.Add(box, column + 1, row);
        }

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 10, 12, 12) };
        actions.Controls.Add(CreateButton("Salvar e aplicar", Save));
        actions.Controls.Add(CreateButton("Trocar A↔B e X↔Y", () => ShowEntries(ButtonMap.FaceButtonSwap)));
        actions.Controls.Add(CreateButton("Sem troca", () => ShowEntries(new Dictionary<PadButton, PadButton>())));
        actions.Controls.Add(_savedLabel);

        var editor = new Panel { Dock = DockStyle.Fill };
        editor.Controls.Add(actions);
        editor.Controls.Add(grid);
        editor.Controls.Add(header);
        return editor;
    }

    private static Button CreateButton(string text, Action onClick)
    {
        var button = new Button { Text = text, AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void ShowEntries(IReadOnlyDictionary<PadButton, PadButton> entries)
    {
        _savedLabel.Text = "";
        foreach ((PadButton button, ComboBox box) in _targetBoxes)
        {
            PadButton target = entries.GetValueOrDefault(button, button);
            box.SelectedIndex = Array.FindIndex(Buttons, entry => entry.Button == target);
        }
    }

    private void Save()
    {
        var entries = new Dictionary<PadButton, PadButton>();
        foreach ((PadButton button, ComboBox box) in _targetBoxes)
            entries[button] = Buttons[box.SelectedIndex].Button;

        try
        {
            ButtonMap.Save(SelectedKind, entries);
            _remapper.ReloadMap();
            _savedLabel.Text = "Salvo";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Não foi possível salvar o mapa de botões.\n{exception.Message}", "PadSwap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RefreshLiveView()
    {
        PadKind? kind = _remapper.CurrentKind;
        if (kind == null)
        {
            _padView.Show(default);
            _liveLabel.Text = "Nenhum controle conectado";
            return;
        }

        // Start the editor on the mode the controller is actually in, unless the user already picked one.
        if (!_modeChosen)
        {
            _modeChosen = true;
            _modeBox.SelectedIndex = (int)kind.Value;
        }

        (PadState physical, ushort virtualButtons) = _remapper.Snapshot;
        _padView.Show(physical with { Buttons = virtualButtons });

        string mode = kind == PadKind.Xbox ? "Xbox" : "PlayStation";
        _liveLabel.Text = physical.Buttons == 0
            ? $"Controle em modo {mode}. Aperte um botão para testar."
            : $"Controle envia: {Describe(physical.Buttons)}     Jogo recebe: {Describe(virtualButtons)}";
    }

    private static string Describe(ushort buttons) =>
        string.Join(" + ", Buttons.Where(entry => ((PadButton)buttons & entry.Button) != 0).Select(entry => entry.Label));

    protected override void Dispose(bool disposing)
    {
        if (disposing) _refreshTimer.Dispose();
        base.Dispose(disposing);
    }
}
