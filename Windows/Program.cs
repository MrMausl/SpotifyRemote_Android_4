
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Control;
using System.Drawing;
using System.Windows.Forms;

public class Program
{
    private const int Port = 38210;
    private const int RetryDelay = 2000;

    [DllImport("user32.dll")]
    private static extern void keybd_event(
        byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    private const byte VK_MEDIA_PREV_TRACK = 0xB1;
    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const byte VK_VOLUME_DOWN = 0xAE;
    private const byte VK_VOLUME_UP = 0xAF;

    private static volatile bool running = true;

    private static NotifyIcon trayIcon;
    private static ApplicationContext trayContext;

    private static GlobalSystemMediaTransportControlsSessionManager manager;
    private static GlobalSystemMediaTransportControlsSession currentSession;

    private static StreamWriter phoneWriter;
    private static readonly object writerLock = new object();

    private static readonly SemaphoreSlim updateLock =
        new SemaphoreSlim(1, 1);

    [STAThread]
    public static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        CreateTrayIcon();

        Task.Run(async () => await StartRemote());

        trayContext = new ApplicationContext();
        Application.Run(trayContext);


    }


    private static async Task StartRemote()
    {
        // Configure ADB.
        RunAdb("forward --remove tcp:" + Port, out _, out _);

        if (!RunAdb("forward tcp:" + Port + " tcp:" + Port,
            out _, out _))
        {
            ShowTrayMessage("Spotify Remote", "ADB forwarding failed.");
            return;
        }

        try
        {
            manager =
                await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

            manager.CurrentSessionChanged += OnCurrentSessionChanged;

            SetCurrentSession(manager.GetCurrentSession());

            ShowTrayMessage("Spotify Remote", "Remote helper started.");

            while (running)
            {
                try
                {
                    using (TcpClient client = new TcpClient())
                    {
                        client.Connect("127.0.0.1", Port);

                        using (NetworkStream stream = client.GetStream())
                        using (StreamReader reader = new StreamReader(stream))
                        using (StreamWriter writer = new StreamWriter(stream))
                        {
                            writer.AutoFlush = true;

                            lock (writerLock)
                            {
                                phoneWriter = writer;
                            }

                            await SendCurrentSong();

                            try
                            {
                                string command;

                                while (running &&
                                    (command = reader.ReadLine()) != null)
                                {
                                    HandleCommand(command);
                                }
                            }
                            finally
                            {
                                lock (writerLock)
                                {
                                    if (phoneWriter == writer)
                                        phoneWriter = null;
                                }
                            }
                        }
                    }
                }
                catch (SocketException)
                {
                    // Wait for the phone to reconnect.
                }
                catch (IOException)
                {
                    // Connection was interrupted.
                }
                catch (Exception ex)
                {
                    ShowTrayMessage("Spotify Remote", ex.Message);
                }

                if (running)
                    await Task.Delay(RetryDelay);
            }
        }
        catch (Exception ex)
        {
            ShowTrayMessage("Spotify Remote", ex.Message);
        }
        finally
        {
            DetachCurrentSession();

            if (manager != null)
                manager.CurrentSessionChanged -= OnCurrentSessionChanged;

            RunAdb("forward --remove tcp:" + Port, out _, out _);
        }
    }


    

    private static void CreateTrayIcon()
    {
        trayIcon = new NotifyIcon();

        trayIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        trayIcon.Text = "Spotify Remote";
        trayIcon.Visible = true;

        ContextMenuStrip menu = new ContextMenuStrip();

        ToolStripMenuItem title = new ToolStripMenuItem("Spotify Remote");
        title.Enabled = false;

        ToolStripMenuItem exit = new ToolStripMenuItem("Exit");

        exit.Click += (sender, e) =>
        {
            running = false;

            lock (writerLock)
            {
                phoneWriter = null;
            }

            trayIcon.Visible = false;
            trayIcon.Dispose();

            trayContext.ExitThread();
        };

        menu.Items.Add(title);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        trayIcon.ContextMenuStrip = menu;
    }

    private static void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        SetCurrentSession(sender.GetCurrentSession());
    }

    private static void SetCurrentSession(
        GlobalSystemMediaTransportControlsSession session)
    {
        DetachCurrentSession();

        currentSession = session;

        if (currentSession != null)
        {
            currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
        }

        Console.WriteLine("[MEDIA] Active session changed.");

        _ = SendCurrentSong();
    }

    private static void DetachCurrentSession()
    {
        if (currentSession != null)
        {
            currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            currentSession = null;
        }
    }

    private static void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        _ = SendCurrentSong();
    }

    private static async Task SendCurrentSong()
    {
        await updateLock.WaitAsync();

        try
        {
            string title = "";
            string artist = "";

            var session = currentSession;

            if (session != null)
            {
                var media = await session.TryGetMediaPropertiesAsync();

                title = Clean(media.Title);
                artist = Clean(media.Artist);
            }

            string message = "SONG|" + title + "|" + artist;

            lock (writerLock)
            {
                if (phoneWriter != null)
                    phoneWriter.WriteLine(message);
            }

            if (title.Length > 0)
                Console.WriteLine("[SONG] " + title + " - " + artist);
            else
                Console.WriteLine("[SONG] No active song.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[MEDIA] Update error: " + ex.Message);
        }
        finally
        {
            updateLock.Release();
        }
    }

    private static string Clean(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        return value.Replace("|", "/")
                    .Replace("\r", " ")
                    .Replace("\n", " ")
                    .Trim();
    }

    private static void HandleCommand(string command)
    {
        switch (command)
        {
            case "PREV":
                PressMediaKey(VK_MEDIA_PREV_TRACK);
                break;

            case "PLAYPAUSE":
                PressMediaKey(VK_MEDIA_PLAY_PAUSE);
                break;

            case "NEXT":
                PressMediaKey(VK_MEDIA_NEXT_TRACK);
                break;

            case "VOLDOWN":
                PressMediaKey(VK_VOLUME_DOWN);
                break;

            case "VOLUP":
                PressMediaKey(VK_VOLUME_UP);
                break;
        }
    }

    private static void PressMediaKey(byte key)
    {
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static bool RunAdb(
        string arguments, out string output, out string error)
    {
        output = "";
        error = "";

        try
        {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe");
            info.Arguments = arguments;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;

            using (Process process = Process.Start(info))
            {
                Task<string> outputTask =
                    process.StandardOutput.ReadToEndAsync();

                Task<string> errorTask =
                    process.StandardError.ReadToEndAsync();

                process.WaitForExit();

                output = outputTask.Result;
                error = errorTask.Result;

                return process.ExitCode == 0;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void ShowTrayMessage(string title, string message)
    {
        if (trayIcon == null || !trayIcon.Visible)
            return;

        trayIcon.BalloonTipTitle = title;
        trayIcon.BalloonTipText = message;
        trayIcon.BalloonTipIcon = ToolTipIcon.Info;
        trayIcon.ShowBalloonTip(3000);
    }

}
