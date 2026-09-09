# MARS Adaptive Agent

## Normal run

```bash
cd GridBlueprint
dotnet run -- config.adaptive.json
```

## OpenAI run

```bash
cd GridBlueprint
cp .env.example .env
nano .env
```

```text
ADAPTIVE_LLM_PROVIDER=openai
OPENAI_API_KEY=your-api-key
OPENAI_MODEL=gpt-4.1
```

```bash
dotnet run -- config.adaptive.json
```

## Force a new skill generation

```bash
rm -f bin/Debug/net10.0/skills.json
dotnet run -- config.adaptive.json
```

## Visualization

Terminal 1:

```bash
cd Visualization
pip3 install -r requirements.txt
python3 main.py
```

Terminal 2:

```bash
cd GridBlueprint
dotnet run -- config.adaptive.visual.json
```

## Benchmark

```bash
cd GridBlueprint
dotnet run -- --benchmark
```
