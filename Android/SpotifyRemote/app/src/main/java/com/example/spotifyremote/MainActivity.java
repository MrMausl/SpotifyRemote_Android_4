
package com.example.spotifyremote;

import android.app.Activity;
import android.os.Bundle;
import android.os.Handler;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.io.BufferedWriter;
import java.io.OutputStreamWriter;
import java.net.ServerSocket;
import java.net.Socket;
import java.io.BufferedReader;
import java.io.InputStreamReader;

public class MainActivity extends Activity {

    private static final int PORT = 38210;

    private TextView status;
    private TextView songArtist;
    private TextView songTitle;
    private ServerSocket serverSocket;
    private volatile Socket clientSocket;
    private volatile BufferedWriter writer;

    private final Handler handler = new Handler();
    private volatile boolean running = true;

    @Override
    public void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        getWindow().setFlags(
                android.view.WindowManager.LayoutParams.FLAG_FULLSCREEN,
                android.view.WindowManager.LayoutParams.FLAG_FULLSCREEN
        );

        buildInterface();
        startServer();
    }



    private void buildInterface() {
        LinearLayout layout = new LinearLayout(this);
        layout.setOrientation(LinearLayout.VERTICAL);
        layout.setPadding(18, 10, 18, 10);
        layout.setBackgroundColor(0xFF101713);

        // Header
        LinearLayout header = new LinearLayout(this);
        header.setGravity(Gravity.CENTER_VERTICAL);
        header.setOrientation(LinearLayout.HORIZONTAL);

        TextView heading = new TextView(this);
        heading.setText("♪  SPOTIFY REMOTE");
        heading.setTextColor(0xFF1ED760);
        heading.setTextSize(23);
        heading.setTypeface(null, 1);

        header.addView(heading,
                new LinearLayout.LayoutParams(0,
                        LinearLayout.LayoutParams.WRAP_CONTENT, 1));

        status = new TextView(this);
        status.setText("Waiting for PC...");
        status.setTextColor(0xFFB5C5B9);
        status.setTextSize(12);
        status.setGravity(Gravity.RIGHT);

        header.addView(status);

        layout.addView(header);

        // Song information card
        LinearLayout songCard = new LinearLayout(this);
        songCard.setOrientation(LinearLayout.VERTICAL);
        songCard.setGravity(Gravity.CENTER_VERTICAL);
        songCard.setPadding(12, 5, 12, 5);
        songCard.setBackgroundColor(0xFF1D2922);

        TextView nowPlaying = new TextView(this);
        nowPlaying.setText("NOW PLAYING");
        nowPlaying.setTextColor(0xFF1ED760);
        nowPlaying.setTextSize(11);
        nowPlaying.setTypeface(null, 1);

        songCard.addView(nowPlaying);

        songTitle = new TextView(this);
        songTitle.setText("No song playing");
        songTitle.setTextColor(0xFFFFFFFF);
        songTitle.setTextSize(23);
        songTitle.setTypeface(null, 1);
        songTitle.setSingleLine(true);
        songTitle.setEllipsize(android.text.TextUtils.TruncateAt.END);

        songCard.addView(songTitle);

        songArtist = new TextView(this);
        songArtist.setTextColor(0xFFB5C5B9);
        songArtist.setTextSize(16);
        songArtist.setSingleLine(true);
        songArtist.setEllipsize(android.text.TextUtils.TruncateAt.END);

        songCard.addView(songArtist);

        LinearLayout.LayoutParams cardParams =
                new LinearLayout.LayoutParams(
                        LinearLayout.LayoutParams.MATCH_PARENT, 0, 1.5f);

        cardParams.setMargins(0, 10, 0, 10);
        layout.addView(songCard, cardParams);

        // Playback controls
        LinearLayout playbackRow = new LinearLayout(this);
        playbackRow.setOrientation(LinearLayout.HORIZONTAL);

        addButton(playbackRow, "◀|", "PREV");
        addButton(playbackRow, "▶  /  ❚❚", "PLAYPAUSE");
        addButton(playbackRow, "|▶", "NEXT");

        LinearLayout.LayoutParams playbackParams =
                new LinearLayout.LayoutParams(
                        LinearLayout.LayoutParams.MATCH_PARENT, 0, 1);

        playbackParams.setMargins(0, 0, 0, 8);
        layout.addView(playbackRow, playbackParams);

        // Volume controls
        LinearLayout volumeRow = new LinearLayout(this);
        volumeRow.setOrientation(LinearLayout.HORIZONTAL);

        addButton(volumeRow, "−  VOLUME", "VOLDOWN");
        addButton(volumeRow, "+  VOLUME", "VOLUP");

        layout.addView(volumeRow,
                new LinearLayout.LayoutParams(
                        LinearLayout.LayoutParams.MATCH_PARENT, 0, 1));

        setContentView(layout);
    }



    private void addButton(LinearLayout layout,
                           String label, final String command) {

        Button button = new Button(this);
        button.setText(label);
        button.setTextSize(16);
        button.setTextColor(0xFFFFFFFF);
        button.setAllCaps(false);
        button.setTypeface(null, 1);
        button.setGravity(Gravity.CENTER);
        button.setPadding(4, 4, 4, 4);
        button.setBackgroundColor(
                command.equals("PLAYPAUSE") ? 0xFF1ED760 : 0xFF26352B);

        if (command.equals("PLAYPAUSE")) {
            button.setTextColor(0xFF101713);
        }

        LinearLayout.LayoutParams params =
                new LinearLayout.LayoutParams(
                        0,
                        LinearLayout.LayoutParams.MATCH_PARENT,
                        1);

        params.setMargins(4, 2, 4, 2);
        layout.addView(button, params);

        button.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View view) {
                sendCommand(command);
            }
        });
    }


    private void startServer() {
        Thread serverThread = new Thread(new Runnable() {
            @Override
            public void run() {
                try {
                    serverSocket = new ServerSocket(PORT);
                    updateStatus("Waiting for PC...");

                    while (running) {
                        Socket socket = serverSocket.accept();

                        clientSocket = socket;
                        writer = new BufferedWriter(
                                new OutputStreamWriter(
                                        socket.getOutputStream(), "UTF-8"));

                        updateStatus("PC connected!");

                        // Keep this connection until the PC disconnects.
                        BufferedReader reader = new BufferedReader(
                                new InputStreamReader(socket.getInputStream(), "UTF-8"));

                        String message;

                        while (running && (message = reader.readLine()) != null) {
                            handleIncomingMessage(message);
                        }

                        closeClient();
                        updateStatus("Waiting for PC...");
                    }

                } catch (Exception e) {
                    if (running) {
                        updateStatus("Server error: " + e.getMessage());
                    }
                }
            }
        });

        serverThread.start();
    }

    private void handleIncomingMessage(String message) {

        String[] parts = message.split("\\|", 3);

        if (parts.length == 3 && parts[0].equals("SONG")) {

            final String title = parts[1];
            final String artist = parts[2];

            handler.post(new Runnable() {
                @Override
                public void run() {
                    songTitle.setText(title);
                    songArtist.setText(artist);
                }
            });
        }
    }
    private void sendCommand(String command) {
        try {
            if (writer == null || clientSocket == null
                    || clientSocket.isClosed()) {
                updateStatus("PC not connected!");
                return;
            }

            writer.write(command);
            writer.newLine();
            writer.flush();

            updateStatus("Sent: " + command);

        } catch (Exception e) {
            closeClient();
            updateStatus("Connection lost");
        }
    }

    private void updateStatus(final String message) {
        handler.post(new Runnable() {
            @Override
            public void run() {
                if (status != null) {
                    status.setText(message);
                }
            }
        });
    }

    private void closeClient() {
        try {
            if (writer != null) writer.close();
        } catch (Exception ignored) {
        }

        try {
            if (clientSocket != null) clientSocket.close();
        } catch (Exception ignored) {
        }

        writer = null;
        clientSocket = null;
    }

    @Override
    protected void onDestroy() {
        running = false;
        closeClient();

        try {
            if (serverSocket != null) serverSocket.close();
        } catch (Exception ignored) {
        }

        super.onDestroy();
    }
}
