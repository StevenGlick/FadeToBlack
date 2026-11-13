# FadeToBlack

A tiny Windows tray utility that fades the screen to black over time, rather than sleeping the monitor instantly.

Windows normally just cuts the display off when the "turn off my screen after" timer runs out. If you're sitting nearby (reading, watching a long build, letting an AI agent run) and simply forgot to wiggle the mouse, the screen goes blank and you have to wake it up again.

FadeToBlack gives you a warning you can actually see: for the last minute or two before Windows would sleep the display, the whole screen slowly darkens. Move the mouse or press a key at any point and it snaps right back. If you don't, it reaches full black and then turns the monitor off, just like Windows would have.

## Features

- **Follows your Windows setting.** It reads "Turn off my screen after" from your active power plan (plugged in or on battery) and times the fade to end exactly then. Change the setting in Windows and FadeToBlack picks it up within a few seconds.
- **Smooth, even fade.** The dimming curve is adjusted for how eyes perceive brightness, so it darkens steadily instead of doing nothing and then dropping off at the end.
- **Instant cancel.** Any mouse movement or key press restores the screen immediately. The dark layer is click-through, so the wiggle that cancels it doesn't click anything.
- **Respects video and presentations.** If an app is keeping the display awake (video players, browsers playing video, slideshows), Windows won't sleep the monitor, and FadeToBlack won't fade it.
- **Works on any monitor.** It uses an on-screen overlay rather than the monitor's brightness controls, so it works on every display, including multi-monitor and ultrawide setups.
- **Stays out of the way.** It's a single small tray app with no window, no install and no background service.

## Requirements

- Windows 10 or 11
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (x64)

## Getting started

### Build from source

You'll need the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```bash
git clone https://github.com/StevenGlick/FadeToBlack.git
cd FadeToBlack
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o bin/app
```

Then run `bin/app/FadeToSleep.exe`. A small gold-to-black circle appears in the system tray.

To build a version that doesn't need .NET installed (much larger file), use `--self-contained true` instead.

> **Note:** Because the exe isn't code-signed, Windows SmartScreen may show "Windows protected your PC" the first time you run a downloaded copy. Click **More info → Run anyway**.

## Using it

Right-click the tray icon:

| Menu item | What it does |
| --- | --- |
| *Status line* | Shows your current display timeout and when the fade will start. |
| **Enabled** | Pause or resume FadeToBlack. |
| **Fade length** | How long the fade lasts: 30 sec, 1 min, 1.5 min (default), 2 min or 3 min. The fade never takes up more than 80% of your display timeout. |
| **Preview fade (10 sec)** | Take your hands off the mouse and keyboard to see the full effect, including the monitor turning off. Move the mouse to cancel. |
| **Start with Windows** | Launch automatically when you sign in. |
| **Exit** | Close the app and restore the screen. |

Settings are saved to `%APPDATA%\FadeToSleep\settings.json`.

If your display timeout is set to **Never**, FadeToBlack has nothing to do and stays idle.

## How it works

Every quarter second, FadeToBlack checks how long you've been idle (`GetLastInputInfo`) and reads the display timeout from your active power scheme. When the fade window begins, it shows a borderless, click-through, always-on-top black window on each monitor and raises its opacity about 30 times a second. At 100% it sends the standard "monitor off" command (`SC_MONITORPOWER`). Before fading, it asks Windows whether any app is requesting the display stay on (`CallNtPowerInformation` → `ES_DISPLAY_REQUIRED`) and does nothing if one is.

For troubleshooting, `FadeToSleep.exe --status <file>` writes what the app currently sees (timeout, idle time, display requests, monitors) to a text file and exits.

## Known limitations

- Some exclusive-fullscreen games can draw over the overlay. You're usually using the mouse or keyboard while gaming, so the fade wouldn't start anyway.
- Windows' own screen-off can kick in a moment before the fade fully finishes if the two timers drift slightly. The result is the same: the screen goes dark.

## License

[MIT](LICENSE)
