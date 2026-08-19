class_name NeuroVoiceChat
extends Node

## Optional voice chat side-channel for games with built-in voice chat.
##
## This opens a second websocket next to the main Neuro API connection, over
## which the game streams each remote player's voice (with attribution) so Neuro
## can hear them, and receives Neuro's voice to feed into the game's voice chat
## transmit path.
##
## See the voice chat API documentation for the wire protocol. Voice chat is
## strictly optional: if the server does not support it, [signal voice_unavailable]
## is emitted once and this node stays idle — your game must work without it.
##
## Usage:
## [codeblock]
## var voice := NeuroVoiceChat.new()
## add_child(voice)
## voice.start_voice()
## [/codeblock]

signal voice_ready
signal voice_unavailable(reason: String)
## Neuro started/stopped talking. Use it to key push-to-talk in the game's voice chat.
signal speaking_changed(speaking: bool)
## Neuro's speech was interrupted. Immediately discard any buffered audio and release push-to-talk.
signal speech_cancelled
## A chunk of Neuro's voice as 48 kHz mono PCM. Feed it into the game's voice
## chat transmit path. Do not also play it locally.
signal audio_received(samples: PackedFloat32Array)

const WIRE_SAMPLE_RATE := 48000
const SPEAKER_FRAME_VERSION := 1
const SPEAKER_FRAME_HEADER_BYTES := 4
const RECONNECT_INTERVAL := 3.0

var is_voice_ready: bool = false

var _socket: WebSocketPeer
var _handshake_sent := false
var _speakers := {}
var _next_speaker_id := 1
var _stopped := false
var _refused := false


func _process(_delta: float) -> void:
	if _socket == null:
		return

	_socket.poll()
	var state: int = _socket.get_ready_state()

	match state:
		WebSocketPeer.STATE_OPEN:
			if not _handshake_sent:
				_handshake_sent = true
				_send_start()
			_read_packets()

		WebSocketPeer.STATE_CLOSED:
			var was_ready := is_voice_ready
			is_voice_ready = false
			_socket = null
			if was_ready:
				speaking_changed.emit(false)
			if not _stopped and not _refused:
				_reconnect()


## Connect the voice chat websocket and handshake. Call again after
## [method stop_voice] to rejoin voice chat later.
func start_voice() -> void:
	_stopped = false
	_refused = false
	_ws_start()


## Leave voice chat: sends voice/stop and closes the connection.
func stop_voice() -> void:
	_stopped = true
	is_voice_ready = false
	if _socket != null and _socket.get_ready_state() == WebSocketPeer.STATE_OPEN:
		_send_control("voice/stop")
		_socket.close()
	_socket = null


## Register a remote player as a voice chat speaker and return the id to tag
## their audio with. Do not register Neuro's own player. The name is what her
## speech recognition attributes the audio to, so use the player's display name.
func register_speaker(speaker_name: String) -> int:
	var id := _next_speaker_id
	_next_speaker_id += 1
	_speakers[id] = speaker_name
	if is_voice_ready:
		_send_roster([id])
	return id


## Rename a registered speaker (e.g. the player changed display name).
func rename_speaker(id: int, speaker_name: String) -> void:
	if not _speakers.has(id):
		push_warning("rename_speaker called for unknown speaker id %d" % id)
		return
	_speakers[id] = speaker_name
	if is_voice_ready:
		_send_roster([id])


## Unregister a speaker (the player left the lobby).
func unregister_speaker(id: int) -> void:
	if not _speakers.erase(id):
		return
	if is_voice_ready:
		_send_control("voice/speakers/unregister", {"ids": [id]})


## Send a chunk of one speaker's voice audio. Only call this while the player is
## actually transmitting (your voice chat layer's voice activity / push-to-talk
## state) — do not stream continuous silence. Input is interleaved PCM at any
## sample rate / channel count; it is converted to 48 kHz mono before sending.
## Recommended chunk size is 20 ms; anything between 10 ms and 100 ms is fine.
func send_speaker_audio(id: int, samples: PackedFloat32Array, sample_rate: int = WIRE_SAMPLE_RATE, channels: int = 1) -> void:
	if not is_voice_ready or _socket == null or _socket.get_ready_state() != WebSocketPeer.STATE_OPEN:
		return
	if not _speakers.has(id):
		push_warning("send_speaker_audio called for unknown speaker id %d" % id)
		return

	var wire := _to_wire_format(samples, sample_rate, channels)
	if wire.is_empty():
		return

	var frame := PackedByteArray()
	frame.resize(SPEAKER_FRAME_HEADER_BYTES)
	frame.encode_u8(0, SPEAKER_FRAME_VERSION)
	frame.encode_u8(1, 0)
	frame.encode_u16(2, id)
	frame.append_array(wire.to_byte_array())
	_socket.send(frame, WebSocketPeer.WRITE_MODE_BINARY)


func _ws_start() -> void:
	if _socket != null:
		var state: int = _socket.get_ready_state()
		if state == WebSocketPeer.STATE_OPEN or state == WebSocketPeer.STATE_CONNECTING:
			_socket.close()

	var voice_url := _derive_voice_url(OS.get_environment("NEURO_SDK_WS_URL"), NeuroSdkConfig.game)
	if voice_url == "":
		var reason := "Could not derive the voice chat websocket URL. Make sure NEURO_SDK_WS_URL is set and NeuroSdkConfig.game is not empty."
		push_warning(reason)
		voice_unavailable.emit(reason)
		return

	_handshake_sent = false
	_socket = WebSocketPeer.new()

	var err: Error = _socket.connect_to_url(voice_url)
	if err != OK:
		push_warning("Could not connect to voice chat websocket, error code %d" % [err])
		_socket = null
		_reconnect()


func _reconnect() -> void:
	await get_tree().create_timer(RECONNECT_INTERVAL).timeout
	if _stopped or _refused or not is_inside_tree():
		return
	_ws_start()


func _read_packets() -> void:
	while _socket != null and _socket.get_available_packet_count():
		var packet: PackedByteArray = _socket.get_packet()
		if _socket.was_string_packet():
			_handle_control(packet.get_string_from_utf8())
		else:
			if packet.size() % 4 != 0:
				push_warning("Dropped malformed voice frame (%d bytes)" % packet.size())
				continue
			var samples := packet.to_float32_array()
			if samples.size() > 0:
				audio_received.emit(samples)


func _handle_control(message_str: String) -> void:
	var json := JSON.new()
	if json.parse(message_str) != OK or typeof(json.data) != TYPE_DICTIONARY:
		push_warning("Could not parse voice chat message: %s" % message_str)
		return

	var message := IncomingData.new(json.data)
	var command := message.get_string("command")
	var data := message.get_object("data", {})

	match command:
		"voice/ready":
			print("Voice chat ready")
			is_voice_ready = true
			_send_roster()
			voice_ready.emit()
		"voice/unavailable":
			var reason := data.get_string("reason", "Voice chat unavailable")
			push_warning("Voice chat unavailable: %s" % reason)
			_refused = true
			is_voice_ready = false
			voice_unavailable.emit(reason)
		"voice/speaking":
			speaking_changed.emit(data.get_boolean("speaking"))
		"voice/cancelled":
			speech_cancelled.emit()


func _send_start() -> void:
	_send_control("voice/start")


func _send_roster(only_ids: Array = []) -> void:
	var speakers := []
	for id in _speakers:
		if not only_ids.is_empty() and not only_ids.has(id):
			continue
		speakers.append({"id": id, "name": _speakers[id]})
	if speakers.is_empty():
		return
	_send_control("voice/speakers/register", {"speakers": speakers})


func _send_control(command: String, data: Dictionary = {}) -> void:
	if _socket == null or _socket.get_ready_state() != WebSocketPeer.STATE_OPEN:
		return

	var message := {
		"command": command,
		"game": NeuroSdkConfig.game,
	}
	if not data.is_empty():
		message["data"] = data

	var err: int = _socket.send_text(JSON.stringify(message))
	if err != OK:
		push_warning("Could not send voice chat message %s, error code %d" % [command, err])


## Derive the voice endpoint from the main websocket URL and the game name:
## `.../game/<name>` becomes `.../game/<name>/voice`, `.../game` gets the game
## name inserted, and any other path (e.g. a bare host) gets
## `/game/<name>/voice` appended. Query parameters (e.g. `?session=`) are kept.
static func _derive_voice_url(base_url: String, game: String) -> String:
	if base_url == "" or game == "":
		return ""

	var url := base_url
	var query := ""
	var query_index := url.find("?")
	if query_index >= 0:
		query = url.substr(query_index)
		url = url.substr(0, query_index)

	url = url.rstrip("/")
	var encoded_game := game.uri_encode()

	var path: String
	var game_index := url.rfind("/game/")
	if url.ends_with("/game"):
		path = "%s/%s/voice" % [url, encoded_game]
	elif game_index >= 0 and url.find("/", game_index + "/game/".length()) < 0:
		# Already .../game/<name> — reuse the existing name segment.
		path = "%s/voice" % url
	else:
		path = "%s/game/%s/voice" % [url, encoded_game]

	return path + query


static func _to_wire_format(samples: PackedFloat32Array, sample_rate: int, channels: int) -> PackedFloat32Array:
	if channels < 1 or sample_rate < 1:
		return PackedFloat32Array()

	var mono := samples
	if channels > 1:
		@warning_ignore("integer_division")
		var frames := samples.size() / channels
		mono = PackedFloat32Array()
		mono.resize(frames)
		for i in frames:
			var sum := 0.0
			for c in channels:
				sum += samples[i * channels + c]
			mono[i] = sum / channels

	if sample_rate == WIRE_SAMPLE_RATE:
		return mono

	var out_length := int(float(mono.size()) * WIRE_SAMPLE_RATE / sample_rate)
	if out_length <= 0:
		return PackedFloat32Array()

	var resampled := PackedFloat32Array()
	resampled.resize(out_length)
	var step := float(sample_rate) / WIRE_SAMPLE_RATE
	for i in out_length:
		var pos := i * step
		var i0 := int(pos)
		var i1: int = min(i0 + 1, mono.size() - 1)
		var frac := pos - i0
		resampled[i] = mono[i0] + (mono[i1] - mono[i0]) * frac
	return resampled
