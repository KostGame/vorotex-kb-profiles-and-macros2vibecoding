using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Vorotex.K15.SleepSweepLab;
using Vorotex.K15.VendorStaticLab;

namespace Vorotex.K15.HidResearchLab;

internal sealed class HidResearchForm : Form
{
    private readonly Label _status = new()
    {
        AutoSize = false,
        ForeColor = Color.FromArgb(188, 207, 235),
        Size = new Size(920, 56)
    };

    private readonly Label _captureStatus = new()
    {
        AutoSize = false,
        ForeColor = Color.FromArgb(180, 205, 235),
        Size = new Size(865, 48),
        Text = "Capture: остановлен"
    };

    private readonly TextBox _targetExe = new()
    {
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.FromArgb(15, 20, 28),
        ForeColor = Color.Gainsboro,
        Size = new Size(682, 28)
    };

    private readonly TextBox _marker = new()
    {
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.FromArgb(15, 20, 28),
        ForeColor = Color.Gainsboro,
        Size = new Size(675, 28),
        PlaceholderText = "метка действия: например, открыл Подсветку / переключил A→B / изменил яркость"
    };

    private readonly Button _startCapture = ActionButton("▶ Start owner capture", 0, 0, 190);
    private readonly Button _markAction = ActionButton("Поставить метку", 0, 0, 160);
    private readonly Button _stopCapture = ActionButton("■ Stop capture", 0, 0, 150);
    private readonly Button _openOutput = ActionButton("Открыть папку", 0, 0, 145);

    private KeyboardSleepCaptureSession? _capture;
    private string? _lastOutputDirectory;

    public HidResearchForm()
    {
        Text = "VOROTEX K15 HID Research Lab · RC2";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(980, 790);
        MinimumSize = new Size(960, 760);
        AutoScroll = true;
        BackColor = Color.FromArgb(13, 17, 23);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9.5f);

        Controls.Add(new Label
        {
            Text = "VOROTEX K15 HID RESEARCH LAB",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 20f),
            ForeColor = Color.White,
            Location = new Point(24, 20)
        });
        Controls.Add(new Label
        {
            Text = "Отдельный research-инструмент · рабочий Status Tray сюда не встроен",
            AutoSize = true,
            ForeColor = Color.FromArgb(145, 175, 225),
            Location = new Point(27, 61)
        });

        var sleepCard = Card("Sleep Sweep", 24, 100, 448, 200);
        sleepCard.Controls.Add(Body(
            "Файловый sweep 1→10 минут. Ничего не пишет в HID. Оставлен для повторных контрольных серий и forensic-сравнений.",
            18, 53, 405, 60));
        var sleep = ActionButton("Открыть Sleep Sweep 1→10", 18, 125, 285);
        sleep.Click += (_, _) => new SleepSweepForm().Show(this);
        sleepCard.Controls.Add(sleep);
        Controls.Add(sleepCard);

        var staticCard = Card("Vendor PE / HID static analysis", 496, 100, 448, 200);
        staticCard.Controls.Add(Body(
            "PE imports, HidD_SetFeature/GetFeature call-sites, sleep/power strings и xrefs. Только чтение файлов.",
            18, 53, 405, 60));
        var analyze = ActionButton("Анализировать штатный VOROTEX", 18, 125, 300);
        analyze.Click += async (_, _) => await AnalyzeVendorAsync(analyze);
        staticCard.Controls.Add(analyze);
        Controls.Add(staticCard);

        var traceCard = Card("Keyboard Sleep UI Trace · owner interaction capture", 24, 324, 920, 340);
        traceCard.Controls.Add(Body(
            "Статически связывает KBSpecialFuncSet/SleepTime с xref-кандидатами, а во время live capture пишет только внешний read-only след: vendor process, safe config deltas, foreground hash и HID presence. Собственных HID feature-запросов нет.",
            18, 49, 875, 55));

        _targetExe.Location = new Point(18, 109);
        _targetExe.Text = VendorPeAnalyzer.FindVendorExecutable() ?? string.Empty;
        traceCard.Controls.Add(_targetExe);
        var browse = ActionButton("Выбрать EXE…", 710, 105, 170);
        browse.Click += (_, _) => BrowseTargetExe();
        traceCard.Controls.Add(browse);

        var staticTrace = ActionButton("Run static trace", 18, 151, 170);
        staticTrace.Click += async (_, _) => await RunStaticTraceAsync(staticTrace);
        traceCard.Controls.Add(staticTrace);

        _startCapture.Location = new Point(199, 151);
        _startCapture.Click += async (_, _) => await StartCaptureAsync();
        traceCard.Controls.Add(_startCapture);

        _stopCapture.Location = new Point(400, 151);
        _stopCapture.Enabled = false;
        _stopCapture.Click += async (_, _) => await StopCaptureAsync();
        traceCard.Controls.Add(_stopCapture);

        _openOutput.Location = new Point(561, 151);
        _openOutput.Enabled = false;
        _openOutput.Click += (_, _) => OpenOutputFolder();
        traceCard.Controls.Add(_openOutput);

        _marker.Location = new Point(18, 207);
        traceCard.Controls.Add(_marker);
        _markAction.Location = new Point(704, 202);
        _markAction.Enabled = false;
        _markAction.Click += (_, _) => MarkOwnerAction();
        traceCard.Controls.Add(_markAction);

        _captureStatus.Location = new Point(18, 252);
        traceCard.Controls.Add(_captureStatus);
        traceCard.Controls.Add(new Label
        {
            Text = "Важно: K15 feature-report read protocol использует SetFeature request, поэтому этот режим намеренно НЕ делает feature queries. Он не добавляет собственные HID события в эксперимент.",
            Location = new Point(18, 296),
            Size = new Size(875, 36),
            ForeColor = Color.FromArgb(244, 198, 106)
        });
        Controls.Add(traceCard);

        _status.Location = new Point(28, 682);
        _status.Text = "Готово к исследованию. Для W909 можно выбрать его установленный главный EXE вручную.";
        Controls.Add(_status);

        Controls.Add(new Label
        {
            Text = "Safety: no HID writes · no feature-query SetFeature · no process injection/debug attach · no EXE patching · no profile switching.",
            Location = new Point(28, 744),
            Size = new Size(915, 28),
            ForeColor = Color.FromArgb(145, 158, 180)
        });

        FormClosed += async (_, _) =>
        {
            if (_capture is not null)
            {
                try { await _capture.DisposeAsync(); }
                catch { }
                _capture = null;
            }
        };
    }

    private void BrowseTargetExe()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Выберите главный EXE VOROTEX / SXS-W909",
            Filter = "Windows executable (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (File.Exists(_targetExe.Text))
            dialog.InitialDirectory = Path.GetDirectoryName(_targetExe.Text);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _targetExe.Text = dialog.FileName;
    }

    private async Task RunStaticTraceAsync(Button button)
    {
        var exe = RequireTargetExe();
        button.Enabled = false;
        _status.Text = "Keyboard Sleep UI Trace: строю exact-token/xref цепочку...";
        try
        {
            var report = await Task.Run(() => KeyboardSleepUiTraceAnalyzer.Analyze(exe));
            var root = NewSessionDirectory("keyboard-sleep-ui-trace");
            WriteStaticTrace(root, report);
            _lastOutputDirectory = root;
            _openOutput.Enabled = true;
            _status.Text =
                $"Static trace готов. tokens={report.TokenOccurrences.Count}; candidates={report.Candidates.Count}; " +
                $"keyboard-resource={(report.KeyboardSpecificResourceFound ? "YES" : "NO")}.\n{root}";
            OpenOutputFolder();
        }
        catch (Exception ex)
        {
            _status.Text = "Static trace не завершён: " + ex.Message;
            MessageBox.Show(ex.Message, "Keyboard Sleep UI Trace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            button.Enabled = true;
        }
    }

    private async Task StartCaptureAsync()
    {
        if (_capture is not null)
            return;
        var exe = RequireTargetExe();
        _startCapture.Enabled = false;
        _status.Text = "Подготавливаю read-only owner capture и статический trace...";
        try
        {
            var root = NewSessionDirectory("keyboard-sleep-owner-capture");
            var report = await Task.Run(() => KeyboardSleepUiTraceAnalyzer.Analyze(exe));
            WriteStaticTrace(root, report);

            var capture = new KeyboardSleepCaptureSession(exe, root);
            capture.StatusChanged += CaptureStatusChanged;
            capture.Start();
            _capture = capture;
            _lastOutputDirectory = root;
            _stopCapture.Enabled = true;
            _markAction.Enabled = true;
            _openOutput.Enabled = true;
            _status.Text = "Capture идёт. Теперь кликай штатный VOROTEX и ставь короткие метки после значимых действий.";
        }
        catch (Exception ex)
        {
            _startCapture.Enabled = true;
            _status.Text = "Capture не стартовал: " + ex.Message;
            MessageBox.Show(ex.Message, "Keyboard Sleep UI Trace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void MarkOwnerAction()
    {
        if (_capture is null)
            return;
        try
        {
            _capture.MarkAction(_marker.Text);
            _marker.Clear();
            _marker.Focus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Owner marker", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private async Task StopCaptureAsync()
    {
        if (_capture is null)
            return;
        var capture = _capture;
        _capture = null;
        _stopCapture.Enabled = false;
        _markAction.Enabled = false;
        try
        {
            capture.StatusChanged -= CaptureStatusChanged;
            await capture.DisposeAsync();
            _status.Text = "Capture остановлен. Пришли всю папку сеанса или хотя бы JSONL + keyboard-sleep-ui-trace.json.";
            _captureStatus.Text = "Capture: остановлен · " + capture.OutputDirectory;
        }
        finally
        {
            _startCapture.Enabled = true;
        }
    }

    private void CaptureStatusChanged(OwnerCaptureStatus status)
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => CaptureStatusChanged(status)));
            return;
        }
        _captureStatus.Text =
            $"Capture: ИДЁТ · vendor process={(status.TargetProcessRunning ? "RUNNING" : "not running")} · " +
            $"K15={(status.K15Present ? "PRESENT" : "not seen")} · markers={status.MarkerCount} · config changes={status.ChangedConfigFiles}";
    }

    private string RequireTargetExe()
    {
        var path = _targetExe.Text.Trim().Trim('"');
        if (!File.Exists(path))
            throw new FileNotFoundException("Выбранный vendor EXE не найден.", path);
        return Path.GetFullPath(path);
    }

    private static string NewSessionDirectory(string kind)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VOROTEX", "K15 HID Research Lab", kind,
            DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteStaticTrace(string root, KeyboardSleepUiTraceReport report)
    {
        var jsonPath = Path.Combine(root, "keyboard-sleep-ui-trace.json");
        var textPath = Path.Combine(root, "keyboard-sleep-ui-trace.txt");
        File.WriteAllText(jsonPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        File.WriteAllText(textPath, KeyboardSleepUiTraceAnalyzer.ToText(report), new UTF8Encoding(false));
    }

    private void OpenOutputFolder()
    {
        if (string.IsNullOrWhiteSpace(_lastOutputDirectory) || !Directory.Exists(_lastOutputDirectory))
            return;
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{_lastOutputDirectory}\"",
            UseShellExecute = true
        });
    }

    private async Task AnalyzeVendorAsync(Button button)
    {
        button.Enabled = false;
        _status.Text = "Статический анализ VOROTEX-K15-PRO.exe...";
        try
        {
            var exe = VendorPeAnalyzer.FindVendorExecutable();
            if (exe is null)
                throw new FileNotFoundException("VOROTEX-K15-PRO.exe не найден в Program Files.");

            var report = await Task.Run(() => VendorPeAnalyzer.Analyze(exe));
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VOROTEX", "K15 HID Research Lab", "vendor-static",
                DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(root);

            var jsonPath = Path.Combine(root, "vendor-static-report.json");
            var textPath = Path.Combine(root, "vendor-static-report.txt");
            File.WriteAllText(jsonPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.WriteAllText(textPath, VendorPeAnalyzer.ToText(report), new UTF8Encoding(false));

            _status.Text =
                $"Готово. HidD_SetFeature sites: {report.SetFeatureCallSites.Count}; " +
                $"GetFeature sites: {report.GetFeatureCallSites.Count}; sleep/power matches: {report.KeywordMatches.Count}.\n" +
                $"Пришли vendor-static-report.json: {jsonPath}";

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{jsonPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _status.Text = "Анализ не завершён: " + ex.Message;
            MessageBox.Show(ex.Message, "VOROTEX K15 HID Research Lab",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            button.Enabled = true;
        }
    }

    private static Panel Card(string title, int x, int y, int width, int height)
    {
        var panel = new Panel
        {
            Location = new Point(x, y),
            Size = new Size(width, height),
            BackColor = Color.FromArgb(22, 27, 34)
        };
        panel.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 13f),
            ForeColor = Color.White,
            Location = new Point(18, 17)
        });
        return panel;
    }

    private static Label Body(string text, int x, int y, int width, int height) => new()
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(width, height),
        ForeColor = Color.FromArgb(188, 200, 218)
    };

    private static Button ActionButton(string text, int x, int y, int width) => new()
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(width, 42),
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(35, 47, 67),
        ForeColor = Color.White,
        Cursor = Cursors.Hand
    };
}
