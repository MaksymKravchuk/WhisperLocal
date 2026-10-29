# Changelog

## 1.1.0 — 2026-10-29

- Optional cloud speech recognition: `Stt.Provider` = `Groq` (whisper-large-v3 / turbo) or `Cloudflare` Workers AI (whisper-large-v3-turbo); `Local` stays the default.
- API keys in `secrets.json` next to the exe or in environment variables, never in `config.json`.
- Automatic fallback to the local whisper-server when the cloud request fails, with one tray warning.
- The provider that produced the text is shown in the log and in the `--batch` CSV.

## 1.0.0 — 2026-10-27

First working version.

- Push-to-talk dictation from the tray: hold the hotkey (default Left Ctrl + Left Win), speak, release — the text is pasted into the active window.
- Speech recognition by bundled whisper.cpp (`whisper-server`, Vulkan build) with Whisper large-v3, language forced to Ukrainian.
- English terms: initial prompt plus a regex dictionary in the config.
- Silence / short-press filtering and removal of typical Whisper hallucinations.
- Unload from memory (manual and after 5 minutes of inactivity); the next hotkey press loads everything again.
- Hotkey, microphone, GPU, autostart with Windows and all other settings in `config.json` (hot reload, `//` comments).
- `--batch` mode for comparing settings and models on recorded audio.
