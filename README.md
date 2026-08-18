V 1.5
click time can be set by user





# Keys Autoclicker

A Windows 11-style keyboard automation desktop app built with C# and .NET 8 WPF.

## Features

- Multiple key steps with press counts and drag-to-reorder sequence editing.
- Adjustable delay, sequence repetitions, instant cancellation, and background execution.
- Global Start/Stop hotkeys (F8/F9 defaults), duplicate prevention, master toggle, light/dark mode, and persisted settings.

## Run from source

Install the .NET 8 SDK, then from the repository root:

```powershell
dotnet run --project .\KeysAutoclicker\KeysAutoclicker.csproj
```

## Build portable app

```powershell
dotnet publish .\KeysAutoclicker\KeysAutoclicker.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\Release\portable
```

Run `Release\Keys Autoclicker.exe`. The app stores settings in `%LocalAppData%\KeysAutoclicker\settings.json`.

## Install

Run `Release\Keys Autoclicker Installer.exe`. It installs for the current user in `%LocalAppData%\Programs\Keys Autoclicker` and opens the app.

Developed by Jeevan Varghese. Visit [itsjeevanvarghese.web.app](https://itsjeevanvarghese.web.app) for more software.
