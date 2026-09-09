# MARS Adaptive Agent

## Demo ansehen

Benötigt werden .NET 10 und Python 3.13. Beide Terminals im Repository-Ordner starten.

Die Visualisierung verwendet ein 20×20-Gitter mit roten Hinderniszellen.

Der Agent startet bei `(1,10)`. Gelb markiert das erste Ziel bei `(9,10)`.

Grün markiert das finale Ziel bei `(18,10)`.

Der hellblaue Punkt ist der Agent.

Terminal 1:

```bash
cd Visualization
python3.13 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
python main.py
```

Terminal 2:

```bash
cd GridBlueprint
bash run.sh config.adaptive.visual.json
```

Nach jedem Lauf wird das Log automatisch im Texteditor geöffnet. Die Logs werden in `GridBlueprint/logs/` gespeichert.

Achte im Log auf `blocked at`, `Generating skill` und `Registered validated detour`. An der zweiten Wand sollte `Reusing validated detour` erscheinen. Am Ende steht `reached its goal`.

## Skill-Generierung

Wenn der Agent blockiert wird, sucht er zuerst nach einem gespeicherten Umweg zum Ziel. Wenn kein passender Skill existiert, erhält der Generator die Gittergrenzen, Hindernisse und Bewegungsregeln. Jeder Schritt wird vor der Registrierung geprüft. Die zweite Wand testet die Wiederverwendung an einer anderen Position.

Skills dürfen höchstens 8 Grundbewegungen enthalten. Ungültige Vorschläge werden verworfen. Nach 3 fehlgeschlagenen Versuchen wird die Aufgabe als fehlgeschlagen markiert. Diese Demo benötigt nur einen Umweg mit 4 Bewegungen.

Ohne Konfiguration verwendet der Generator einen festen Demo-Skill. Der Anbieter wird in `GridBlueprint/.env` ausgewählt:

```text
# Offline-Demo
ADAPTIVE_LLM_PROVIDER=fake

# Echte Modellgenerierung
ADAPTIVE_LLM_PROVIDER=openai
OPENAI_API_KEY=your-api-key
OPENAI_MODEL=gpt-4.1
```

Wenn kein Anbieter angegeben ist, wird `fake` verwendet. Im Log stehen der Anbieter und der gesendete Prompt.

Für eine neue Skill-Generierung kann `GridBlueprint/bin/Debug/net10.0/skills.json` vor dem Start verschoben werden. Sonst wird ein gespeicherter Umweg direkt wiederverwendet. Die Grundbewegungen reichen physisch für diesen Weg aus; die fehlende Fähigkeit ist die Planung des Umwegs.

## Benchmark

```bash
cd GridBlueprint
dotnet run -- --benchmark
```
