using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace ClaudeRcManager;

sealed class MainForm : Form
{
    const string AppTitle = "Claude RC Manager";

    readonly AppConfig _cfg;
    readonly JobObject _job = new();
    readonly List<SessionRunner> _runners = new();

    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, CheckBoxes = true,
        HideSelection = false, MultiSelect = true, BorderStyle = BorderStyle.None,
    };
    readonly NotifyIcon _tray = new();
    readonly CheckBox _autoStart = new() { Text = "Windows 로그인 시 자동 실행", AutoSize = true };
    readonly CheckBox _autoRestart = new() { Text = "세션이 죽으면 자동 재시작", AutoSize = true };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    readonly Label _loginLabel = new() { AutoSize = true, Margin = new Padding(24, 4, 0, 0) };
    bool? _loggedIn;            // null = 아직 모름/확인 실패
    bool _pendingAutoStart;     // 로그인 확인이 끝나면 체크된 세션을 시작해야 함
    bool _checkingLogin;
    bool _loginPromptOpen;
    Process? _loginProc;

    bool _startHidden;
    bool _reallyExit;
    bool _populating;
    bool _trayHintShown;

    public MainForm(bool startHidden)
    {
        _startHidden = startHidden;
        _cfg = Store.Load();

        Text = AppTitle;
        Icon = LoadIcon(new Size(32, 32));
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(980, 420);
        MinimumSize = new Size(640, 260);
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();

        foreach (var s in _cfg.Sessions) AddRunner(s);
        RebuildList();

        try { AutoStart.FixPathIfMoved(); } catch { }
        _populating = true;
        _autoStart.Checked = SafeAutoStartEnabled();
        _autoRestart.Checked = _cfg.AutoRestart;
        _populating = false;

        _timer.Tick += (_, _) => RefreshRows();
        _timer.Start();

        // 프로그램이 뜨면(= 재부팅 후 로그인하면) 먼저 claude 로그인 상태를 확인하고,
        // 로그인되어 있을 때만 체크된 세션을 모두 시작한다.
        _pendingAutoStart = true;
        _ = CheckLoginAsync(prompt: true);

        Shown += (_, _) =>
        {
            if (_cfg.Sessions.Count == 0 && _loggedIn != false) ImportExisting(silentIfNone: true);
        };
    }

    // ---------- 로그인 확인 ----------

    /// <summary>
    /// claude 로그인 여부를 확인한다. 로그인되어 있으면(또는 확인 자체가 실패하면) 보류해 둔 자동 시작을 진행하고,
    /// 로그인이 안 되어 있으면 세션을 띄우지 않고 로그인하도록 안내한다.
    /// </summary>
    async Task CheckLoginAsync(bool prompt)
    {
        if (_checkingLogin) return;
        _checkingLogin = true;
        SetLoginLabel("Claude 로그인 확인 중…", SystemColors.GrayText);
        AuthStatus st;
        try { st = await Task.Run(() => AuthCheck.Check(_cfg)); }
        finally { _checkingLogin = false; }
        if (IsDisposed || _reallyExit) return;

        _loggedIn = st.LoggedIn;
        if (st.LoggedIn == false)
        {
            SetLoginLabel("Claude 로그인 필요", Color.FromArgb(190, 40, 40));
            if (prompt) PromptLogin();
            return;
        }

        if (st.LoggedIn == true)
            SetLoginLabel("Claude 로그인됨" + (st.Detail.Length > 0 ? $" ({st.Detail})" : ""), Color.FromArgb(0, 128, 60));
        else
            SetLoginLabel("로그인 확인 실패: " + st.Detail, Color.FromArgb(160, 110, 0));

        if (_pendingAutoStart)
        {
            _pendingAutoStart = false;
            StartSessions(_runners.Where(r => r.Config.Enabled).ToList(), interactive: false);
        }
    }

    void SetLoginLabel(string text, Color color)
    {
        _loginLabel.Text = text;
        _loginLabel.ForeColor = color;
    }

    void PromptLogin()
    {
        if (_loginPromptOpen || _loginProc != null) return;
        ShowFromTray();   // 트레이로만 떠 있었더라도 로그인이 필요하면 창을 띄워 알린다
        _loginPromptOpen = true;
        DialogResult answer;
        try
        {
            answer = MessageBox.Show(this,
                "Claude 에 로그인되어 있지 않아 rc 세션을 시작할 수 없습니다.\n\n지금 로그인할까요? (로그인 창이 열립니다)",
                AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        }
        finally { _loginPromptOpen = false; }
        if (answer == DialogResult.Yes) StartLogin();
    }

    void StartLogin()
    {
        if (_loginProc != null) return;
        try
        {
            var p = AuthCheck.StartLogin(_cfg);
            _loginProc = p;
            SetLoginLabel("로그인 창에서 진행 중…", Color.FromArgb(160, 110, 0));
            p.Exited += (_, _) =>
            {
                try
                {
                    BeginInvoke(() =>
                    {
                        _loginProc = null;
                        p.Dispose();
                        // 로그인 창이 닫히면 다시 확인하고, 성공했으면 보류된 세션들을 시작한다.
                        _ = CheckLoginAsync(prompt: false);
                    });
                }
                catch { }
            };
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "로그인 창을 열지 못했습니다: " + ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------- UI 구성 ----------

    void BuildUi()
    {
        _list.Columns.Add("이름", 170);
        _list.Columns.Add("폴더", 300);
        _list.Columns.Add("상태", 230);
        _list.Columns.Add("PID", 60);
        _list.Columns.Add("시작 시각", 120);
        _list.Columns.Add("모드", 80);
        _list.ItemChecked += (_, e) =>
        {
            if (_populating || e.Item.Tag is not SessionRunner r) return;
            r.Config.Enabled = e.Item.Checked;
            Store.Save(_cfg);
        };
        _list.DoubleClick += (_, _) => EditSelected();
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };

        var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 4, 6, 4) };
        ToolStripButton Btn(string text, Action act, string? tip = null)
        {
            var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = tip ?? text };
            b.Click += (_, _) => act();
            tools.Items.Add(b);
            return b;
        }
        Btn("＋ 추가", AddNew);
        Btn("편집", EditSelected);
        Btn("삭제", RemoveSelected);
        tools.Items.Add(new ToolStripSeparator());
        Btn("▶ 시작", () => StartSessions(SelectedRunners(), true));
        Btn("■ 중지", () => SelectedRunners().ForEach(r => r.Stop()));
        Btn("↻ 재시작", RestartSelected);
        tools.Items.Add(new ToolStripSeparator());
        Btn("모두 시작", () => StartSessions(_runners.ToList(), true));
        Btn("모두 중지", () => _runners.ForEach(r => r.Stop()));
        tools.Items.Add(new ToolStripSeparator());
        Btn("로그", ShowLog);
        Btn("웹에서 열기", OpenWeb, "claude.ai/code 에서 이 세션 열기");
        Btn("폴더 열기", OpenFolder);
        tools.Items.Add(new ToolStripSeparator());
        Btn("기존 rc 가져오기", () => ImportExisting(false), "이 PC에서 claude rc 를 쓰던 폴더를 찾아 등록");

        var menu = new ContextMenuStrip();
        menu.Items.Add("시작", null, (_, _) => StartSessions(SelectedRunners(), true));
        menu.Items.Add("중지", null, (_, _) => SelectedRunners().ForEach(r => r.Stop()));
        menu.Items.Add("재시작", null, (_, _) => RestartSelected());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("로그 보기", null, (_, _) => ShowLog());
        menu.Items.Add("웹에서 열기", null, (_, _) => OpenWeb());
        menu.Items.Add("폴더 열기", null, (_, _) => OpenFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("편집…", null, (_, _) => EditSelected());
        menu.Items.Add("삭제", null, (_, _) => RemoveSelected());
        _list.ContextMenuStrip = menu;

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8, 6, 8, 6) };
        var hint = new Label
        {
            Text = "체크된 세션 = 프로그램 시작 시 자동 시작",
            AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(24, 4, 0, 0),
        };
        _autoStart.CheckedChanged += (_, _) =>
        {
            if (_populating) return;
            try { AutoStart.Set(_autoStart.Checked); }
            catch (Exception ex) { MessageBox.Show(this, "자동 실행 설정 실패: " + ex.Message, AppTitle); }
        };
        _autoRestart.CheckedChanged += (_, _) =>
        {
            if (_populating) return;
            _cfg.AutoRestart = _autoRestart.Checked;
            Store.Save(_cfg);
        };
        bottom.Controls.Add(_autoStart);
        bottom.Controls.Add(_autoRestart);
        bottom.Controls.Add(hint);
        bottom.Controls.Add(_loginLabel);
        var recheck = new LinkLabel { Text = "다시 확인", AutoSize = true, Margin = new Padding(8, 4, 0, 0) };
        recheck.LinkClicked += (_, _) => _ = CheckLoginAsync(prompt: true);
        var login = new LinkLabel { Text = "로그인…", AutoSize = true, Margin = new Padding(8, 4, 0, 0) };
        login.LinkClicked += (_, _) => StartLogin();
        bottom.Controls.Add(recheck);
        bottom.Controls.Add(login);

        Controls.Add(_list);
        Controls.Add(tools);
        Controls.Add(bottom);

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("열기", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("모두 시작", null, (_, _) => StartSessions(_runners.ToList(), false));
        trayMenu.Items.Add("모두 중지", null, (_, _) => _runners.ForEach(r => r.Stop()));
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("종료", null, (_, _) => ExitApp());
        _tray.Icon = LoadIcon(SystemInformation.SmallIconSize);   // 트레이 크기(DPI)에 맞는 프레임
        _tray.Text = AppTitle;
        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.Visible = true;
    }

    /// <summary>exe 에 들어 있는 것과 같은 app.ico 에서 원하는 크기의 프레임을 꺼낸다.</summary>
    static Icon LoadIcon(Size size)
    {
        try
        {
            using var s = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico");
            if (s != null) return new Icon(s, size);
        }
        catch { }
        try { if (Environment.ProcessPath is { } exe && Icon.ExtractAssociatedIcon(exe) is { } ico) return ico; } catch { }
        return SystemIcons.Application;
    }

    static bool SafeAutoStartEnabled()
    {
        try { return AutoStart.IsEnabled; } catch { return false; }
    }

    // ---------- 목록 ----------

    SessionRunner AddRunner(SessionConfig s)
    {
        var r = new SessionRunner(s, _cfg, _job);
        r.Changed += () =>
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(RefreshRows); }
            catch { }
        };
        _runners.Add(r);
        return r;
    }

    void RebuildList()
    {
        _populating = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in _runners)
        {
            var item = new ListViewItem(new[] { "", "", "", "", "", "" }) { Tag = r, Checked = r.Config.Enabled };
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _populating = false;
        RefreshRows();
    }

    void RefreshRows()
    {
        int running = 0;
        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag is not SessionRunner r) continue;
            if (r.IsRunning) running++;
            Set(item, 0, r.Config.DisplayName);
            Set(item, 1, r.Config.Directory);
            Set(item, 2, r.StatusText);
            Set(item, 3, r.Pid?.ToString() ?? "");
            Set(item, 4, r.IsRunning && r.StartedAt is { } t ? t.ToString("MM-dd HH:mm:ss") : "");
            Set(item, 5, r.Config.SpawnMode);
            var color = r.State switch
            {
                RunState.Connected => Color.FromArgb(0, 128, 60),
                RunState.Starting => Color.FromArgb(160, 110, 0),
                RunState.Waiting or RunState.Exited => Color.FromArgb(190, 40, 40),
                RunState.External => Color.FromArgb(90, 90, 160),
                _ => SystemColors.GrayText,
            };
            if (item.ForeColor != color) item.ForeColor = color;
        }
        var tip = $"{AppTitle} — 실행 중 {running}/{_runners.Count}";
        if (_tray.Text != tip) _tray.Text = tip;

        static void Set(ListViewItem item, int col, string text)
        {
            if (item.SubItems[col].Text != text) item.SubItems[col].Text = text;
        }
    }

    List<SessionRunner> SelectedRunners() =>
        _list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<SessionRunner>().ToList();

    // ---------- 동작 ----------

    /// <summary>
    /// 세션을 시작한다. 같은 폴더에서 터미널 등으로 이미 rc 가 돌고 있으면 중복 실행하지 않고,
    /// interactive 일 때는 그쪽을 끄고 넘겨받을지 물어본다.
    /// </summary>
    async void StartSessions(List<SessionRunner> targets, bool interactive)
    {
        if (_loggedIn == false)
        {
            // 로그인 없이는 어차피 실패만 반복하므로 띄우지 않고 로그인부터 안내한다.
            PromptLogin();
            return;
        }

        var toStart = new List<SessionRunner>();
        var external = new List<(SessionRunner r, int pid)>();
        foreach (var r in targets)
        {
            if (r.IsActive) continue;
            var pid = BridgePointers.LivePid(r.Config.Directory);
            if (pid is int p) external.Add((r, p)); else toStart.Add(r);
        }

        if (external.Count > 0)
        {
            bool takeOver = false;
            if (interactive)
            {
                var names = string.Join("\n", external.Select(e => $"  • {e.r.Config.DisplayName} (PID {e.pid})"));
                takeOver = MessageBox.Show(this,
                    "다음 폴더에서는 이미 다른 곳(터미널 등)에서 claude rc 가 실행 중입니다.\n\n" + names +
                    "\n\n기존 프로세스를 종료하고 이 프로그램에서 다시 시작할까요?\n(그 세션에서 진행 중인 작업이 있으면 중단됩니다)",
                    AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            }
            foreach (var (r, pid) in external)
            {
                if (!takeOver) { r.MarkExternal(); continue; }
                // 터미널에서 Ctrl+C 를 누른 것처럼 끝내야 그쪽에 붙어 있던 세션들이 전부 넘어온다.
                r.MarkTakingOver();
                await Task.Run(() => ConsoleCtrl.StopGracefully(pid, 20000));
                if (IsDisposed || _reallyExit) return;
                toStart.Add(r);
            }
        }

        foreach (var r in toStart) r.Start();
    }

    async void RestartSelected()
    {
        var sel = SelectedRunners();
        // 정상 종료(세션 목록 저장)가 끝난 뒤에 다시 시작해야 세션들이 전부 다시 붙는다.
        await Task.WhenAll(sel.Select(r => r.StopAsync()));
        if (IsDisposed || _reallyExit) return;
        StartSessions(sel, true);
    }

    void AddNew()
    {
        using var dlg = new EditForm(null);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (IsDuplicate(dlg.Result.Directory, null)) return;
        _cfg.Sessions.Add(dlg.Result);
        Store.Save(_cfg);
        var r = AddRunner(dlg.Result);
        RebuildList();
        StartSessions(new List<SessionRunner> { r }, true);
    }

    void EditSelected()
    {
        var r = SelectedRunners().FirstOrDefault();
        if (r == null) return;
        var before = (r.Config.Directory, r.Config.Name, r.Config.SpawnMode, r.Config.PermissionMode, r.Config.ExtraArgs);
        var copy = new SessionConfig
        {
            Id = r.Config.Id, Name = r.Config.Name, Directory = r.Config.Directory, SpawnMode = r.Config.SpawnMode,
            PermissionMode = r.Config.PermissionMode, ExtraArgs = r.Config.ExtraArgs, Enabled = r.Config.Enabled,
        };
        using var dlg = new EditForm(copy);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (IsDuplicate(copy.Directory, r)) return;

        r.Config.Name = copy.Name; r.Config.Directory = copy.Directory; r.Config.SpawnMode = copy.SpawnMode;
        r.Config.PermissionMode = copy.PermissionMode; r.Config.ExtraArgs = copy.ExtraArgs; r.Config.Enabled = copy.Enabled;
        Store.Save(_cfg);
        RebuildList();

        var after = (r.Config.Directory, r.Config.Name, r.Config.SpawnMode, r.Config.PermissionMode, r.Config.ExtraArgs);
        if (r.IsRunning && before != after &&
            MessageBox.Show(this, "변경 내용을 적용하려면 세션을 재시작해야 합니다. 지금 재시작할까요?", AppTitle,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            foreach (ListViewItem i in _list.Items) i.Selected = ReferenceEquals(i.Tag, r);
            RestartSelected();
        }
    }

    bool IsDuplicate(string dir, SessionRunner? except)
    {
        bool dup = _runners.Any(x => !ReferenceEquals(x, except) &&
            string.Equals(x.Config.Directory.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (dup) MessageBox.Show(this, "이미 등록된 폴더입니다:\n" + dir, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return dup;
    }

    void RemoveSelected()
    {
        var sel = SelectedRunners();
        if (sel.Count == 0) return;
        var names = string.Join(", ", sel.Select(r => r.Config.DisplayName));
        if (MessageBox.Show(this, $"목록에서 삭제할까요? (실행 중이면 중지됩니다)\n\n{names}", AppTitle,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        foreach (var r in sel)
        {
            r.Stop();
            _runners.Remove(r);
            _cfg.Sessions.Remove(r.Config);
        }
        Store.Save(_cfg);
        RebuildList();
    }

    void ImportExisting(bool silentIfNone)
    {
        var known = new HashSet<string>(_runners.Select(r => r.Config.Directory.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
        var found = BridgePointers.Scan().Where(i => !known.Contains(i.Directory.TrimEnd('\\'))).ToList();
        if (found.Count == 0)
        {
            if (!silentIfNone)
                MessageBox.Show(this, "새로 가져올 폴더가 없습니다.", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new ImportForm(found);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Selected.Count == 0) return;

        var added = new List<SessionRunner>();
        foreach (var it in dlg.Selected)
        {
            var s = new SessionConfig { Directory = it.Directory };
            _cfg.Sessions.Add(s);
            added.Add(AddRunner(s));
        }
        Store.Save(_cfg);
        RebuildList();
        StartSessions(added, interactive: true);
    }

    void ShowLog()
    {
        var r = SelectedRunners().FirstOrDefault();
        if (r == null) return;
        new LogForm(r.Config.DisplayName, r.LogPath).Show(this);
    }

    void OpenWeb()
    {
        var r = SelectedRunners().FirstOrDefault();
        var url = r?.SessionUrl ?? r?.EnvironmentUrl ?? "https://claude.ai/code";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    void OpenFolder()
    {
        var r = SelectedRunners().FirstOrDefault();
        if (r == null || !Directory.Exists(r.Config.Directory)) return;
        try { Process.Start(new ProcessStartInfo(r.Config.Directory) { UseShellExecute = true }); } catch { }
    }

    // ---------- 창/트레이/종료 ----------

    public void ShowFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden)
        {
            // --minimized: 창을 띄우지 않고 트레이에서만 시작
            _startHidden = false;
            if (!IsHandleCreated) CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            // X 버튼은 종료가 아니라 트레이로 숨김
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(3000, AppTitle, "트레이에서 계속 실행됩니다. 완전히 끄려면 트레이 아이콘 우클릭 → 종료.", ToolTipIcon.Info);
            }
            return;
        }
        // Windows 종료/재부팅 때도 세션 목록을 저장할 시간을 벌기 위해, 끝날 때까지 종료를 잠깐 붙잡아 둔다.
        bool osShutdown = e.CloseReason == CloseReason.WindowsShutDown;
        if (osShutdown) { try { ShutdownBlockReasonCreate(Handle, "Claude rc 세션 목록을 저장하는 중"); } catch { } }
        Shutdown();
        if (osShutdown) { try { ShutdownBlockReasonDestroy(Handle); } catch { } }
        base.OnFormClosing(e);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string reason);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);

    /// <summary>다른 인스턴스가 `--exit` 로 요청했을 때: 묻지 않고 세션을 정상 종료한 뒤 끝낸다.</summary>
    public void ExitNow()
    {
        _reallyExit = true;
        Close();
    }

    void ExitApp()
    {
        int running = _runners.Count(r => r.IsActive);
        if (running > 0 && MessageBox.Show(
                $"종료하면 실행 중인 rc 세션 {running}개가 모두 중지됩니다. 종료할까요?", AppTitle,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _reallyExit = true;
        Close();
    }

    void Shutdown()
    {
        _timer.Stop();
        // 모든 세션에 Ctrl+C 를 보내고, 각자 세션 목록을 저장하고 끝날 때까지 기다린다 (안 끝나면 강제 종료됨).
        try { Task.WaitAll(_runners.Select(r => r.StopAsync()).ToArray(), 30000); } catch { }
        _tray.Visible = false;
        _tray.Dispose();
        _job.Dispose();   // 남은 자식 프로세스가 있으면 여기서 함께 정리된다
    }
}
