
package com.example.spotifyremote;

import android.app.Activity;
import android.os.Bundle;
import android.os.Handler;
import android.view.Gravity;
import android.view.View;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.util.Base64;

import java.io.BufferedWriter;
import java.io.OutputStreamWriter;
import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.io.IOException;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.InetSocketAddress;
import java.net.InetAddress;
import java.net.SocketTimeoutException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public class MainActivity extends Activity {

    private static final int PORT = 38210;
    private static final int HEARTBEAT_TIMEOUT = 15000;

    private TextView status;
    private TextView songArtist;
    private TextView songTitle;
    private TextView playbackState;

    private ImageView albumArt;
    private Button playButton;

    private volatile ServerSocket serverSocket;
    private volatile Socket clientSocket;
    private volatile BufferedWriter writer;

    private final Object clientLock = new Object();

    private final Handler handler = new Handler();

    private final ExecutorService commandExecutor =
            Executors.newSingleThreadExecutor();

    private volatile boolean running = true;
    private volatile boolean destroyed = false;

    private Bitmap currentAlbumArt;

    @Override
    public void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        getWindow().setFlags(
                WindowManager.LayoutParams.FLAG_FULLSCREEN,
                WindowManager.LayoutParams.FLAG_FULLSCREEN);

        getWindow().addFlags(
                WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

        buildInterface();
        startServer();
    }

    private int dp(int value) {
        float density = getResources().getDisplayMetrics().density;
        return (int) (value * density + 0.5f);
    }

    private void buildInterface() {
        LinearLayout layout = new LinearLayout(this);
        layout.setOrientation(LinearLayout.VERTICAL);
        layout.setPadding(
                dp(18), dp(10), dp(18), dp(10));
        layout.setBackgroundColor(0xFF101713);

        LinearLayout header = new LinearLayout(this);
        header.setGravity(Gravity.CENTER_VERTICAL);
        header.setOrientation(LinearLayout.HORIZONTAL);

        TextView heading = new TextView(this);
        heading.setText("♪  SPOTIFY REMOTE");
        heading.setTextColor(0xFF1ED760);
        heading.setTextSize(23);
        heading.setTypeface(null, 1);

        header.addView(heading,
                new LinearLayout.LayoutParams(
                        0,
                        LinearLayout.LayoutParams.WRAP_CONTENT,
                        1));

        status = new TextView(this);
        status.setText("Waiting for PC...");
        status.setTextColor(0xFFB5C5B9);
        status.setTextSize(12);
        status.setGravity(Gravity.RIGHT);

        header.addView(status);
        layout.addView(header);

        LinearLayout songCard = new LinearLayout(this);
        songCard.setOrientation(LinearLayout.HORIZONTAL);
        songCard.setGravity(Gravity.CENTER_VERTICAL);
        songCard.setPadding(dp(12), dp(8), dp(12), dp(8));
        songCard.setBackgroundColor(0xFF1D2922);

        albumArt = new ImageView(this);
        albumArt.setBackgroundColor(0xFF26352B);
        albumArt.setScaleType(ImageView.ScaleType.CENTER_CROP);

        LinearLayout.LayoutParams artParams =
                new LinearLayout.LayoutParams(
                        dp(100), dp(100));

        artParams.setMargins(0, 0, dp(12), 0);
        songCard.addView(albumArt, artParams);

        LinearLayout songInfo = new LinearLayout(this);
        songInfo.setOrientation(LinearLayout.VERTICAL);
        songInfo.setGravity(Gravity.CENTER_VERTICAL);

        TextView nowPlaying = new TextView(this);
        nowPlaying.setText("NOW PLAYING");
        nowPlaying.setTextColor(0xFF1ED760);
        nowPlaying.setTextSize(11);
        nowPlaying.setTypeface(null, 1);

        songInfo.addView(nowPlaying);

        songTitle = new TextView(this);
        songTitle.setText("No song playing");
        songTitle.setTextColor(0xFFFFFFFF);
        songTitle.setTextSize(23);
        songTitle.setTypeface(null, 1);
        songTitle.setSingleLine(true);
        songTitle.setEllipsize(
                android.text.TextUtils.TruncateAt.END);

        songInfo.addView(songTitle);

        songArtist = new TextView(this);
        songArtist.setTextColor(0xFFB5C5B9);
        songArtist.setTextSize(16);
        songArtist.setSingleLine(true);
        songArtist.setEllipsize(
                android.text.TextUtils.TruncateAt.END);

        songInfo.addView(songArtist);

        playbackState = new TextView(this);
        playbackState.setText("Paused");
        playbackState.setTextColor(0xFF1ED760);
        playbackState.setTextSize(12);

        songInfo.addView(playbackState);

        songCard.addView(songInfo,
                new LinearLayout.LayoutParams(
                        0,
                        LinearLayout.LayoutParams.MATCH_PARENT,
                        1));

        LinearLayout.LayoutParams cardParams =
                new LinearLayout.LayoutParams(
                        LinearLayout.LayoutParams.MATCH_PARENT,
                        0, 1.5f);

        cardParams.setMargins(0, dp(10), 0, dp(10));
        layout.addView(songCard, cardParams);

        LinearLayout playbackRow = new LinearLayout(this);
        playbackRow.setOrientation(LinearLayout.HORIZONTAL);

        addButton(playbackRow, "◀|", "PREV");

        playButton = addButton(
                playbackRow, "▶  PLAY", "PLAYPAUSE");

        addButton(playbackRow, "|▶", "NEXT");

        LinearLayout.LayoutParams playbackParams =
                new LinearLayout.LayoutParams(
                        LinearLayout.LayoutParams.MATCH_PARENT,
                        0, 1);

        playbackParams.setMargins(0, 0, 0, dp(8));
        layout.addView(playbackRow, playbackParams);

        LinearLayout volumeRow = new LinearLayout(this);
        volumeRow.setOrientation(LinearLayout.HORIZONTAL);

        addButton(volumeRow, "−  VOLUME", "VOLDOWN");
        addButton(volumeRow, "+  VOLUME", "VOLUP");

        layout.addView(volumeRow,
                new LinearLayout.LayoutParams(
                        LinearLayout.LayoutParams.MATCH_PARENT,
                        0, 1));

        setContentView(layout);
    }

    private Button addButton(
            LinearLayout layout,
            String label,
            final String command) {

        Button button = new Button(this);
        button.setText(label);
        button.setTextSize(16);
        button.setTextColor(0xFFFFFFFF);
        button.setAllCaps(false);
        button.setTypeface(null, 1);
        button.setGravity(Gravity.CENTER);
        button.setPadding(4, 4, 4, 4);

        button.setBackgroundColor(
                command.equals("PLAYPAUSE")
                        ? 0xFF1ED760
                        : 0xFF26352B);

        if (command.equals("PLAYPAUSE")) {
            button.setTextColor(0xFF101713);
        }

        LinearLayout.LayoutParams params =
                new LinearLayout.LayoutParams(
                        0,
                        LinearLayout.LayoutParams.MATCH_PARENT,
                        1);

        params.setMargins(dp(4), dp(2), dp(4), dp(2));
        layout.addView(button, params);

        button.setOnClickListener(
                new View.OnClickListener() {
                    @Override
                    public void onClick(View view) {
                        sendCommand(command);
                    }
                });

        return button;
    }

    private void startServer() {
        Thread serverThread = new Thread(
                new Runnable() {
                    @Override
                    public void run() {
                        while (running) {
                            ServerSocket localServer = null;

                            try {
                                localServer = new ServerSocket();

                                localServer.setReuseAddress(true);

                                localServer.bind(
                                        new InetSocketAddress(
                                                InetAddress.getByName(
                                                        "127.0.0.1"),
                                                PORT));

                                serverSocket = localServer;

                                updateStatus("Waiting for PC...");

                                while (running) {
                                    Socket socket = null;

                                    try {
                                        socket = localServer.accept();

                                        if (!running) {
                                            socket.close();
                                            break;
                                        }

                                        closeClient();

                                        socket.setSoTimeout(
                                                HEARTBEAT_TIMEOUT);

                                        BufferedWriter newWriter =
                                                new BufferedWriter(
                                                        new OutputStreamWriter(
                                                                socket.getOutputStream(),
                                                                "UTF-8"));

                                        synchronized (clientLock) {
                                            clientSocket = socket;
                                            writer = newWriter;
                                        }

                                        updateStatus("PC connected!");

                                        BufferedReader reader =
                                                new BufferedReader(
                                                        new InputStreamReader(
                                                                socket.getInputStream(),
                                                                "UTF-8"));

                                        String message;

                                        while (running &&
                                                (message = reader.readLine()) != null) {
                                            handleIncomingMessage(message);
                                        }

                                    } catch (SocketTimeoutException e) {
                                        updateStatus(
                                                "Connection timed out");

                                    } catch (Exception e) {
                                        if (running) {
                                            updateStatus(
                                                    "Connection interrupted");
                                        }
                                    } finally {
                                        closeClient();

                                        if (running) {
                                            updateStatus(
                                                    "Waiting for PC...");
                                        }
                                    }
                                }

                            } catch (Exception e) {
                                if (running) {
                                    updateStatus("Server restarting");
                                }
                            } finally {
                                if (serverSocket == localServer) {
                                    serverSocket = null;
                                }

                                if (localServer != null) {
                                    try {
                                        localServer.close();
                                    } catch (Exception ignored) {
                                    }
                                }
                            }

                            if (running) {
                                try {
                                    Thread.sleep(500);
                                } catch (InterruptedException e) {
                                    Thread.currentThread().interrupt();
                                    break;
                                }
                            }
                        }
                    }
                });

        serverThread.setName("SpotifyRemoteServer");
        serverThread.start();
    }

    private void handleIncomingMessage(String message) {
        if (message == null)
            return;

        if (message.equals("PING")) {
            sendLine("PONG");
            return;
        }

        if (message.startsWith("SONG|")) {
            String[] parts = message.split("\\|", 4);

            if (parts.length != 4)
                return;

            final String title = parts[1];
            final String artist = parts[2];
            final boolean playing =
                    parts[3].equals("1");

            handler.post(new Runnable() {
                @Override
                public void run() {
                    if (destroyed)
                        return;

                    if (songTitle != null)
                        songTitle.setText(
                                title.length() == 0
                                        ? "No song playing"
                                        : title);

                    if (songArtist != null)
                        songArtist.setText(artist);

                    if (playbackState != null) {
                        playbackState.setText(
                                playing ? "Playing" : "Paused");
                    }

                    if (playButton != null) {
                        playButton.setText(
                                playing
                                        ? "❚❚  PAUSE"
                                        : "▶  PLAY");
                    }
                }
            });

            return;
        }

        if (message.startsWith("ART|")) {
            int separator = message.indexOf('|');

            if (separator < 0)
                return;

            String encoded =
                    message.substring(separator + 1);

            decodeAlbumArt(encoded);
        }
    }

    private void decodeAlbumArt(final String encoded) {
        final Bitmap decoded;

        if (encoded.length() == 0) {
            decoded = null;
        } else {
            Bitmap temporary = null;

            try {
                byte[] data = Base64.decode(
                        encoded, Base64.DEFAULT);

                BitmapFactory.Options bounds =
                        new BitmapFactory.Options();

                bounds.inJustDecodeBounds = true;

                BitmapFactory.decodeByteArray(
                        data, 0, data.length, bounds);

                if (bounds.outWidth <= 0 ||
                        bounds.outHeight <= 0) {
                    return;
                }

                int sample = 1;

                while (bounds.outWidth / sample > 300 ||
                        bounds.outHeight / sample > 300) {
                    sample *= 2;
                }

                BitmapFactory.Options options =
                        new BitmapFactory.Options();

                options.inSampleSize = sample;
                options.inPreferredConfig =
                        Bitmap.Config.RGB_565;

                temporary = BitmapFactory.decodeByteArray(
                        data, 0, data.length, options);

            } catch (OutOfMemoryError e) {
                if (temporary != null) {
                    temporary.recycle();
                }

                return;
            } catch (Exception e) {
                return;
            }

            decoded = temporary;
        }

        handler.post(new Runnable() {
            @Override
            public void run() {
                if (destroyed) {
                    if (decoded != null &&
                            !decoded.isRecycled()) {
                        decoded.recycle();
                    }
                    return;
                }

                if (albumArt == null) {
                    if (decoded != null &&
                            !decoded.isRecycled()) {
                        decoded.recycle();
                    }
                    return;
                }

                if (currentAlbumArt != null &&
                        !currentAlbumArt.isRecycled()) {
                    currentAlbumArt.recycle();
                }

                currentAlbumArt = decoded;

                if (decoded == null) {
                    albumArt.setImageDrawable(null);
                    albumArt.setBackgroundColor(0xFF26352B);
                } else {
                    albumArt.setImageBitmap(decoded);
                }
            }
        });
    }

    private void sendCommand(final String command) {
        if (!running || destroyed)
            return;

        try {
            commandExecutor.execute(new Runnable() {
                @Override
                public void run() {
                    if (!running || destroyed)
                        return;

                    if (!sendLine(command)) {
                        updateStatus("PC not connected!");
                    } else {
                        updateStatus("Sent: " + command);
                    }
                }
            });
        } catch (Exception e) {
            updateStatus("Connection unavailable");
        }
    }

    private boolean sendLine(String message) {
        synchronized (clientLock) {
            if (writer == null ||
                    clientSocket == null ||
                    clientSocket.isClosed()) {
                return false;
            }

            try {
                writer.write(message);
                writer.newLine();
                writer.flush();
                return true;
            } catch (Exception e) {
                return false;
            }
        }
    }

    private void updateStatus(final String message) {
        if (destroyed)
            return;

        handler.post(new Runnable() {
            @Override
            public void run() {
                if (destroyed)
                    return;

                if (status != null) {
                    status.setText(message);
                }
            }
        });
    }

    private void closeClient() {
        Socket oldSocket;
        BufferedWriter oldWriter;

        synchronized (clientLock) {
            oldSocket = clientSocket;
            oldWriter = writer;

            clientSocket = null;
            writer = null;
        }

        if (oldWriter != null) {
            try {
                oldWriter.close();
            } catch (Exception ignored) {
            }
        }

        if (oldSocket != null) {
            try {
                oldSocket.close();
            } catch (Exception ignored) {
            }
        }
    }

    @Override
    protected void onDestroy() {
        destroyed = true;
        running = false;

        handler.removeCallbacksAndMessages(null);

        closeClient();

        ServerSocket oldServer = serverSocket;
        serverSocket = null;

        if (oldServer != null) {
            try {
                oldServer.close();
            } catch (Exception ignored) {
            }
        }

        commandExecutor.shutdownNow();

        if (currentAlbumArt != null &&
                !currentAlbumArt.isRecycled()) {
            currentAlbumArt.recycle();
            currentAlbumArt = null;
        }

        super.onDestroy();
    }
}
