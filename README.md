# AST: Additional Screen Taskbars

Windows 11 can't give each screen its own taskbar position. AST adds a separate taskbar to your other screens, and each screen can have its own setting.

[![Download ASTSetup.exe](https://img.shields.io/badge/Download-ASTSetup.exe-2ea44f?style=for-the-badge&logo=windows&logoColor=white)](https://github.com/Gnawbie/additional-screen-taskbars/releases/download/v0.1.1/ASTSetup.exe)

![Two screens side by side: the Windows taskbar along the bottom of the main screen, and an AST taskbar down the left side of the second screen](docs/screenshot.png)

*The Windows taskbar on the main screen, with an AST bar on the side of the second screen.*

## Features

- Put a bar on the top, bottom, left or right of each screen, or turn it off for that screen
- Drag an empty part of a bar to another edge of its screen
- Resize a bar by dragging its inner edge, or with the slider in Options
- Right-click a bar and choose **Lock taskbars** to stop dragging from moving or resizing it
- Show icons only (the default), or right-click a bar and choose **Show program names**
- List programs from all screens (the default), or right-click a bar and untick **Show programs from all screens** to list only that screen's programs
- Show program names vertically on side bars
- Tray buttons for hidden icons, network and volume, with live status
- Choose the Modern or Classic look, or add your own (see [docs/MODDING.md](docs/MODDING.md))
- Bars hide themselves when a fullscreen app is on their screen

AST uses the documented Windows app bar API. It doesn't inject code into Explorer or games, and it doesn't hook the keyboard.

## Install

Run `ASTSetup.exe`. It installs for the current user, needs no admin rights and uses the .NET Framework built into Windows 10 and 11.

The exe isn't code-signed yet, so Windows SmartScreen may warn about it.

To remove AST, go to Settings > Apps > Installed apps.

## Use

Your main screen starts with no bar, because the Windows taskbar is there. Other screens start with a bar at the bottom.

To open Options, right-click a bar and choose **Options...**, or click the AST tray icon. Use **Identify screens** to see each screen's number. Numbers come from the Windows display name, such as `\\.\DISPLAY2`, and normally match Settings > System > Display.

Command line options: `--install`, `--uninstall`, `--silent`, `--settings`, `--quit`.

## Build

You need Visual Studio or the VS Build Tools with a .NET Framework 4.7.1 or later targeting pack.

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```

The output goes to `dist\`.

## Known limits

- No jump lists, thumbnail previews or flashing buttons for windows that want attention
- Tray buttons mirror the Windows taskbar's and open Windows' own flyouts, moved to the bar's screen just after they open
- Other programs' tray icons, such as Discord's, stay in the hidden icons flyout rather than on the bar
- Windows chooses which screen the Start menu opens on
- A store app may show a generic icon

## License

[MIT](LICENSE)
