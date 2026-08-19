#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using NativeWebSocket;
using NeuroSdk.Il2Cpp;
using NeuroSdk.Internal;
using NeuroSdk.Websocket;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Events;

namespace NeuroSdk.Voice
{
    /// <summary>
    /// Optional voice chat side-channel for games with built-in voice chat.
    ///
    /// This opens a second websocket next to the main Neuro API connection, over which
    /// the game streams each remote player's voice (with attribution) so Neuro can hear
    /// them, and receives Neuro's voice to feed into the game's voice chat transmit path.
    ///
    /// See the voice chat API documentation for the wire protocol. Voice chat is strictly
    /// optional: if the server does not support it, this component raises
    /// <see cref="onUnavailable"/> once and stays idle — your game must work without it.
    /// </summary>
#pragma warning disable CS0618 // Type or member is obsolete
    [RegisterInIl2Cpp]
#pragma warning restore CS0618 // Type or member is obsolete
    public sealed class NeuroVoiceChat : MonoBehaviour
    {
        private const float RECONNECT_INTERVAL = 3;
        private const int MAX_CONTROL_MESSAGE_BYTES = 1024;

        private static NeuroVoiceChat? _instance;
        public static NeuroVoiceChat? Instance => _instance;

        private static WebSocket? _socket;

        /// <summary>Fired when the server has acknowledged the voice session (voice/ready).</summary>
        public UnityEvent? onReady;
        /// <summary>Fired when the server refused the voice session (voice/unavailable) or no URL could be derived. The string is a reason.</summary>
        public UnityEvent<string>? onUnavailable;
        /// <summary>Fired when Neuro starts (true) or stops (false) speaking. Use it to key push-to-talk in the game's voice chat.</summary>
        public UnityEvent<bool>? onSpeakingChanged;
        /// <summary>Fired when Neuro's speech was interrupted. Immediately discard any buffered audio and release push-to-talk.</summary>
        public UnityEvent? onCancelled;
        /// <summary>Fired with chunks of Neuro's voice as 48 kHz mono PCM. Feed these into the game's voice chat transmit path. Do not also play them locally.</summary>
        public UnityEvent<float[]>? onAudioReceived;

        public bool IsReady { get; private set; }

        private readonly Dictionary<int, string> _speakers = new();
        private int _nextSpeakerId = 1;
        private bool _stopped;
        private bool _refused;

        /// <summary>
        /// Create (or return) the voice chat component and start connecting. Requires the
        /// main SDK connection to have been initialized first, since the voice websocket
        /// URL is derived from the main websocket URL and the game name.
        /// </summary>
        public static NeuroVoiceChat Connect()
        {
            if (_instance) return _instance!;

            GameObject obj = new("NeuroSdkVoiceChat") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(obj);
            return obj.AddComponent<NeuroVoiceChat>();
        }

        private void Awake()
        {
            if (_instance)
            {
                Debug.Log("Destroying duplicate NeuroVoiceChat instance");
                Destroy(this);
                return;
            }

            _instance = this;
        }

        // ReSharper disable once ArrangeThisQualifier -- Il2Cpp has this as an extension method
        private void Start() => this.StartCoroutine(StartWs());

        /// <summary>
        /// Register a remote player as a voice chat speaker and return the id to tag
        /// their audio with. Do not register Neuro's own player. The name is what her
        /// speech recognition attributes the audio to, so use the player's display name.
        /// </summary>
        public int RegisterSpeaker(string name)
        {
            int id = _nextSpeakerId++;
            _speakers[id] = name;
            if (IsReady) SendSpeakerRoster(new[] { id });
            return id;
        }

        /// <summary>Rename a registered speaker (e.g. the player changed display name).</summary>
        public void RenameSpeaker(int id, string name)
        {
            if (!_speakers.ContainsKey(id))
            {
                Debug.LogWarning($"RenameSpeaker called for unknown speaker id {id}");
                return;
            }
            _speakers[id] = name;
            if (IsReady) SendSpeakerRoster(new[] { id });
        }

        /// <summary>Unregister a speaker (the player left the lobby).</summary>
        public void UnregisterSpeaker(int id)
        {
            if (!_speakers.Remove(id)) return;
            if (!IsReady) return;
            SendControl("voice/speakers/unregister", new JObject { ["ids"] = new JArray(id) });
        }

        /// <summary>
        /// Send a chunk of one speaker's voice audio. Only call this while the player is
        /// actually transmitting (your voice chat layer's voice activity / push-to-talk
        /// state) — do not stream continuous silence. Input is interleaved PCM at any
        /// sample rate / channel count; it is converted to 48 kHz mono before sending.
        /// Recommended chunk size is 20 ms; anything between 10 ms and 100 ms is fine.
        /// </summary>
        public void SendSpeakerAudio(int id, float[] samples, int sampleRate = VoiceAudio.WireSampleRate, int channels = 1)
        {
            if (!IsReady || _socket?.State is not WebSocketState.Open) return;
            if (!_speakers.ContainsKey(id))
            {
                Debug.LogWarning($"SendSpeakerAudio called for unknown speaker id {id}");
                return;
            }

            float[] wire = VoiceAudio.ToWireFormat(samples, sampleRate, channels);
            if (wire.Length == 0) return;
            _ = _socket.Send(VoiceAudio.EncodeSpeakerFrame(id, wire));
        }

        /// <summary>
        /// Leave voice chat: sends voice/stop and closes the connection. Call
        /// <see cref="Connect"/> again to rejoin later.
        /// </summary>
        public void Disconnect()
        {
            _stopped = true;
            IsReady = false;
            if (_socket?.State is WebSocketState.Open)
            {
                SendControl("voice/stop", null);
                _ = _socket.Close();
            }
            _instance = null;
            Destroy(gameObject);
        }

        [Il2CppHide]
        private IEnumerator Reconnect()
        {
            yield return new WaitForSecondsRealtime(RECONNECT_INTERVAL);
            yield return StartWs();
        }

        [Il2CppHide]
        private IEnumerator StartWs()
        {
            if (_stopped || _refused) yield break;

            try
            {
                if (_socket?.State is WebSocketState.Open or WebSocketState.Connecting) _ = _socket.Close();
            }
            catch
            {
                // ignored
            }

            string? baseUrl = null;
            yield return WsUrlFinder.FindWsUrl(result => baseUrl = result);

            string? game = WebsocketConnection.Instance?.game;
            string? voiceUrl = DeriveVoiceUrl(baseUrl, game);
            if (voiceUrl is null)
            {
                const string reason = "Could not derive the voice chat websocket URL. Make sure the main SDK connection is initialized and NEURO_SDK_WS_URL is set.";
                Debug.LogWarning(reason);
                onUnavailable?.Invoke(reason);
                yield break;
            }

            // Websocket callbacks get run on separate threads! Watch out
            _socket = new WebSocket(voiceUrl);
            _socket.OnOpen += () =>
            {
                // ReSharper disable once ArrangeThisQualifier -- Il2Cpp has this as an extension method
                this.StartCoroutine(coroutine());
                return;

                IEnumerator coroutine()
                {
                    yield return null;
                    SendStart();
                }
            };
            // OnMessage is dispatched on the main thread (via DispatchMessageQueue /
            // the browser event loop), so it is handled directly to keep audio latency low.
            _socket.OnMessage += ReceiveMessage;
            _socket.OnClose += _ =>
            {
                // ReSharper disable once ArrangeThisQualifier -- Il2Cpp has this as an extension method
                this.StartCoroutine(coroutine());
                return;

                IEnumerator coroutine()
                {
                    yield return null;

                    bool wasReady = IsReady;
                    IsReady = false;
                    if (wasReady) onSpeakingChanged?.Invoke(false);
                    if (!_stopped && !_refused)
                    {
                        // ReSharper disable once ArrangeThisQualifier -- Il2Cpp has this as an extension method
                        this.StartCoroutine(Reconnect());
                    }
                }
            };

            _ = _socket.Connect();
        }

        private void Update()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            _socket?.DispatchMessageQueue();
#endif
        }

        private void OnDestroy()
        {
            if (_instance != this) return;
            _instance = null;
            _stopped = true;
            try
            {
                if (_socket?.State is WebSocketState.Open or WebSocketState.Connecting) _ = _socket.Close();
            }
            catch
            {
                // ignored
            }
        }

        /// <summary>
        /// Derive the voice endpoint from the main websocket URL and the game name:
        /// `.../game/&lt;name&gt;` becomes `.../game/&lt;name&gt;/voice`, `.../game` gets the
        /// game name inserted, and any other path (e.g. a bare host) gets
        /// `/game/&lt;name&gt;/voice` appended. Query parameters (e.g. `?session=`) are kept.
        /// </summary>
        internal static string? DeriveVoiceUrl(string? baseUrl, string? game)
        {
            if (baseUrl is null or "" || game is null or "") return null;

            string url = baseUrl;
            string query = "";
            int queryIndex = url.IndexOf('?');
            if (queryIndex >= 0)
            {
                query = url.Substring(queryIndex);
                url = url.Substring(0, queryIndex);
            }

            url = url.TrimEnd('/');
            string encodedGame = Uri.EscapeDataString(game);

            string path;
            int gameIndex = url.LastIndexOf("/game/", StringComparison.Ordinal);
            if (url.EndsWith("/game", StringComparison.Ordinal))
            {
                path = $"{url}/{encodedGame}/voice";
            }
            else if (gameIndex >= 0 && url.IndexOf('/', gameIndex + "/game/".Length) < 0)
            {
                // Already .../game/<name> — reuse the existing name segment.
                path = $"{url}/voice";
            }
            else
            {
                path = $"{url}/game/{encodedGame}/voice";
            }

            return path + query;
        }

        [Il2CppHide]
        private void SendStart() => SendControl("voice/start", null);

        [Il2CppHide]
        private void SendSpeakerRoster(ICollection<int>? onlyIds = null)
        {
            JArray speakers = new();
            foreach (KeyValuePair<int, string> entry in _speakers)
            {
                if (onlyIds is not null && !onlyIds.Contains(entry.Key)) continue;
                speakers.Add(new JObject { ["id"] = entry.Key, ["name"] = entry.Value });
            }
            if (speakers.Count == 0) return;
            SendControl("voice/speakers/register", new JObject { ["speakers"] = speakers });
        }

        [Il2CppHide]
        private void SendControl(string command, JObject? data)
        {
            if (_socket?.State is not WebSocketState.Open) return;

            JObject message = new()
            {
                ["command"] = command,
                ["game"] = WebsocketConnection.Instance?.game ?? "",
            };
            if (data is not null) message["data"] = data;

            _ = _socket.SendText(message.ToString(Formatting.None));
        }

        [Il2CppHide]
        private void ReceiveMessage(byte[] bytes)
        {
            // NativeWebSocket does not expose whether a frame was text or binary, so
            // sniff: control messages are small JSON objects, PCM frames are large and
            // only start with '{' (0x7B) by coincidence — in that case the JSON parse
            // fails and we fall through to PCM.
            if (bytes.Length > 0 && bytes.Length <= MAX_CONTROL_MESSAGE_BYTES && bytes[0] == (byte)'{')
            {
                if (TryHandleControl(bytes)) return;
            }

            float[]? pcm = VoiceAudio.DecodePcm(bytes);
            if (pcm is null)
            {
                Debug.LogWarning($"Dropped malformed voice frame ({bytes.Length} bytes)");
                return;
            }
            if (pcm.Length > 0) onAudioReceived?.Invoke(pcm);
        }

        [Il2CppHide]
        private bool TryHandleControl(byte[] bytes)
        {
            JObject message;
            try
            {
                message = JObject.Parse(System.Text.Encoding.UTF8.GetString(bytes));
            }
            catch
            {
                return false;
            }

            string? command = message["command"]?.Value<string>();
            JObject? data = message["data"] as JObject;

            switch (command)
            {
                case "voice/ready":
                    Debug.Log("Voice chat ready");
                    IsReady = true;
                    SendSpeakerRoster();
                    onReady?.Invoke();
                    return true;
                case "voice/unavailable":
                    string reason = data?["reason"]?.Value<string>() ?? "Voice chat unavailable";
                    Debug.LogWarning($"Voice chat unavailable: {reason}");
                    _refused = true;
                    IsReady = false;
                    onUnavailable?.Invoke(reason);
                    return true;
                case "voice/speaking":
                    onSpeakingChanged?.Invoke(data?["speaking"]?.Value<bool>() ?? false);
                    return true;
                case "voice/cancelled":
                    onCancelled?.Invoke();
                    return true;
                default:
                    // Unknown command — still a control message, not PCM.
                    return true;
            }
        }
    }
}
