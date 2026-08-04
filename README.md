# PifflersCrestronLibrary

Kurzer Einstieg fuer die Zusammenarbeit und Treiber-Portierung.

## Team-Workflow

Die verbindliche Arbeitsweise liegt in:

- `COPILOT_WORKFLOW.md`

Dort sind festgelegt:

- Python -> C# Portierungsprozess
- Vererbungsmodell ueber `BasicTCP`
- Logging-/Debugger-Integration (`Debug.Log`, `ErrorLog.Error`)
- Event- und API-Konventionen (`Set/Get/TryGet`, Enums)

## Kurzprozess fuer neue Treiber

1. Python-Modul bereitstellen.
2. Zielklasse + Besonderheiten benennen.
3. Portierung in `Devices/...` auf Basis von `BasicTCP`.
4. Parser, Polling, KeepAlive und Events integrieren.
5. Fehler-/Build-Check durchfuehren.

## Hinweis

Falls Regeln geaendert werden, bitte zuerst `COPILOT_WORKFLOW.md` aktualisieren.

