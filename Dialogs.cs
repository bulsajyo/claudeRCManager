using System.Text;

namespace ClaudeRcManager;

/// <summary>세션 추가/편집 창.</summary>
sealed class EditForm : Form
{
    readonly TextBox _dir = new() { Width = 380 };
    readonly TextBox _name = new() { Width = 380, PlaceholderText = "비워 두면 자동 (claude rc 와 동일)" };
    readonly ComboBox _spawn = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    readonly ComboBox _perm = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    readonly TextBox _extra = new() { Width = 380, PlaceholderText = "예: --capacity 8" };
    readonly CheckBox _enabled = new() { Text = "프로그램 시작 시(재부팅 후) 자동으로 시작", AutoSize = true };

    const string PermDefault = "(지정 안 함)";

    public SessionConfig Result { get; }

    public EditForm(SessionConfig? existing)
    {
        Result = existing ?? new SessionConfig();
        Text = existing == null ? "세션 추가" : "세션 편집";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        Font = new Font("Segoe UI", 9f);

        _spawn.Items.AddRange(new object[] { "same-dir", "worktree", "session" });
        _perm.Items.AddRange(new object[] { PermDefault, "default", "acceptEdits", "auto", "plan", "dontAsk", "bypassPermissions" });

        var browse = new Button { Text = "찾아보기…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "claude rc 를 실행할 폴더", UseDescriptionForTitle = true };
            if (Directory.Exists(_dir.Text)) dlg.SelectedPath = _dir.Text;
            if (dlg.ShowDialog(this) == DialogResult.OK) _dir.Text = dlg.SelectedPath;
        };

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill };
        void Row(string label, Control c, Control? extra = null)
        {
            int r = grid.RowCount++;
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 8) }, 0, r);
            c.Anchor = AnchorStyles.Left;
            grid.Controls.Add(c, 1, r);
            if (extra != null) grid.Controls.Add(extra, 2, r);
        }
        Row("폴더", _dir, browse);
        Row("세션 이름", _name);
        Row("Spawn 모드", _spawn);
        Row("권한 모드", _perm);
        Row("추가 인자", _extra);
        Row("", _enabled);

        var ok = new Button { Text = "확인", AutoSize = true, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Accept();
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(grid);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        _dir.Text = Result.Directory;
        _name.Text = Result.Name;
        _spawn.SelectedItem = _spawn.Items.Contains(Result.SpawnMode) ? Result.SpawnMode : "same-dir";
        _perm.SelectedItem = _perm.Items.Contains(Result.PermissionMode) ? Result.PermissionMode : PermDefault;
        _extra.Text = Result.ExtraArgs;
        _enabled.Checked = Result.Enabled;
    }

    void Accept()
    {
        var dir = _dir.Text.Trim().Trim('"');
        if (!Directory.Exists(dir))
        {
            MessageBox.Show(this, "존재하는 폴더를 지정하세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Result.Directory = Path.GetFullPath(dir).TrimEnd('\\', '/') is { Length: > 2 } p ? p : Path.GetFullPath(dir);
        Result.Name = _name.Text.Trim();
        Result.SpawnMode = (string?)_spawn.SelectedItem ?? "same-dir";
        Result.PermissionMode = (string?)_perm.SelectedItem is { } pm && pm != PermDefault ? pm : "";
        Result.ExtraArgs = _extra.Text.Trim();
        Result.Enabled = _enabled.Checked;
        DialogResult = DialogResult.OK;
    }
}

/// <summary>claude 가 남긴 기록에서 rc 를 쓰던 폴더를 찾아 한꺼번에 등록하는 창.</summary>
sealed class ImportForm : Form
{
    readonly CheckedListBox _list = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    readonly List<BridgePointers.Info> _items;

    public List<BridgePointers.Info> Selected { get; } = new();

    public ImportForm(List<BridgePointers.Info> items)
    {
        _items = items;
        Text = "기존 rc 폴더 가져오기";
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(620, 340);
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        Padding = new Padding(12);

        var info = new Label
        {
            Text = "이 PC에서 claude rc 를 실행했던 폴더입니다. 관리할 폴더를 선택하세요.",
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 8),
        };

        foreach (var it in items)
        {
            var label = it.Alive
                ? $"{it.Directory}    — 지금 실행 중 (PID {it.Pid})"
                : $"{it.Directory}    — 마지막 사용 {it.LastUsed:yyyy-MM-dd}";
            // 지금 돌고 있거나 최근 2주 안에 쓴 폴더는 기본 선택
            _list.Items.Add(label, it.Alive || it.LastUsed > DateTime.Now.AddDays(-14));
        }

        var ok = new Button { Text = "가져오기", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(_list);
        Controls.Add(info);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        FormClosing += (_, _) =>
        {
            if (DialogResult != DialogResult.OK) return;
            foreach (int i in _list.CheckedIndices) Selected.Add(_items[i]);
        };
    }
}

/// <summary>세션 로그를 따라가며 보여 주는 창.</summary>
sealed class LogForm : Form
{
    readonly TextBox _box = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
        Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f), BackColor = Color.White,
    };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    readonly string _path;
    long _lastLength = -1;

    public LogForm(string title, string path)
    {
        _path = path;
        Text = "로그 — " + title;
        ClientSize = new Size(900, 520);
        StartPosition = FormStartPosition.CenterParent;
        ShowIcon = false;
        Controls.Add(_box);
        _timer.Tick += (_, _) => Reload();
        Load += (_, _) => { Reload(); _timer.Start(); };
        FormClosed += (_, _) => _timer.Dispose();
    }

    void Reload()
    {
        try
        {
            if (!File.Exists(_path))
            {
                if (_lastLength != 0) { _box.Text = "(아직 로그가 없습니다)"; _lastLength = 0; }
                return;
            }
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length == _lastLength) return;
            _lastLength = fs.Length;

            const int Tail = 200 * 1024;
            bool cut = fs.Length > Tail;
            if (cut) fs.Seek(-Tail, SeekOrigin.End);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            var text = sr.ReadToEnd();
            if (cut) text = text[(text.IndexOf('\n') + 1)..];   // 잘린 첫 줄 버림

            _box.Text = text;
            _box.SelectionStart = _box.TextLength;
            _box.ScrollToCaret();
        }
        catch { }
    }
}
