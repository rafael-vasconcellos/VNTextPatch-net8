# Script extensions by engine

This reference was prepared from the parsers registered in `VNTextPatch.Shared/Scripts/FolderScriptCollection.cs` and the `Extension` properties of the scripts in `VNTextPatch.Shared/Scripts`.

| Extension | Engine / format | Parser(s) | Notes |
|---|---|---|---|
| `.a0` | C-System | `CSystemScript` | Text script for the C-System engine. |
| `.art` | Artemis | `ArtemisTxtScript` | Text format for the Artemis engine. |
| `.asb` | Artemis | `ArtemisAsbScript` | Binary/structured script for the Artemis engine. |
| `.ast` | Artemis | `ArtemisAstScript` | Lua tables/attributes used by the Artemis engine. |
| `.bgi` | Ethornell / BGI | `EthornellScript` | Scripts for the Buriko General Interpreter engine. |
| `.bin` | ArcGameEngine | `AgeScript` | Binary scripts for ArcGameEngine. |
| `.cst` | CatSystem | `CatSystemScript` | Scripts for the CatSystem engine. |
| `.dat` | Kaguya | `KaguyaScript` | Script files handled by the Kaguya parser. |
| `.hst` | SH System | `ShSystemScript` | Scripts for the SH System engine. |
| `.ks` | KiriKiri | `KirikiriKsScript` | Text-based KAG/KiriKiri scenarios. |
| `.map` | Silky's | `SilkysMapScript` | String maps used by Silky's games. |
| `.mes` | Silky's | `SilkysMesScript` | MES scripts for Silky's / AI6WIN / Silky's Plus engines. |
| `.mjo` | Majiro | `MajiroScript` | Compiled scripts for the Majiro engine. |
| `.msc` | Propeller | `PropellerScript` | Scripts for the Propeller engine. |
| `.nnn` | System-NNN | `SystemNnnDevScript` | System-NNN development format. |
| `.nut` | Mware | `MwareScript` | Squirrel bytecode used by the Mware format. |
| `.rl` | RealLive | `RealLiveScript` | Scripts for the RealLive engine. |
| `.rpy` | Ren'Py | `RenpyScript` | Text-based Python/Ren'Py scripts. |
| `.s` | QLIE | `QlieScript` | Scripts for the QLIE engine. |
| `.sc` | Musica | `MusicaScript` | Scripts for the Musica engine. |
| `.soc` | KiriKiri | `KirikiriSocScript` | SOC files for the KiriKiri engine. |
| `.spt` | System-NNN | `SystemNnnReleaseScript` | System-NNN release format. |
| `.srp` | Tmr-Hiro ADV System | `TmrHiroAdvSystemCodeScript` | Code scripts for the Tmr-Hiro ADV System engine. |
| `.src` | Softpal | `SoftpalScript` | Scripts for the Softpal engine. |
| `.tjs` | KiriKiri | `KirikiriTjsScript` | TJS scripts for the KiriKiri engine. |
| `.ws2` | ADV HD | `AdvHdScript` | Scripts for the ADV HD engine. |
| `.ybn` | YU-RIS | `YurisScript`, `YurisScenarioScript`, `YurisConfigScript` | YU-RIS container; the parser dispatches scenarios and configuration files by internal magic. |

## Parsers with no fixed extension or no automatic registration

| Extension | Engine / format | Parser(s) | Notes |
|---|---|---|---|
| No fixed extension | Tmr-Hiro ADV System | `TmrHiroAdvSystemTextScript` | Auxiliary text parser; selected by format, not by extension. |
| No fixed extension | Whale | `WhaleScript` | Parser selected by format, not by extension. |
| `.scn` | KiriKiri | `KirikiriScnScript` | The code exists in the directory, but it is removed from the build and commented out in the registered parser list. |

## Auxiliary formats that are not game engines

| Extension | Format | Parser(s) | Notes |
|---|---|---|---|
| `.json` | JSON | `JsonScript` | Generic auxiliary format. |
| `.xlsx` | Excel | `ExcelScript` | Used as a text spreadsheet/collection, not as an engine. |
| Google Docs | Google Sheets | `GoogleDocsScript` | Used as a remote text collection, not as an engine. |
