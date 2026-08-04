# Copilot Workflow fuer Treiber-Portierungen (Python -> C# Crestron)

Dieses Dokument ist unsere feste Arbeitsweise fuer kommende Sessions.

## Ziel

Python-Module (Hersteller-Treiber) werden in die C#-Library portiert, im Stil der bestehenden Crestron-Treiber.

## Standard-Vorgehen

1. Python-Quelle analysieren
   - Befehlsstrings, Antwortmuster, Status-Keys, KeepAlive, Auth-Flow, Polling.
2. C#-Zielstil angleichen
   - Namensschema und API an bestehende Treiber angleichen.
   - Vererbung ueber `BasicTCP` nutzen.
3. Typisierte Bedienung einbauen
   - Wo sinnvoll Enums statt roher Strings/Ints fuer Kanaele, Modi, Quellen.
4. Debug/Logging integrieren
   - `Debug.Log(...)` fuer TX/RX, Connect-Status, Parser-Treffer.
   - `ErrorLog.Error(...)` fuer Fehlerpfade und Protokollfehler.
5. Events und Status
   - `RaiseDataEvent("...")` mit klaren, stabilen Keys.
   - Interne State-Caches, damit nur bei Aenderung Events ausgeloest werden.
6. Verifikation
   - Dateifehler pruefen (`get_errors`).
   - Build pruefen (`dotnet build`) und Ergebnis transparent berichten.

## Architektur-Regeln

- Primaer immer `: BasicTCP`.
- KeepAlive ueber `protected override void KeepAliveCallback(object _)`.
- Regex-Parser zentral in `InitializeRegexDictionary()` registrieren.
- Keine Protokoll-Strings hart ueber mehrere Stellen verteilen: Helfer/Mapper nutzen.
- Oeffentliche API immer klar und robust:
  - `Set...(...)`
  - `Get...(...)`
  - `TryGet...(...)`

## Logging-/Debugger-Regeln

- Beim Connect/Disconnect immer Ziel `host:port` loggen.
- Rohdaten RX nur loggen, wenn fuer Diagnose relevant (nicht unnoetig noisy).
- Protokollfehler klar kennzeichnen und als `LastError`/`DataEvent("Error")` spiegeln.
- Keine stillen Fehler: invalid input frueh validieren und loggen.

## Was ich bei jedem neuen Treiber liefere

- Neue Treiberdatei im passenden Ordner (z. B. `Devices/Shure/`, `Devices/Lightware/`, `Devices/Custom/`).
- Falls noetig `.csproj`-Eintrag.
- Kurzes Nutzungsbeispiel.
- Build-/Fehlerstatus.

## Input-Template fuer dich

Wenn du mir den naechsten Treiber gibst, reicht diese Form:

- Python-Datei: `<pfad/zur/datei.py>`
- Zielklasse: `<z. B. Shure_XYZ>`
- Besonderheiten:
  - [ ] Single class
  - [x] Vererbung ueber `BasicTCP`
  - [x] Enums fuer Channel/Mode
  - [x] Debug-Integration hoch
  - [ ] Rueckwaertskompatible API noetig

## Aktuelle Team-Entscheidung

- Vererbung ist Standard.
- Debugger/Logger wird konsequent eingebunden.
- Python ist die fachliche Protokoll-Referenz.

