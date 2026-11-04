# Third-party components shipped with Vision Studio

Third-party open-source parts keep their own names, copyright and licence texts (AGENTS.md IP rule 8).
Add a row when a shipped component is added.

## Fonts (requirements 17.15 NXT-02)

Embedded in `dotnet/Studio/Assets/Fonts` as Avalonia resources; each licence text ships beside the fonts
and is copied to the application's `licences` folder.

| Font | Files | Copyright | Licence | Source |
| --- | --- | --- | --- | --- |
| Rajdhani | Rajdhani-Medium, -SemiBold, -Bold (.ttf) | © 2014 Indian Type Foundry | SIL Open Font License 1.1 (`Rajdhani-OFL.txt`) | github.com/google/fonts, `ofl/rajdhani` |
| Noto Sans | NotoSans-Regular, -SemiBold, -Bold (.ttf) | © 2022 The Noto Project Authors | SIL Open Font License 1.1 (`NotoSans-OFL.txt`) | github.com/notofonts (latin-greek-cyrillic) |
| JetBrains Mono | JetBrainsMono-Regular, -SemiBold, -Bold (.ttf) | © 2020 The JetBrains Mono Project Authors | SIL Open Font License 1.1 (`JetBrainsMono-OFL.txt`) | github.com/JetBrains/JetBrainsMono |

## Libraries

| Component | Used for | Licence |
| --- | --- | --- |
| System.Speech (Microsoft, NuGet) | Spoken alarms on Windows (SAPI), NXT-05 | MIT |
