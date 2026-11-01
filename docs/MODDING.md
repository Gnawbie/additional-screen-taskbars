# Modding AST

There are two ways to change how a bar looks. Both get drag-to-resize, right-click menus and fullscreen hiding automatically.

**Themes and plugins can run code.** XAML can create .NET objects, so treat a theme like a program, and only install ones you trust.

## XAML themes (no compiling)

1. Open AST's Options and choose **Open themes folder** (`%APPDATA%\AST\themes`).
2. Copy `samples/themes/neon` into it and rename the folder.
3. Edit `theme.xaml`, then pick your theme under **Look** in Options.

To reload your edits, switch to another theme and back.

A theme is a `ResourceDictionary` that needs one thing: a `DataTemplate` with the key `AST.Bar`. It controls the whole bar, so you can change the layout as well as the colors. Relative image paths resolve against the theme's folder.

`theme.json` is optional and sets the name shown in Options:

```json
{ "name": "My theme" }
```

If a theme fails to load, AST shows the built-in Modern look and writes the error to `%APPDATA%\AST\log.txt`.

### What you can bind to

The template's data context is a `BarViewModel`:

| Property | Meaning |
|---|---|
| `Items` | Open windows on this screen |
| `Edge` | `Top`, `Bottom`, `Left` or `Right` |
| `IsVertical`, `IsHorizontal` | Which way the bar runs |
| `Thickness` | Bar size in device-independent pixels |
| `IconsOnly` | The user has chosen to hide program names |
| `ShowLabels` | Whether to show names right now. Use this for a name's visibility |
| `RotateLabels` | Names should run sideways |
| `IsNarrow` | A side bar too thin for most horizontal text, so shrink or rotate it |
| `TimeText`, `DateText` | The current time and date |
| `ShowClock`, `ShowStartButton` | The user's choices |
| `TrayItems`, `ShowTray` | Tray buttons, each with `Name`, `Glyph` (in the Segoe MDL2 Assets font) and `InvokeCommand` |
| `MonitorName`, `MonitorNumber` | Which screen this is |
| `StartCommand`, `SettingsCommand` | Open Start, or AST's Options |

Each item in `Items` is a `TaskItem`:

| Property | Meaning |
|---|---|
| `Title`, `Icon` | The window's title and icon |
| `IsActive`, `IsMinimized` | The window's state |
| `ActivateCommand` | Switch to the window, or minimize it if it's already active |

### Laying out buttons

`TaskPanel` is an optional items panel that shrinks buttons evenly when they run out of room:

```xml
xmlns:eb="clr-namespace:AST;assembly=AST"

<eb:TaskPanel IsVertical="{Binding IsVertical}" IsRotated="{Binding RotateLabels}" IsIconsOnly="{Binding IconsOnly}" />
```

You can set the button sizes with `ItemLength`, `VerticalItemLength` and `IconItemLength`.

## Code plugins (C#)

1. Create a .NET Framework 4.7.1 class library that references `AST.exe`. Set the reference's Copy Local to false.
2. Implement `IAstStyle`. `CreateBar` runs once per bar, so listen for changes to the `BarViewModel` it receives.
3. Put the DLL in `%APPDATA%\AST\plugins`.
4. In AST's Options, turn on **Enable plugin**, then pick your style under **Look**.

`samples/plugins/ExampleStyle` is a complete example.

Plugins stay off until the user enables each one. A disabled plugin stops being used after AST restarts, because .NET Framework can't unload a DLL while the app is running.
