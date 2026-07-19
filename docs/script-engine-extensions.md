# Extensões de scripts por engine

Este levantamento foi feito a partir dos parsers registrados em `VNTextPatch.Shared/Scripts/FolderScriptCollection.cs` e das propriedades `Extension` dos scripts em `VNTextPatch.Shared/Scripts`.

| Extensão | Engine / formato | Parser(es) | Observações |
|---|---|---|---|
| `.a0` | C-System | `CSystemScript` | Script textual da engine C-System. |
| `.art` | Artemis | `ArtemisTxtScript` | Formato textual da engine Artemis. |
| `.asb` | Artemis | `ArtemisAsbScript` | Script binário/estruturado da engine Artemis. |
| `.ast` | Artemis | `ArtemisAstScript` | Tabelas/atributos Lua usados pela engine Artemis. |
| `.bgi` | Ethornell / BGI | `EthornellScript` | Scripts da engine Buriko General Interpreter. |
| `.bin` | ArcGameEngine | `AgeScript` | Scripts binários da ArcGameEngine. |
| `.cst` | CatSystem | `CatSystemScript` | Scripts da engine CatSystem. |
| `.dat` | Kaguya | `KaguyaScript` | Arquivos de script usados pelo parser Kaguya. |
| `.hst` | SH System | `ShSystemScript` | Scripts da engine SH System. |
| `.ks` | KiriKiri | `KirikiriKsScript` | Cenários KAG/KiriKiri em texto. |
| `.map` | Silky's | `SilkysMapScript` | Mapas de strings usados em jogos Silky's. |
| `.mes` | Silky's | `SilkysMesScript` | Scripts MES para engines Silky's / AI6WIN / Silky's Plus. |
| `.mjo` | Majiro | `MajiroScript` | Scripts compilados da engine Majiro. |
| `.msc` | Propeller | `PropellerScript` | Scripts da engine Propeller. |
| `.nnn` | System-NNN | `SystemNnnDevScript` | Formato de desenvolvimento do System-NNN. |
| `.nut` | Mware | `MwareScript` | Bytecode Squirrel usado pelo formato Mware. |
| `.rl` | RealLive | `RealLiveScript` | Scripts da engine RealLive. |
| `.rpy` | Ren'Py | `RenpyScript` | Scripts Python/Ren'Py em texto. |
| `.s` | QLIE | `QlieScript` | Scripts da engine QLIE. |
| `.sc` | Musica | `MusicaScript` | Scripts da engine Musica. |
| `.soc` | KiriKiri | `KirikiriSocScript` | Arquivos SOC da engine KiriKiri. |
| `.spt` | System-NNN | `SystemNnnReleaseScript` | Formato de release do System-NNN. |
| `.srp` | Tmr-Hiro ADV System | `TmrHiroAdvSystemCodeScript` | Scripts de código da engine Tmr-Hiro ADV System. |
| `.src` | Softpal | `SoftpalScript` | Scripts da engine Softpal. |
| `.tjs` | KiriKiri | `KirikiriTjsScript` | Scripts TJS da engine KiriKiri. |
| `.ws2` | ADV HD | `AdvHdScript` | Scripts da engine ADV HD. |
| `.ybn` | YU-RIS | `YurisScript`, `YurisScenarioScript`, `YurisConfigScript` | Contêiner YU-RIS; o parser despacha cenários e configurações pelo magic interno. |

## Parsers sem extensão fixa ou não registrados automaticamente

| Extensão | Engine / formato | Parser(es) | Observações |
|---|---|---|---|
| Sem extensão fixa | Tmr-Hiro ADV System | `TmrHiroAdvSystemTextScript` | Parser de texto auxiliar; selecionável por formato, não por extensão. |
| Sem extensão fixa | Whale | `WhaleScript` | Parser selecionável por formato, não por extensão. |
| `.scn` | KiriKiri | `KirikiriScnScript` | O código existe no diretório, mas está removido do build e comentado na lista de parsers registrados. |

## Formatos auxiliares que não representam game engines

| Extensão | Formato | Parser(es) | Observações |
|---|---|---|---|
| `.json` | JSON | `JsonScript` | Formato genérico auxiliar. |
| `.xlsx` | Excel | `ExcelScript` | Usado como coleção/planilha de texto, não como engine. |
| Google Docs | Google Sheets | `GoogleDocsScript` | Usado como coleção remota de texto, não como engine. |
