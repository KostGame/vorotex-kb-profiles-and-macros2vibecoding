namespace Vorotex.K15.Hud;

internal sealed class HudApplicationContext : ApplicationContext
{
    private const int HotkeyToggle = 1;
    private const int HotkeyNextProfile = 2;
    private const int HotkeyBothProfiles = 3;

    private readonly HudConfig _config;
    private readonly HudUserSettings _userSettings;
    private readonly Dictionary<int, HotkeyBinding> _hotkeys;
    private readonly OverlayForm _overlay;
    private readonly HotkeyWindow _hotkeyWindow;
    private readonly NotifyIcon _trayIcon;
    private readonly Icon _trayGlyph;
    private ToolStripMenuItem _sizeMenu = null!;
    private ToolStripMenuItem _positionMenu = null!;
    private string _currentProfileId;

    public HudApplicationContext()
    {
        _config = HudConfig.Load();
        _userSettings = HudUserSettings.Load(_config.Overlay);
        _currentProfileId = _config.Profiles.Any(p => p.Id == _config.DefaultProfile)
            ? _config.DefaultProfile
            : _config.Profiles.First().Id;

        _hotkeys = new Dictionary<int, HotkeyBinding>
        {
            [HotkeyToggle] = HotkeyBinding.ParseOrDefault(_config.Hotkeys.Toggle, "Ctrl+Alt+K"),
            [HotkeyNextProfile] = HotkeyBinding.ParseOrDefault(_config.Hotkeys.CycleProfile, "Ctrl+Alt+P"),
            [HotkeyBothProfiles] = HotkeyBinding.ParseOrDefault(_config.Hotkeys.ShowBoth, "Ctrl+Alt+Shift+K")
        };

        var hint = $"{_hotkeys[HotkeyToggle].Display} показать · {_hotkeys[HotkeyNextProfile].Display} профиль · {_hotkeys[HotkeyBothProfiles].Display} оба";
        _overlay = new OverlayForm(_config.AutoHideMs, hint, _userSettings.ToOverlayOptions());
        _hotkeyWindow = new HotkeyWindow();
        _hotkeyWindow.HotkeyPressed += HandleHotkey;

        _trayGlyph = TrayIconFactory.Create();
        _trayIcon = new NotifyIcon
        {
            Icon = _trayGlyph,
            Text = "VOROTEX K15 HUD",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _trayIcon.DoubleClick += (_, _) => ToggleCurrentProfile();

        RegisterHotkeys();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add($"Показать / скрыть\t{_hotkeys[HotkeyToggle].Display}", null, (_, _) => ToggleCurrentProfile());
        menu.Items.Add($"Следующий профиль\t{_hotkeys[HotkeyNextProfile].Display}", null, (_, _) => CycleProfile());
        menu.Items.Add($"Оба профиля\t{_hotkeys[HotkeyBothProfiles].Display}", null, (_, _) => ShowBoth());
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Показать профиль A", null, (_, _) => ShowProfile("A"));
        menu.Items.Add("Показать профиль B", null, (_, _) => ShowProfile("B"));
        menu.Items.Add(new ToolStripSeparator());

        _sizeMenu = BuildSizeMenu();
        _positionMenu = BuildPositionMenu();
        menu.Items.Add(_sizeMenu);
        menu.Items.Add(_positionMenu);
        menu.Items.Add(new ToolStripSeparator());

        var startupItem = new ToolStripMenuItem("Запускать с Windows")
        {
            Checked = AutoStartManager.IsEnabled(),
            CheckOnClick = true
        };
        startupItem.CheckedChanged += (_, _) =>
        {
            try
            {
                AutoStartManager.SetEnabled(startupItem.Checked);
            }
            catch (Exception ex)
            {
                startupItem.Checked = AutoStartManager.IsEnabled();
                ShowTrayError($"Не удалось изменить автозапуск: {ex.Message}");
            }
        };
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());
        return menu;
    }

    private ToolStripMenuItem BuildSizeMenu()
    {
        var menu = new ToolStripMenuItem("Размер");
        AddSizeChoice(menu, "Очень маленький", "extraSmall");
        AddSizeChoice(menu, "Маленький", "small");
        AddSizeChoice(menu, "Средний", "medium");
        AddSizeChoice(menu, "Большой", "large");
        return menu;
    }

    private ToolStripMenuItem BuildPositionMenu()
    {
        var menu = new ToolStripMenuItem("Расположение");
        AddPositionChoice(menu, "Над курсором", "aboveCursor");
        menu.DropDownItems.Add(new ToolStripSeparator());
        AddPositionChoice(menu, "Левый верхний угол", "topLeft");
        AddPositionChoice(menu, "Правый верхний угол", "topRight");
        AddPositionChoice(menu, "Левый нижний угол", "bottomLeft");
        AddPositionChoice(menu, "Правый нижний угол", "bottomRight");
        return menu;
    }

    private void AddSizeChoice(ToolStripMenuItem parent, string label, string value)
    {
        var canonical = OverlayOptions.NormalizeSize(value);
        var item = new ToolStripMenuItem(label)
        {
            Tag = canonical,
            Checked = _userSettings.Size.Equals(canonical, StringComparison.OrdinalIgnoreCase)
        };
        item.Click += (_, _) =>
        {
            _userSettings.Size = canonical;
            UpdateChoiceChecks(parent, canonical);
            SaveAndApplyOverlaySettings();
        };
        parent.DropDownItems.Add(item);
    }

    private void AddPositionChoice(ToolStripMenuItem parent, string label, string value)
    {
        var canonical = OverlayOptions.NormalizePosition(value);
        var item = new ToolStripMenuItem(label)
        {
            Tag = canonical,
            Checked = _userSettings.Position.Equals(canonical, StringComparison.OrdinalIgnoreCase)
        };
        item.Click += (_, _) =>
        {
            _userSettings.Position = canonical;
            UpdateChoiceChecks(parent, canonical);
            SaveAndApplyOverlaySettings();
        };
        parent.DropDownItems.Add(item);
    }

    private static void UpdateChoiceChecks(ToolStripMenuItem parent, string selected)
    {
        foreach (ToolStripItem child in parent.DropDownItems)
        {
            if (child is not ToolStripMenuItem item || item.Tag is not string value)
                continue;

            item.Checked = value.Equals(selected, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void SaveAndApplyOverlaySettings()
    {
        try
        {
            _userSettings.Save();
        }
        catch (Exception ex)
        {
            ShowTrayError($"Настройка применена, но не сохранена: {ex.Message}");
        }

        _overlay.ApplyPreferences(_userSettings.ToOverlayOptions(), Cursor.Position);
    }

    private void RegisterHotkeys()
    {
        var failures = new List<string>();
        foreach (var pair in _hotkeys)
        {
            var binding = pair.Value;
            if (!NativeMethods.RegisterHotKey(_hotkeyWindow.Handle, pair.Key, binding.Modifiers, (uint)binding.Key))
                failures.Add(binding.Display);
        }

        if (failures.Count > 0)
            ShowTrayError("Не удалось зарегистрировать хоткей: " + string.Join(", ", failures));
    }

    private void HandleHotkey(int id)
    {
        switch (id)
        {
            case HotkeyToggle:
                ToggleCurrentProfile();
                break;
            case HotkeyNextProfile:
                CycleProfile();
                break;
            case HotkeyBothProfiles:
                ShowBoth();
                break;
        }
    }

    private void ToggleCurrentProfile()
    {
        if (_overlay.Visible)
        {
            _overlay.HideOverlay();
            return;
        }

        ShowProfile(_currentProfileId);
    }

    private void CycleProfile()
    {
        var index = _config.Profiles.FindIndex(p => p.Id == _currentProfileId);
        if (index < 0)
            index = 0;
        _currentProfileId = _config.Profiles[(index + 1) % _config.Profiles.Count].Id;
        ShowProfile(_currentProfileId);
    }

    private void ShowProfile(string id)
    {
        var profile = _config.Profiles.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
            return;
        _currentProfileId = profile.Id;
        _overlay.ShowProfiles([profile], Cursor.Position);
    }

    private void ShowBoth()
    {
        _overlay.ShowProfiles(_config.Profiles, Cursor.Position);
    }

    private void ShowTrayError(string message)
    {
        _trayIcon.BalloonTipTitle = "VOROTEX K15 HUD";
        _trayIcon.BalloonTipText = message;
        _trayIcon.BalloonTipIcon = ToolTipIcon.Warning;
        _trayIcon.ShowBalloonTip(5000);
    }

    protected override void ExitThreadCore()
    {
        foreach (var id in _hotkeys.Keys)
            NativeMethods.UnregisterHotKey(_hotkeyWindow.Handle, id);

        _hotkeyWindow.Dispose();
        _overlay.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayGlyph.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    public event Action<int>? HotkeyPressed;

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "VorotexK15HudHotkeyWindow",
            X = -32000,
            Y = -32000,
            Width = 1,
            Height = 1
        });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WmHotkey)
            HotkeyPressed?.Invoke(m.WParam.ToInt32());
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        DestroyHandle();
        GC.SuppressFinalize(this);
    }
}
