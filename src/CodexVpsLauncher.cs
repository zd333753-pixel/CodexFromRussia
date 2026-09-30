using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { Application.Run(new MainForm(LauncherSettings.Load())); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Настройка подключения", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }
}

internal sealed class MainForm : Form
{
    private readonly string ServerIp;
    private readonly int ServerPort;
    private readonly LauncherSettings settings;
    private const int ProxyPort = 18080;
    private const int HttpProxyPort = 18081;

    private readonly Label statusLabel;
    private readonly Button startButton;
    private readonly Button testButton;
    private readonly Button stopButton;
    private readonly Button browserButton;
    private readonly TextBox browserUrl;
    private readonly System.Windows.Forms.Timer monitorTimer;

    private Process tunnelProcess;
    private Process codexProcess;
    private bool browserOpened;
    private HttpTunnelProxy httpProxy;

    public MainForm(LauncherSettings settings)
    {
        this.settings = settings;
        ServerIp = settings.ServerIp;
        ServerPort = settings.ServerPort;
        Text = "Codex через VPS";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 305);
        MinimumSize = new Size(536, 344);
        MaximizeBox = false;
        Font = new Font("Segoe UI", 10F);

        var title = new Label();
        title.Text = "Персональный туннель для Codex";
        title.Font = new Font("Segoe UI Semibold", 15F, FontStyle.Bold);
        title.AutoSize = true;
        title.Location = new Point(22, 18);
        Controls.Add(title);

        var description = new Label();
        description.Text = "Codex и отдельное окно браузера используют один туннель.\r\nОбычные окна браузера работают как раньше.";
        description.AutoSize = true;
        description.Location = new Point(24, 57);
        Controls.Add(description);

        statusLabel = new Label();
        statusLabel.Text = "Туннель выключен";
        statusLabel.AutoSize = false;
        statusLabel.Size = new Size(470, 25);
        statusLabel.Location = new Point(24, 105);
        statusLabel.ForeColor = Color.FromArgb(150, 45, 45);
        Controls.Add(statusLabel);

        startButton = new Button();
        startButton.Text = "Запустить Codex";
        startButton.Size = new Size(160, 38);
        startButton.Location = new Point(24, 147);
        startButton.Click += StartButtonClick;
        Controls.Add(startButton);

        testButton = new Button();
        testButton.Text = "Проверить туннель";
        testButton.Size = new Size(160, 38);
        testButton.Location = new Point(192, 147);
        testButton.Click += TestButtonClick;
        Controls.Add(testButton);

        stopButton = new Button();
        stopButton.Text = "Остановить";
        stopButton.Size = new Size(130, 38);
        stopButton.Location = new Point(360, 147);
        stopButton.Enabled = false;
        stopButton.Click += delegate { StopEverything(); };
        Controls.Add(stopButton);

        var urlLabel = new Label();
        urlLabel.Text = "Адрес сайта или ссылка авторизации из Codex:";
        urlLabel.Location = new Point(24, 195);
        urlLabel.AutoSize = true;
        Controls.Add(urlLabel);
        browserUrl = new TextBox();
        browserUrl.Text = "https://chatgpt.com/auth/login";
        browserUrl.Location = new Point(24, 219);
        browserUrl.Size = new Size(466, 27);
        Controls.Add(browserUrl);
        browserButton = new Button();
        browserButton.Text = "Открыть браузер через VPS";
        browserButton.Location = new Point(24, 254);
        browserButton.Size = new Size(280, 35);
        browserButton.Click += BrowserButtonClick;
        Controls.Add(browserButton);

        monitorTimer = new System.Windows.Forms.Timer();
        monitorTimer.Interval = 1500;
        monitorTimer.Tick += MonitorTimerTick;
        monitorTimer.Start();

        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (!startButton.Enabled) { e.Cancel = true; return; }
            StopEverything();
        };
    }

    private void BrowserButtonClick(object sender, EventArgs e)
    {
        Uri address;
        if (!Uri.TryCreate(browserUrl.Text.Trim(), UriKind.Absolute, out address) ||
            (address.Scheme != "https" && !(address.Scheme == "http" && address.IsLoopback)))
        {
            MessageBox.Show("Введите HTTPS-адрес сайта или ссылку авторизации из Codex.");
            return;
        }
        SetBusy(true);
        try
        {
            string browser = FindBrowserPath();
            StartTunnel();
            if (VerifyProxy() != ServerIp) throw new InvalidOperationException("Не удалось подтвердить IP VPS.");
            string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexVpsBrowser");
            var info = new ProcessStartInfo(browser);
            info.UseShellExecute = false;
            info.Arguments = "--user-data-dir=\"" + profile + "\" --new-window --no-first-run --no-default-browser-check" +
                " --proxy-server=\"socks5://127.0.0.1:" + ProxyPort + "\" --proxy-bypass-list=\"localhost;127.0.0.1;[::1]\"" +
                " --disable-quic \"" + address.AbsoluteUri + "\"";
            using (Process process = Process.Start(info)) { }
            browserOpened = true;
            SetStatus("Браузер и Codex используют VPS " + ServerIp, true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ошибка запуска браузера", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { SetBusy(false); }
    }

    private static string FindBrowserPath()
    {
        string[] candidates = {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
        };
        foreach (string candidate in candidates) if (File.Exists(candidate)) return candidate;
        throw new FileNotFoundException("Не найден Chrome или Microsoft Edge.");
    }

    private void StartButtonClick(object sender, EventArgs e)
    {
        if (Process.GetProcessesByName("ChatGPT").Length > 0)
        {
            MessageBox.Show(
                "Сначала полностью закройте текущее приложение Codex, затем снова нажмите «Запустить Codex».\n\nОткрытые задачи сохранятся.",
                "Codex уже запущен",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        SetBusy(true);
        try
        {
            StartTunnel();
            string ip = VerifyProxy();
            if (!String.Equals(ip, ServerIp, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Проверка вернула неожиданный IP: " + ip);
            }

            string codexPath = FindCodexPath();
            if (String.IsNullOrWhiteSpace(codexPath) || !File.Exists(codexPath))
            {
                throw new FileNotFoundException("Приложение Codex не найдено. Переустановите Codex из Microsoft Store.");
            }

            var startInfo = new ProcessStartInfo();
            startInfo.FileName = codexPath;
            startInfo.Arguments = "--proxy-server=\"socks5://127.0.0.1:" + ProxyPort + "\" --proxy-bypass-list=\"localhost;127.0.0.1\" codex://launch";
            startInfo.UseShellExecute = false;
            if (httpProxy == null) httpProxy = new HttpTunnelProxy(HttpProxyPort, ProxyPort);
            string httpProxyUrl = "http://127.0.0.1:" + HttpProxyPort;
            startInfo.EnvironmentVariables["ALL_PROXY"] = httpProxyUrl;
            startInfo.EnvironmentVariables["all_proxy"] = httpProxyUrl;
            startInfo.EnvironmentVariables["HTTPS_PROXY"] = httpProxyUrl;
            startInfo.EnvironmentVariables["https_proxy"] = httpProxyUrl;
            startInfo.EnvironmentVariables["HTTP_PROXY"] = httpProxyUrl;
            startInfo.EnvironmentVariables["http_proxy"] = httpProxyUrl;
            startInfo.EnvironmentVariables["NO_PROXY"] = "localhost,127.0.0.1";
            startInfo.EnvironmentVariables["no_proxy"] = "localhost,127.0.0.1";

            codexProcess = Process.Start(startInfo);
            SetStatus("Codex запущен через VPS. IP: " + ip, true);
            WindowState = FormWindowState.Minimized;
        }
        catch (Exception ex)
        {
            StopTunnel();
            SetStatus("Ошибка запуска", false);
            MessageBox.Show(ex.Message, "Не удалось запустить", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void TestButtonClick(object sender, EventArgs e)
    {
        SetBusy(true);
        try
        {
            StartTunnel();
            string ip = VerifyProxy();
            bool correct = String.Equals(ip, ServerIp, StringComparison.OrdinalIgnoreCase);
            SetStatus(correct ? "Туннель работает. IP: " + ip : "Неожиданный IP: " + ip, correct);
            if (!correct)
            {
                MessageBox.Show("Ожидался IP VPS " + ServerIp + ", получен " + ip, "Ошибка проверки", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            StopTunnel();
            SetStatus("Туннель не работает", false);
            MessageBox.Show(ex.Message, "Ошибка проверки", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void StartTunnel()
    {
        if (tunnelProcess != null && !tunnelProcess.HasExited && IsPortOpen(ProxyPort, 300))
        {
            return;
        }

        if (IsPortOpen(ProxyPort, 300))
        {
            throw new InvalidOperationException("Локальный порт " + ProxyPort + " уже занят другой программой.");
        }

        string keyPath = settings.KeyPath;
        if (!File.Exists(keyPath))
        {
            throw new FileNotFoundException("Не найден SSH-ключ: " + keyPath);
        }

        string sshPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe");
        if (!File.Exists(sshPath))
        {
            sshPath = "ssh.exe";
        }

        var startInfo = new ProcessStartInfo();
        startInfo.FileName = sshPath;
        startInfo.Arguments =
            "-N -D 127.0.0.1:" + ProxyPort +
            " -p " + ServerPort +
            " -i \"" + keyPath + "\"" +
            " -o IdentitiesOnly=yes" +
            " -o BatchMode=yes" +
            " -o ExitOnForwardFailure=yes" +
            " -o ConnectTimeout=12" +
            " -o ServerAliveInterval=30" +
            " -o ServerAliveCountMax=3" +
            " -o StrictHostKeyChecking=yes" +
            " -o LogLevel=ERROR" +
            " " + settings.Username + "@" + ServerIp;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        startInfo.RedirectStandardError = true;

        tunnelProcess = Process.Start(startInfo);
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            if (tunnelProcess.HasExited)
            {
                string details = tunnelProcess.StandardError.ReadToEnd().Trim();
                string message = "SSH-туннель завершился с кодом " + tunnelProcess.ExitCode + ".";
                if (!String.IsNullOrWhiteSpace(details))
                {
                    message += "\n\n" + details;
                }
                throw new InvalidOperationException(message);
            }
            if (IsPortOpen(ProxyPort, 250))
            {
                stopButton.Enabled = true;
                return;
            }
            Thread.Sleep(150);
        }

        throw new TimeoutException("VPS не ответил за 15 секунд.");
    }

    private static string VerifyProxy()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.FileName = "curl.exe";
        startInfo.Arguments = "--silent --show-error --fail --max-time 20 --proxy socks5h://127.0.0.1:" + ProxyPort + " https://api.ipify.org";
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using (Process process = Process.Start(startInfo))
        {
            string output = process.StandardOutput.ReadToEnd().Trim();
            string error = process.StandardError.ReadToEnd().Trim();
            if (!process.WaitForExit(25000))
            {
                process.Kill();
                throw new TimeoutException("Проверка туннеля превысила время ожидания.");
            }
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("Проверка туннеля не выполнена: " + error);
            }
            return output;
        }
    }

    private static string FindCodexPath()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.FileName = "powershell.exe";
        startInfo.Arguments = "-NoProfile -NonInteractive -Command \"$p=Get-AppxPackage -Name OpenAI.Codex; if($p){Join-Path $p.InstallLocation 'app\\ChatGPT.exe'}\"";
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using (Process process = Process.Start(startInfo))
        {
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(10000);
            return output;
        }
    }

    private static bool IsPortOpen(int port, int timeoutMilliseconds)
    {
        using (var client = new TcpClient())
        {
            IAsyncResult result = client.BeginConnect("127.0.0.1", port, null, null);
            bool connected = result.AsyncWaitHandle.WaitOne(timeoutMilliseconds);
            if (!connected)
            {
                return false;
            }
            try
            {
                client.EndConnect(result);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private void MonitorTimerTick(object sender, EventArgs e)
    {
        // StartTunnel pumps UI messages while it waits. Do not let this timer
        // dispose and null the same Process object during that operation.
        if (!monitorTimer.Enabled)
        {
            return;
        }

        if (tunnelProcess != null && tunnelProcess.HasExited)
        {
            tunnelProcess.Dispose();
            tunnelProcess = null;
            SetStatus("Соединение с VPS потеряно", false);
            stopButton.Enabled = false;
        }

        if (codexProcess != null && codexProcess.HasExited)
        {
            codexProcess.Dispose();
            codexProcess = null;
            if (!browserOpened) {
                StopTunnel();
                SetStatus("Codex закрыт, туннель выключен", false);
            } else {
                SetStatus("Codex закрыт. Туннель остаётся для браузера.", true);
            }
            WindowState = FormWindowState.Normal;
        }
    }

    private void StopEverything()
    {
        StopTunnel();
        SetStatus("Туннель выключен", false);
    }

    private void StopTunnel()
    {
        if (httpProxy != null) { httpProxy.Dispose(); httpProxy = null; }
        if (tunnelProcess != null)
        {
            try
            {
                if (!tunnelProcess.HasExited)
                {
                    tunnelProcess.Kill();
                    tunnelProcess.WaitForExit(3000);
                }
            }
            catch
            {
            }
            tunnelProcess.Dispose();
            tunnelProcess = null;
        }
        stopButton.Enabled = false;
    }

    private void SetBusy(bool busy)
    {
        monitorTimer.Enabled = !busy;
        startButton.Enabled = !busy;
        testButton.Enabled = !busy;
        browserButton.Enabled = !busy;
        browserUrl.Enabled = !busy;
        stopButton.Enabled = !busy && tunnelProcess != null && !tunnelProcess.HasExited;
        UseWaitCursor = busy;
    }

    private void SetStatus(string text, bool ok)
    {
        statusLabel.Text = text;
        statusLabel.ForeColor = ok ? Color.FromArgb(30, 125, 65) : Color.FromArgb(150, 45, 45);
    }
}
