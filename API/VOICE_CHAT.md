# Voice Chat API

This document specifies the optional voice chat side-channel of the Neuro API, for games with built-in voice chat (lobby VC, proximity chat, radios). It allows Neuro to:

1. **Hear other players** through the game's voice chat, with correct attribution of who is speaking.
2. **Speak into** the game's voice chat, so other players hear her through the game like any other player.

> [!Important]
> Voice chat is strictly **additive and optional**. A game integration must work fully without it, and the base protocol in [`SPECIFICATION.md`](./SPECIFICATION.md) is unchanged.
> Testing tools (Randy, Tony) do not implement it; the official SDKs degrade gracefully when the server does not accept the voice connection.

## Scope

- Only voice chat audio should be sent — never game SFX, music, or a raw speaker capture. This is not a general "let Neuro hear the game" feature.
- Game actions, state, and turn flow stay on the main text protocol. Voice is a conversational side channel; gameplay must never depend on Neuro responding vocally. Hearing → speech-to-text → decision → text-to-speech is a full cognitive cycle (seconds, not milliseconds), so treat her VC participation like a human player's.
- VC-related game controls (mute self, push-to-talk toggles, switching channels) need nothing new — register them as normal actions.
- Whether and how heard voice chat is moderated is a backend policy decision and intentionally not part of this API.

## Design overview

A game that supports voice chat opens **one additional WebSocket** next to its normal API connection:

```
ws://<host>:<port>/game/<game name>/voice
```

The URL is derived from the main websocket URL: `.../game/<name>` becomes `.../game/<name>/voice`; a base URL ending in `/game` or without a game path gets `/game/<url-encoded game name>/voice` appended. Query parameters on the base URL are preserved. The official SDKs do this derivation for you.

The voice connection carries no identifier of its own. The server pairs it with the main `/game` connection of the same game name, so both ends of your integration always talk to the same character.

- **Text frames** are JSON control messages using the same `{ command, game, data }` envelope as the main API.
- **Binary frames** are raw PCM audio: **48 kHz, mono, Float32 little-endian**, in both directions. The game client is responsible for resampling/downmixing before sending (the official SDKs do this for you).

The main `/game` socket stays JSON-only. The separate socket keeps full backward compatibility, keeps the audio hot path from blocking action/result traffic, and gives voice its own lifecycle (a game can join/leave VC repeatedly in one play session).

### Audio flow

```
game VC (per-player streams) ──binary──► server ──► STT (attributed) ──► Neuro
Neuro TTS                    ◄──binary── server
        └─► game injects into its VC transmit path (mod-level or virtual mic)
```

**Upstream (game → Neuro):** the game taps each remote player's voice stream *individually* (most VC SDKs — Vivox, Steam Voice, Photon Voice, Dissonance — expose per-player audio before it is mixed for playback) and sends each as a separate tagged stream. Per-speaker streams are what make attribution work: each stream is transcribed labelled with the player's name.

**Downstream (Neuro → game):** the server sends Neuro's spoken audio as PCM. The game feeds it into its VC transmit path — for modded games, directly into the VC SDK's audio input; otherwise into a virtual microphone device that the game's VC is configured to use. The game must **not** also play this audio locally (her voice is already audible through the normal stream pipeline; playing it again would double it).

## Wire protocol

### Handshake

After connecting, the game sends a `voice/start` text frame:

```ts
{
    "command": "voice/start",
    "game": string
}
```

The server replies with either:

```ts
{
    "command": "voice/ready",
    "data": {
        "sample_rate": 48000,   // always 48000 for now, but read it anyway
        "channels": 1           // mono, both directions
    }
}
```

or `voice/unavailable` (with an optional `reason` in `data`), after which the server closes the socket and the game should continue without voice. Servers that don't implement voice chat at all simply won't accept the connection — treat a failed connect the same as `voice/unavailable`.

### Registering speakers

Before sending audio for a player, the game must register them under a compact numeric id used to tag binary frames:

```ts
{
    "command": "voice/speakers/register",
    "game": string,
    "data": {
        "speakers": [
            {
                "id": number,     // uint16, chosen by the game, unique per socket
                "name": string    // player display name — Neuro hears speech attributed to this name
            }
        ]
    }
}
```

- `name` is what speech-to-text attribution uses (e.g. `"Vedal"` → Neuro hears *Vedal said: ...*). **This information will be directly received by Neuro.**
- Re-registering an id updates its name (player renamed). `voice/speakers/unregister` with `{ "ids": number[] }` removes speakers (player left the lobby).
- Do **not** register or send audio for Neuro's own player — that would echo her own voice back into her ears.
- The server may cap concurrent registered speakers (currently 32) and drops audio for unregistered ids.

### Binary frames, game → server (speaker audio)

```
offset 0   uint8    protocol version (1)
offset 1   uint8    flags (reserved, 0)
offset 2   uint16   speaker id (little-endian)
offset 4+  float32  PCM samples (48 kHz mono, little-endian)
```

The 4-byte header keeps the Float32 payload 4-byte aligned. Send audio only while a player is actually transmitting (VC SDKs already give you voice activity / PTT state) — do not stream continuous silence for every connected player; the server handles trailing silence itself.

Recommended frame size is 20 ms (960 samples / 3 840 payload bytes); anything between 10 ms and 100 ms is acceptable.

### Binary frames, server → game (Neuro's voice)

Headerless PCM: 48 kHz mono Float32 little-endian. There is only one downstream stream per socket, so no tagging is needed. Buffer it slightly and play it out at a steady rate into the VC transmit path. Mono is intentional — a game that wants spatialization can position the mono source itself.

### Control messages, server → game

| Command | Data | Meaning |
| --- | --- | --- |
| `voice/speaking` | `{ "speaking": boolean }` | Neuro started/stopped talking. Use it to key push-to-talk in the game's VC. |
| `voice/cancelled` | -- | Neuro's speech was interrupted. Immediately discard any buffered downstream audio and release PTT. |

Games can keep using the existing `speech_finished` message on the main `/game` socket for turn-flow logic; `voice/speaking` exists so the voice path is self-contained and timing-accurate for PTT.

### Teardown

`voice/stop` (C2S, no data) ends the voice session but keeps the socket usable for a fresh `voice/start` (leave and rejoin a VC lobby in one play session). Closing the socket also ends the session.

## Voice-only clients

The voice connection does not require a `/game` connection to exist. A client can open `/game/<name>/voice` on its own and use nothing but this protocol — useful for building Discord-like voice interfaces, where `<name>` is just the interface's name rather than an actual game. Such a client registers the people in the call as speakers and feeds their audio in exactly as described here.

## Multiple AI players in one lobby

When two characters (e.g. Neuro and Evil) are both in the same lobby, there are two supported ways for them to hear each other, chosen per setup:

- *Game-side:* each game process connects on behalf of its own character (which character a connection talks to is backend configuration), and registers the other character's player as a normal speaker. Each hears the other exactly like a human player, through the game's VC, including any proximity/radio effects the game applies.
- *Server-side:* the backend routes their vocals between characters internally. In that case the game must **not** register the other character as a speaker, or she'd be heard twice. Which leg to silence is an operator decision on the backend side.

## Performance and limits

- Uncompressed 48 kHz mono Float32 is 192 KB/s per *actively talking* speaker — fine for localhost/LAN. The version/flags header bytes leave room for a compressed payload format later without breaking the frame layout.
- The server drops malformed frames, frames for unknown speakers, and audio beyond internal buffer caps.

## SDK support

Both official SDKs ship an optional voice chat component that wraps this protocol, including URL derivation, the handshake, speaker id management, resampling/downmixing to the wire format, and graceful degradation when the server refuses voice.

- **Unity:** `NeuroSdk.Voice.NeuroVoiceChat` — see the [Unity usage docs](../Unity/USAGE.md#voice-chat).
- **Godot:** `NeuroVoiceChat` — see the [Godot usage docs](../Godot/USAGE.md#voice-chat).
