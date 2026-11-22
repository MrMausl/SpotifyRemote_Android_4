# SpotifyRemote_Android_4

A USB-connected Spotify remote control for Android 4.2.2 and Windows 11.

## Overview

SpotifyRemote_Android_4 consists of two applications:

- An Android application that displays the currently playing track and sends playback commands.
- A Windows application that communicates with the Android device through Android Debug Bridge (ADB) and controls Windows media playback.

The applications communicate over a TCP connection forwarded through ADB.

The project does not use the Spotify Web API. It uses the Windows Media Session API to retrieve playback information and Windows media keys to control playback.

## Features

- Display the current track title and artist.
- Previous track.
- Play and pause.
- Next track.
- Volume down.
- Volume up.
- Windows system tray application.
- ADB USB connection between the Android device and Windows.
- No Spotify account credentials are required by this project.

## Requirements

### Android

- Android 4.2.2 (API 17) or later.
- USB connection to the Windows computer.
- USB debugging enabled.

### Windows

- Windows 11.
- Spotify or another application that supports the Windows Media Session API.
- Android Debug Bridge (ADB), included in the Windows release.
- USB drivers for the connected Android device, if required.

## Project Structure

```text
SpotifyRemote_Android_4/
├── Android/
│   └── SpotifyRemote/
├── Windows/
│   └── SpotifyRemotePC/
└── Releases/
    ├── SpotifyRemote.apk
    └── Windows/
```

## Connection

The Android application communicates with the Windows application through ADB USB port forwarding.

The Windows application configures the forwarding for TCP port `38210`.

Both applications must be running, and the Android device must be connected with USB debugging enabled.

## Installation

### Android

1. Install `Releases/SpotifyRemote.apk` on the Android device.
2. Enable USB debugging in Android developer options.
3. Connect the device to the Windows computer using USB.

### Windows

1. Open `Releases/Windows/`.
2. Run `SpotifyRemotePC.exe`.
3. Allow the application to configure ADB port forwarding when required.
4. Keep the Windows application running in the system tray.

## Source Code

The Android source code is located in `Android/SpotifyRemote/`.

The Windows source code is located in `Windows/SpotifyRemotePC/`.

## Disclaimer

This is an independent project and is not affiliated with, endorsed by, or supported by Spotify AB.

Spotify is a trademark of Spotify AB.

Source of the .ico file: http://icon-icons.com/de/symbol/spotify-dark-macos-bigsur/189701?ii_item_modal=1&ii_modal_origin=%2Fde%2Fsuche%2Fsymbole%2Fsp


