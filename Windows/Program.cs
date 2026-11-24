
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Media.Control;
using Windows.Storage.Streams;

public class Program
{
    private const int Port = 38210;
    private const int AdbTimeoutMs = 5000;
    private const int InitialRetrySeconds = 2;
    private const int MaximumRetrySeconds = 15;

    [DllImport("user32.dll")]
    private static extern void keybd_event(
        byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    private const byte VK_MEDIA_PREV_TRACK = 0xB1;
    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const byte VK_VOLUME_DOWN = 0xAE;
    private const byte VK_VOLUME_UP = 0xAF;

    private static readonly CancellationTokenSource shutdown =
        new CancellationTokenSource();

    private static readonly object writerLock = new object();
    private static readonly object sessionLock = new object();
    private static readonly object clientLock = new object();
    private static readonly object balloonLock = new object();

    private static readonly Dictionary<string, DateTime> lastBalloon =
        new Dictionary<string, DateTime>();

    private static NotifyIcon trayIcon;
    private static ApplicationContext trayContext;
    private static Control uiDispatcher;
    private static Mutex singleInstanceMutex;

    private static GlobalSystemMediaTransportControlsSessionManager manager;
    private static GlobalSystemMediaTransportControlsSession currentSession;

    private static StreamWriter phoneWriter;
    private static TcpClient activeClient;

    private static long lastPongTicks;
    private static int connectionGeneration;

    private static int updatePending;
    private static int updateWorkerRunning;

    private static string lastArtSongKey;
    private static string lastArtHash;

    private static GlobalSystemMediaTransportControlsSession lastKnownSession;
    private static string lastKnownTitle = "";
    private static string lastKnownArtist = "";

    private static readonly string AdbPath =
        Path.Combine(AppContext.BaseDirectory,
            "platform-tools", "adb.exe");

    [STAThread]
    public static void Main()
    {
        bool createdNew;

        singleInstanceMutex = new Mutex(
            true, @"Local\SpotifyRemotePC", out createdNew);

        if (!createdNew)
        {
            singleInstanceMutex.Dispose();
            return;
        }

        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            trayContext = new ApplicationContext();

            uiDispatcher = new Control();
            IntPtr unusedHandle = uiDispatcher.Handle;

            CreateTrayIcon();

            Task.Run(() => StartRemote(shutdown.Token));

            Application.Run(trayContext);
        }
        finally
        {
            shutdown.Cancel();
            CloseActiveClient();

            if (singleInstanceMutex != null)
            {
                singleInstanceMutex.ReleaseMutex();
                singleInstanceMutex.Dispose();
            }
        }
    }

    private static async Task StartRemote(CancellationToken token)
    {
        try
        {
            manager =
                await GlobalSystemMediaTransportControlsSessionManager
                    .RequestAsync();

            manager.CurrentSessionChanged += OnCurrentSessionChanged;

            SetCurrentSession(SelectPreferredSession());

            ShowTrayMessage(
                "Spotify Remote", "Remote helper started.");

            int retrySeconds = InitialRetrySeconds;

            while (!token.IsCancellationRequested)
            {
                bool connected = false;

                try
                {
                    if (!File.Exists(AdbPath))
                    {
                        ShowTrayMessage(
                            "Spotify Remote",
                            "adb.exe was not found in platform-tools.");

                        Log("ADB executable missing: " + AdbPath);
                    }
                    else
                    {
                        RunAdb(
                            "forward --remove tcp:" + Port,
                            out _, out _);

                        bool forwarded = RunAdb(
                            "forward tcp:" + Port + " tcp:" + Port,
                            out string adbOutput,
                            out string adbError);

                        if (!forwarded)
                        {
                            Log("ADB forwarding failed: " + adbError);
                            ShowTrayMessage(
                                "Spotify Remote",
                                "ADB forwarding failed. Check USB connection.");
                        }
                        else
                        {
                            using (TcpClient client = new TcpClient())
                            {
                                lock (clientLock)
                                    activeClient = client;

                                await client.ConnectAsync(
                                    "127.0.0.1", Port, token);

                                connected = true;
                                retrySeconds = InitialRetrySeconds;

                                using (NetworkStream stream =
                                    client.GetStream())
                                using (StreamReader reader =
                                    new StreamReader(stream))
                                using (StreamWriter writer =
                                    new StreamWriter(stream))
                                using (CancellationTokenSource connectionCts =
                                    CancellationTokenSource.CreateLinkedTokenSource(token))
                                {
                                    writer.AutoFlush = true;

                                    lock (writerLock)
                                    {
                                        phoneWriter = writer;
                                        connectionGeneration++;
                                        lastArtSongKey = null;
                                        lastArtHash = null;
                                    }

                                    Interlocked.Exchange(
                                        ref lastPongTicks,
                                        DateTime.UtcNow.Ticks);

                                    Log("Phone connected.");

                                    QueueSongUpdate();

                                    Task heartbeat =
                                        MonitorHeartbeatAsync(
                                            client, connectionCts.Token);

                                    try
                                    {
                                        string line;

                                        while (!token.IsCancellationRequested &&
                                            (line = await reader.ReadLineAsync()) != null)
                                        {
                                            line = line.Trim();

                                            if (line == "PONG")
                                            {
                                                Interlocked.Exchange(
                                                    ref lastPongTicks,
                                                    DateTime.UtcNow.Ticks);
                                            }
                                            else
                                            {
                                                HandleCommand(line);
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        connectionCts.Cancel();
                                        client.Close();

                                        lock (writerLock)
                                        {
                                            if (phoneWriter == writer)
                                                phoneWriter = null;
                                        }

                                        try
                                        {
                                            await heartbeat;
                                        }
                                        catch (OperationCanceledException)
                                        {
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log("Connection error: " + ex);
                }
                finally
                {
                    lock (clientLock)
                    {
                        if (activeClient != null &&
                            activeClient.Connected == false)
                        {
                            activeClient = null;
                        }
                    }
                }

                if (token.IsCancellationRequested)
                    break;

                if (connected)
                    retrySeconds = InitialRetrySeconds;

                await Task.Delay(
                    TimeSpan.FromSeconds(retrySeconds), token);

                retrySeconds = Math.Min(
                    retrySeconds * 2, MaximumRetrySeconds);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log("Remote startup error: " + ex);
            ShowTrayMessage("Spotify Remote", "Remote helper error.");
        }
        finally
        {
            DetachCurrentSession();

            if (manager != null)
                manager.CurrentSessionChanged -= OnCurrentSessionChanged;

            CloseActiveClient();

            RunAdb(
                "forward --remove tcp:" + Port,
                out _, out _);

            Log("Remote helper stopped.");
        }
    }

    private static async Task MonitorHeartbeatAsync(
        TcpClient client, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(5), token);

                long last = Interlocked.Read(ref lastPongTicks);

                TimeSpan elapsed =
                    DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc);

                if (elapsed.TotalSeconds > 10)
                {
                    Log("Heartbeat timed out.");
                    client.Close();
                    return;
                }

                SendLine("PING");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log("Heartbeat error: " + ex.Message);
            client.Close();
        }
    }

    private static void CloseActiveClient()
    {
        TcpClient client;

        lock (clientLock)
        {
            client = activeClient;
            activeClient = null;
        }

        if (client != null)
        {
            try { client.Close(); }
            catch { }
        }

        lock (writerLock)
        {
            phoneWriter = null;
        }
    }

    private static void CreateTrayIcon()
    {
        trayIcon = new NotifyIcon();

        trayIcon.Icon =
            Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        trayIcon.Text = "Spotify Remote";
        trayIcon.Visible = true;

        ContextMenuStrip menu = new ContextMenuStrip();

        ToolStripMenuItem title =
            new ToolStripMenuItem("Spotify Remote");

        title.Enabled = false;

        ToolStripMenuItem exit =
            new ToolStripMenuItem("Exit");

        exit.Click += (sender, e) =>
        {
            shutdown.Cancel();
            CloseActiveClient();

            trayIcon.Visible = false;
            trayIcon.Dispose();

            trayContext.ExitThread();
        };

        menu.Items.Add(title);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        trayIcon.ContextMenuStrip = menu;
    }

    private static void ShowTrayMessage(
        string title, string message)
    {
        string key = title + "|" + message;

        lock (balloonLock)
        {
            DateTime previous;

            if (lastBalloon.TryGetValue(key, out previous) &&
                (DateTime.UtcNow - previous).TotalSeconds < 30)
                return;

            lastBalloon[key] = DateTime.UtcNow;
        }

        if (uiDispatcher == null ||
            uiDispatcher.IsDisposed ||
            !uiDispatcher.IsHandleCreated)
            return;

        try
        {
            uiDispatcher.BeginInvoke(new Action(() =>
            {
                if (trayIcon == null || !trayIcon.Visible)
                    return;

                trayIcon.BalloonTipTitle = title;
                trayIcon.BalloonTipText = message;
                trayIcon.BalloonTipIcon = ToolTipIcon.Info;
                trayIcon.ShowBalloonTip(3000);
            }));
        }
        catch (Exception ex)
        {
            Log("Tray notification error: " + ex.Message);
        }
    }

    private static void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        SetCurrentSession(SelectPreferredSession());
    }

    private static GlobalSystemMediaTransportControlsSession
    SelectPreferredSession()
    {
        try
        {
            if (manager != null)
            {
                var sessions = manager.GetSessions();

                GlobalSystemMediaTransportControlsSession spotifyFallback = null;

                foreach (var session in sessions)
                {
                    string id = session.SourceAppUserModelId;

                    if (string.IsNullOrEmpty(id) ||
                        id.IndexOf(
                            "Spotify",
                            StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    try
                    {
                        var playback = session.GetPlaybackInfo();

                        if (playback != null &&
                            playback.PlaybackStatus ==
                            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        {
                            return session;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("Spotify playback check error: " + ex.Message);
                    }

                    if (spotifyFallback == null)
                        spotifyFallback = session;
                }

                if (spotifyFallback != null)
                    return spotifyFallback;

                return manager.GetCurrentSession();
            }
        }
        catch (Exception ex)
        {
            Log("Session selection error: " + ex.Message);
        }

        return null;
    }

    private static void SetCurrentSession(
    GlobalSystemMediaTransportControlsSession session)
    {
        GlobalSystemMediaTransportControlsSession oldSession;

        lock (sessionLock)
        {
            if (ReferenceEquals(currentSession, session))
            {
                QueueSongUpdate();
                return;
            }

            oldSession = currentSession;
            currentSession = session;
        }

        if (oldSession != null)
        {
            oldSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            oldSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }

        if (session != null)
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        }

        Log("Active media session changed.");
        QueueSongUpdate();
    }

    private static void DetachCurrentSession()
    {
        GlobalSystemMediaTransportControlsSession oldSession;

        lock (sessionLock)
        {
            oldSession = currentSession;
            currentSession = null;
        }

        if (oldSession != null)
        {
            oldSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            oldSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
    }

    private static void OnMediaPropertiesChanged(
    GlobalSystemMediaTransportControlsSession sender,
    MediaPropertiesChangedEventArgs args)
    {
        lock (sessionLock)
        {
            if (!ReferenceEquals(currentSession, sender))
                return;
        }

        QueueSongUpdate();
    }

    private static void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        lock (sessionLock)
        {
            if (!ReferenceEquals(currentSession, sender))
                return;
        }

        QueueSongUpdate();
    }

    private static void QueueSongUpdate()
    {
        Interlocked.Exchange(ref updatePending, 1);

        if (Interlocked.CompareExchange(
            ref updateWorkerRunning, 1, 0) == 0)
        {
            _ = ProcessSongUpdatesAsync();
        }
    }

    private static async Task ProcessSongUpdatesAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref updatePending, 0) == 1)
            {
                await SendCurrentSongCore();
            }
        }
        catch (Exception ex)
        {
            Log("Metadata worker error: " + ex);
        }
        finally
        {
            Interlocked.Exchange(ref updateWorkerRunning, 0);

            if (Interlocked.Exchange(ref updatePending, 0) == 1 &&
                Interlocked.CompareExchange(
                    ref updateWorkerRunning, 1, 0) == 0)
            {
                _ = ProcessSongUpdatesAsync();
            }
        }
    }

    private static async Task SendCurrentSongCore()
    {
        lock (writerLock)
        {
            if (phoneWriter == null)
                return;
        }

        GlobalSystemMediaTransportControlsSession session;

        lock (sessionLock)
            session = currentSession;

        // No active media session: tell the phone that nothing is playing.
        if (session == null)
        {
            lock (sessionLock)
            {
                lastKnownSession = null;
                lastKnownTitle = "";
                lastKnownArtist = "";
            }

            SendLine("SONG|||0");

            bool clearArt;

            lock (writerLock)
            {
                clearArt = lastArtSongKey != "";

                lastArtSongKey = "";
                lastArtHash = "";
            }

            if (clearArt)
                SendLine("ART|");

            return;
        }

        GlobalSystemMediaTransportControlsSessionMediaProperties media = null;

        string title = "";
        string artist = "";
        bool playing = false;

        try
        {
            media = await session.TryGetMediaPropertiesAsync();

            if (media != null)
            {
                title = Clean(media.Title);
                artist = Clean(media.Artist);
            }

            var playback = session.GetPlaybackInfo();

            if (playback != null)
            {
                playing =
                    playback.PlaybackStatus ==
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
        }
        catch (Exception ex)
        {
            Log("Media properties error: " + ex.Message);
        }

        // Ignore results belonging to a session that has since changed.
        lock (sessionLock)
        {
            if (!ReferenceEquals(currentSession, session))
                return;

            if (!string.IsNullOrWhiteSpace(title))
            {
                lastKnownSession = session;
                lastKnownTitle = title;
                lastKnownArtist = artist;
            }
            else if (ReferenceEquals(lastKnownSession, session))
            {
                // Keep the last valid song if Windows temporarily
                // returns empty or unavailable metadata.
                title = lastKnownTitle;
                artist = lastKnownArtist;
            }
        }

        // Don't overwrite a valid phone display with empty metadata.
        if (string.IsNullOrWhiteSpace(title))
            return;

        string songKey = title + "|" + artist;

        SendLine(
            "SONG|" + title + "|" + artist + "|" +
            (playing ? "1" : "0"));

        bool shouldSendArt;

        lock (writerLock)
        {
            shouldSendArt =
                phoneWriter != null &&
                lastArtSongKey != songKey;
        }

        if (!shouldSendArt)
            return;

        byte[] art = null;

        if (media != null && media.Thumbnail != null)
        {
            art = await ReadThumbnailAsync(media.Thumbnail);
        }

        // Retry once because Spotify sometimes supplies its thumbnail late.
        if (art == null)
        {
            await Task.Delay(1000);

            lock (sessionLock)
            {
                if (!ReferenceEquals(currentSession, session))
                    return;
            }

            try
            {
                var retryMedia = await session.TryGetMediaPropertiesAsync();

                if (retryMedia != null && retryMedia.Thumbnail != null)
                {
                    art = await ReadThumbnailAsync(retryMedia.Thumbnail);
                }
            }
            catch (Exception ex)
            {
                Log("Thumbnail retry error: " + ex.Message);
            }
        }

        lock (sessionLock)
        {
            if (!ReferenceEquals(currentSession, session))
                return;
        }

        string hash = art == null
            ? ""
            : Convert.ToBase64String(
                SHA256.Create().ComputeHash(art));

        bool sendArt;

        lock (writerLock)
        {
            if (phoneWriter == null)
                return;

            sendArt =
                lastArtSongKey != songKey ||
                lastArtHash != hash;

            lastArtSongKey = songKey;
            lastArtHash = hash;
        }

        if (sendArt)
        {
            string encoded = art == null
                ? ""
                : Convert.ToBase64String(art);

            SendLine("ART|" + encoded);
        }
    }

    private static async Task<byte[]?> ReadThumbnailAsync(
    IRandomAccessStreamReference? thumbnail)
    {
        try
        {
            using (var randomStream = await thumbnail.OpenReadAsync())
            {
                if (randomStream.Size == 0 ||
                    randomStream.Size > 20 * 1024 * 1024)
                    return null;

                using (DataReader reader =
                    new DataReader(randomStream.GetInputStreamAt(0)))
                {
                    uint size = checked((uint)randomStream.Size);

                    await reader.LoadAsync(size);

                    byte[] original = new byte[size];
                    reader.ReadBytes(original);

                    return ResizeAndEncodeJpeg(original);
                }
            }
        }
        catch (Exception ex)
        {
            Log("Thumbnail read error: " + ex.Message);
            return null;
        }
    }

    private static byte[] ResizeAndEncodeJpeg(byte[] original)
    {
        try
        {
            using (MemoryStream input = new MemoryStream(original))
            using (Bitmap source = new Bitmap(input))
            {
                int width = source.Width;
                int height = source.Height;

                double scale = Math.Min(
                    1.0, 300.0 / Math.Max(width, height));

                int newWidth = Math.Max(1, (int)(width * scale));
                int newHeight = Math.Max(1, (int)(height * scale));

                using (Bitmap resized = new Bitmap(
                    newWidth, newHeight,
                    PixelFormat.Format24bppRgb))
                {
                    using (Graphics graphics =
                        Graphics.FromImage(resized))
                    {
                        graphics.InterpolationMode =
                            System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

                        graphics.SmoothingMode =
                            System.Drawing.Drawing2D.SmoothingMode.HighQuality;

                        graphics.DrawImage(
                            source, 0, 0, newWidth, newHeight);
                    }

                    using (MemoryStream output = new MemoryStream())
                    {
                        ImageCodecInfo jpegEncoder =
                            ImageCodecInfo.GetImageEncoders()
                                .FirstOrDefault(
                                    e => e.MimeType == "image/jpeg");

                        if (jpegEncoder == null)
                            return null;

                        using (EncoderParameters parameters =
                            new EncoderParameters(1))
                        {
                            parameters.Param[0] =
                                new EncoderParameter(
                                    System.Drawing.Imaging.Encoder.Quality,
                                    80L);

                            resized.Save(
                                output, jpegEncoder, parameters);
                        }

                        return output.ToArray();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log("Thumbnail encoding error: " + ex.Message);
            return null;
        }
    }

    private static void SendLine(string line)
    {
        lock (writerLock)
        {
            if (phoneWriter == null)
                return;

            try
            {
                phoneWriter.WriteLine(line);
                phoneWriter.Flush();
            }
            catch (Exception ex)
            {
                Log("Socket write error: " + ex.Message);

                try
                {
                    if (activeClient != null)
                        activeClient.Close();
                }
                catch
                {
                }
            }
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

        if (!File.Exists(AdbPath))
        {
            error = "adb.exe not found: " + AdbPath;
            return false;
        }

        try
        {
            ProcessStartInfo info = new ProcessStartInfo();

            info.FileName = AdbPath;
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

                if (!process.WaitForExit(AdbTimeoutMs))
                {
                    try { process.Kill(true); }
                    catch { }

                    error = "ADB command timed out.";
                    Log(error + " " + arguments);
                    return false;
                }

                output = outputTask.Result;
                error = errorTask.Result;

                return process.ExitCode == 0;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log("ADB error: " + ex);
            return false;
        }
    }

    private static void Log(string message)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "SpotifyRemote");

            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, "remote.log");

            File.AppendAllText(
                path,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                " " + message + Environment.NewLine);
        }
        catch
        {
        }
    }
}
