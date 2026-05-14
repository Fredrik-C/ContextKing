# OpenHands + Context King (Minimal Footprint)

This template integrates Context King into OpenHands with low surface area:

- OpenHands always-on guidance via `AGENTS.md`
- Session bootstrap via `.openhands/setup.sh`
- Light terminal enforcement via `.openhands/hooks.json`

## 1. Install CK on an OpenHands runtime host

```bash
curl -fsSL https://raw.githubusercontent.com/Fredrik-C/ContextKing/main/scripts/install-openhands-remote.sh | bash
```

The installer keeps only OpenHands-relevant assets (`ck` + `~/.agents/skills`) and skips Claude/Codex/OpenCode config.

## 2. Copy template files into your target repository

From the repository root:

```bash
cp docs/openhands/template/AGENTS.md AGENTS.md
mkdir -p .openhands/hooks
cp docs/openhands/template/.openhands/setup.sh .openhands/setup.sh
cp docs/openhands/template/.openhands/hooks.json .openhands/hooks.json
cp docs/openhands/template/.openhands/hooks/ck-terminal-guard.sh .openhands/hooks/ck-terminal-guard.sh
chmod +x .openhands/setup.sh .openhands/hooks/ck-terminal-guard.sh
```

## 3. Run OpenHands

OpenHands loads:

- `AGENTS.md` as persistent context
- `.openhands/setup.sh` at session start
- `.openhands/hooks.json` hooks during tool usage
