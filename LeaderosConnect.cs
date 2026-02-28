using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Rocket.API;
using Rocket.API.Collections;
using Rocket.Core.Plugins;
using Rocket.Unturned;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;

// Explicit alias to avoid Logger ambiguity between Rocket.Core.Logging.Logger and UnityEngine.Logger
using RocketLogger = Rocket.Core.Logging.Logger;

namespace LeaderosConnect
{
    public class LeaderosConnect : RocketPlugin<LeaderosConnectConfiguration>
    {
        #region Static Connection Settings
        private const string APP_KEY = "leaderos-connect";
        private const string HOST = "connect-socket.leaderos.net:6001";
        private const string AUTH_ENDPOINT = "https://connect-api.leaderos.net/broadcasting/auth";
        private const int PING_INTERVAL = 30;
        private const int PONG_TIMEOUT = 10;
        private const int RECONNECT_DELAY = 5;
        private const int MAX_RECONNECT_ATTEMPTS = 10;
        private const string QUEUE_FILE = "LeaderosConnect_queue.json";
        #endregion

        #region Singleton
        public static LeaderosConnect Instance { get; private set; }
        #endregion

        #region Fields
        private LeaderosWebSocket _webSocket;
        private string _socketId;
        private bool _isConnected = false;
        private int _reconnectAttempts = 0;
        private bool _shouldReconnect = true;
        private DateTime _lastPongReceived = DateTime.Now;
        private bool _waitingForPong = false;
        private string _channelName;
        private string _socketUrl;

        private Coroutine _pingCoroutine;
        private Coroutine _reconnectCoroutine;

        private Dictionary<string, List<string>> _commandQueue = new Dictionary<string, List<string>>();
        private readonly object _queueLock = new object();
        private string _queueFilePath;

        // Main thread dispatcher
        private readonly Queue<System.Action> _mainThreadQueue = new Queue<System.Action>();
        private readonly object _mainThreadLock = new object();
        #endregion

        #region Plugin Lifecycle
        protected override void Load()
        {
            Instance = this;

            if (!ValidateConfiguration())
            {
                RocketLogger.LogError("[LeaderosConnect] Configuration validation failed. Plugin disabled.");
                return;
            }

            _channelName = $"private-servers.{Configuration.Instance.ServerToken}";
            _socketUrl = $"ws://{HOST}/app/{APP_KEY}?protocol=7&client=unturned-rocket&version=1.0";
            _queueFilePath = Path.Combine(Directory, QUEUE_FILE);

            LoadQueue();

            U.Events.OnPlayerConnected += OnPlayerConnected;

            RocketLogger.Log("[LeaderosConnect] Plugin initialized successfully!");

            StartCoroutine(ConnectDelayed(2f));
        }

        protected override void Unload()
        {
            _shouldReconnect = false;

            U.Events.OnPlayerConnected -= OnPlayerConnected;

            if (_pingCoroutine != null) StopCoroutine(_pingCoroutine);
            if (_reconnectCoroutine != null) StopCoroutine(_reconnectCoroutine);

            SaveQueue();

            _webSocket?.Disconnect();
            _webSocket = null;
            _isConnected = false;

            RocketLogger.Log("[LeaderosConnect] Plugin unloaded.");
        }

        private IEnumerator ConnectDelayed(float delay)
        {
            yield return new WaitForSeconds(delay);
            ConnectToWebSocket();
        }

        private void RunOnMainThread(System.Action action)
        {
            lock (_mainThreadLock)
                _mainThreadQueue.Enqueue(action);
        }

        private void Update()
        {
            lock (_mainThreadLock)
            {
                while (_mainThreadQueue.Count > 0)
                {
                    var action = _mainThreadQueue.Dequeue();
                    try { action(); }
                    catch (Exception ex) { RocketLogger.LogError($"[LeaderosConnect] Main thread error: {ex.Message}"); }
                }
            }
        }

        public override TranslationList DefaultTranslations => new TranslationList();
        #endregion

        #region Configuration Validation
        private bool ValidateConfiguration()
        {
            bool isValid = true;

            if (string.IsNullOrWhiteSpace(Configuration.Instance.WebsiteUrl))
            {
                RocketLogger.LogError("[LeaderosConnect] Website URL is not set.");
                isValid = false;
            }
            else if (Configuration.Instance.WebsiteUrl.StartsWith("http://"))
            {
                RocketLogger.LogError("[LeaderosConnect] Website URL must use HTTPS.");
                isValid = false;
            }

            if (string.IsNullOrWhiteSpace(Configuration.Instance.ApiKey))
            {
                RocketLogger.LogError("[LeaderosConnect] API key is not set.");
                isValid = false;
            }

            if (string.IsNullOrWhiteSpace(Configuration.Instance.ServerToken))
            {
                RocketLogger.LogError("[LeaderosConnect] Server token is not set.");
                isValid = false;
            }

            return isValid;
        }
        #endregion

        #region Player Events
        private void OnPlayerConnected(UnturnedPlayer player)
        {
            try
            {
                if (player == null) return;

                string steamId = player.Id;

                if (Configuration.Instance.DebugMode)
                    RocketLogger.Log($"[LeaderosConnect] Player connected: {player.DisplayName} ({steamId})");

                if (_commandQueue.ContainsKey(steamId))
                    StartCoroutine(ProcessQueueDelayed(steamId, 2f));
            }
            catch (Exception ex)
            {
                RocketLogger.LogError($"[LeaderosConnect] Error in OnPlayerConnected: {ex.Message}");
            }
        }

        private IEnumerator ProcessQueueDelayed(string steamId, float delay)
        {
            yield return new WaitForSeconds(delay);
            ProcessQueueForPlayer(steamId);
        }
        #endregion

        #region Queue Management
        private void LoadQueue()
        {
            try
            {
                if (File.Exists(_queueFilePath))
                {
                    string json = File.ReadAllText(_queueFilePath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        _commandQueue = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json)
                                        ?? new Dictionary<string, List<string>>();
                        RocketLogger.Log($"[LeaderosConnect] Loaded {_commandQueue.Count} queued entries");
                    }
                }
            }
            catch (Exception ex)
            {
                RocketLogger.LogError($"[LeaderosConnect] Error loading queue: {ex.Message}");
                _commandQueue = new Dictionary<string, List<string>>();
            }
        }

        private void SaveQueue()
        {
            try
            {
                lock (_queueLock)
                {
                    string json = JsonConvert.SerializeObject(_commandQueue, Formatting.Indented);
                    File.WriteAllText(_queueFilePath, json);
                }
            }
            catch (Exception ex)
            {
                RocketLogger.LogError($"[LeaderosConnect] Error saving queue: {ex.Message}");
            }
        }

        private void AddToQueue(string steamId, List<string> commands)
        {
            try
            {
                lock (_queueLock)
                {
                    if (_commandQueue.ContainsKey(steamId))
                        _commandQueue[steamId].AddRange(commands);
                    else
                        _commandQueue[steamId] = new List<string>(commands);

                    SaveQueue();
                    RocketLogger.Log($"[LeaderosConnect] Added {commands.Count} commands to queue for: {steamId}");
                }
            }
            catch (Exception ex)
            {
                RocketLogger.LogError($"[LeaderosConnect] Error adding to queue: {ex.Message}");
            }
        }

        private void ProcessQueueForPlayer(string steamId)
        {
            try
            {
                lock (_queueLock)
                {
                    if (!_commandQueue.ContainsKey(steamId)) return;

                    var commands = _commandQueue[steamId];
                    string username = GetPlayerName(steamId);

                    RocketLogger.Log($"[LeaderosConnect] Processing {commands.Count} queued commands for {username}");

                    ExecuteCommands(commands, username);

                    _commandQueue.Remove(steamId);
                    SaveQueue();
                }
            }
            catch (Exception ex)
            {
                RocketLogger.LogError($"[LeaderosConnect] Error processing queue: {ex.Message}");
            }
        }

        private string GetPlayerName(string steamId)
        {
            try
            {
                var player = GetPlayerBySteamId(steamId);
                return player?.DisplayName ?? "Unknown";
            }
            catch { }
            return "Unknown";
        }

        private UnturnedPlayer GetPlayerBySteamId(string steamId)
        {
            try
            {
                foreach (var client in SDG.Unturned.Provider.clients)
                {
                    var unturnedPlayer = UnturnedPlayer.FromSteamPlayer(client);
                    if (unturnedPlayer != null && unturnedPlayer.Id == steamId)
                        return unturnedPlayer;
                }
            }
            catch { }
            return null;
        }

        private bool IsPlayerOnline(string steamId)
        {
            try
            {
                return GetPlayerBySteamId(steamId) != null;
            }
            catch (Exception ex)
            {
                RocketLogger.LogError($"[LeaderosConnect] Error checking player online: {ex.Message}");
            }
            return false;
        }
        #endregion

        #region Command Processing
        private void HandleSendCommandsEvent(object data)
        {
            try
            {
                if (Configuration.Instance.DebugMode)
                    RocketLogger.Log($"[LeaderosConnect] Processing send-commands: {data}");

                if (data == null)
                {
                    RocketLogger.LogWarning("[LeaderosConnect] Null data in send-commands event");
                    return;
                }

                var commandData = JsonConvert.DeserializeObject<CommandEventData>(data.ToString());

                if (commandData?.commands == null || commandData.commands.Length == 0)
                {
                    RocketLogger.LogWarning("[LeaderosConnect] No commands in send-commands event");
                    return;
                }

                ValidateAndExecuteCommands(commandData.commands);
            }
            catch (Exception ex)
            {
                RocketLogger.LogError($"[LeaderosConnect] Error in send-commands: {ex.Message}");
            }
        }

        private void ValidateAndExecuteCommands(string[] commandIds)
        {
            try
            {
                var formValues = new List<string>
                {
                    $"token={Uri.EscapeDataString(Configuration.Instance.ServerToken)}"
                };

                for (int i = 0; i < commandIds.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(commandIds[i])) continue;
                    formValues.Add($"{Uri.EscapeDataString($"commands[{i}]")}={Uri.EscapeDataString(commandIds[i])}");
                }

                string formData = string.Join("&", formValues);
                string validateUrl = $"{Configuration.Instance.WebsiteUrl}/api/command-logs/validate";

                if (Configuration.Instance.DebugMode)
                    RocketLogger.Log($"[LeaderosConnect] Validating at: {validateUrl}");

                Task.Run(() => PostHttpRequest(validateUrl, formData, "application/x-www-form-urlencoded", (code, response) =>
                {
                    if (code == 200)
                        RunOnMainThread(() => HandleValidationResponse(response));
                    else
                        RocketLogger.LogError($"[LeaderosConnect] Validation failed. Code: {code}");
                }));
            }
            catch (Exception ex)
            {
                RocketLogger.LogError($"[LeaderosConnect] Error validating commands: {ex.Message}");
            }
        }

        private void HandleValidationResponse(string response)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(response))
                {
                    RocketLogger.LogWarning("[LeaderosConnect] Empty validation response");
                    return;
                }

                var result = JsonConvert.DeserializeObject<ValidationResponse>(response);

                if (result?.commands == null || result.commands.Length == 0)
                {
                    RocketLogger.LogWarning("[LeaderosConnect] No valid commands from validation");
                    return;
                }

                // Group commands by steamId to avoid mixing players
                var commandsBySteamId = new Dictionary<string, List<string>>();

                foreach (var item in result.commands)
                {
                    if (string.IsNullOrWhiteSpace(item.command) || string.IsNullOrWhiteSpace(item.username))
                        continue;

                    string steamId = item.username; // username field contains steamId

                    if (!commandsBySteamId.ContainsKey(steamId))
                        commandsBySteamId[steamId] = new List<string>();

                    commandsBySteamId[steamId].Add(item.command);
                }

                if (commandsBySteamId.Count == 0)
                {
                    RocketLogger.LogWarning("[LeaderosConnect] No executable commands or missing Steam ID");
                    return;
                }

                foreach (var kvp in commandsBySteamId)
                {
                    string steamId = kvp.Key;
                    List<string> commands = kvp.Value;

                    if (Configuration.Instance.DebugMode)
                        RocketLogger.Log($"[LeaderosConnect] Processing {commands.Count} commands for steamId: {steamId}");

                    if (Configuration.Instance.CheckPlayerOnline)
                    {
                        if (IsPlayerOnline(steamId))
                        {
                            RocketLogger.Log($"[LeaderosConnect] Player {steamId} is online, executing immediately");
                            var cmds = commands;
                            var sid = steamId;
                            RunOnMainThread(() => ExecuteCommands(cmds, GetPlayerName(sid)));
                        }
                        else
                        {
                            RocketLogger.Log($"[LeaderosConnect] Player {steamId} is offline, adding to queue");
                            AddToQueue(steamId, commands);
                        }
                    }
                    else
                    {
                        var cmds = commands;
                        var sid = steamId;
                        RunOnMainThread(() => ExecuteCommands(cmds, GetPlayerName(sid)));
                    }
                }
            }
            catch (Exception ex)
            {
                RocketLogger.LogError($"[LeaderosConnect] Error in validation response: {ex.Message}");
            }
        }

        private void ExecuteCommands(List<string> commands, string username)
        {
            try
            {
                RocketLogger.Log($"[LeaderosConnect] Executing {commands.Count} command(s) for: {username}");
                StartCoroutine(ExecuteCommandsCoroutine(commands, username));
            }
            catch (Exception ex)
            {
                RocketLogger.LogError($"[LeaderosConnect] Error starting command execution: {ex.Message}");
            }
        }

        private IEnumerator ExecuteCommandsCoroutine(List<string> commands, string username)
        {
            foreach (string command in commands)
            {
                if (string.IsNullOrWhiteSpace(command)) { yield return null; continue; }

                try
                {
                    if (Configuration.Instance.DebugMode)
                        RocketLogger.Log($"[LeaderosConnect] Executing: {command}");

                    Rocket.Core.R.Commands.Execute(new Rocket.API.ConsolePlayer(), command);
                    RocketLogger.Log($"[LeaderosConnect] Executed: {command}");
                }
                catch (Exception ex)
                {
                    RocketLogger.LogError($"[LeaderosConnect] Failed '{command}': {ex.Message}");
                }

                yield return new WaitForSeconds(1f);
            }

            RocketLogger.Log($"[LeaderosConnect] All commands done for: {username}");
        }
        #endregion

        #region WebSocket Connection
        private void ConnectToWebSocket()
        {
            if (_reconnectAttempts >= MAX_RECONNECT_ATTEMPTS)
            {
                RocketLogger.LogError("[LeaderosConnect] Max reconnection attempts reached.");
                return;
            }

            try
            {
                RocketLogger.Log($"[LeaderosConnect] Connecting... (Attempt {_reconnectAttempts + 1}/{MAX_RECONNECT_ATTEMPTS})");

                _webSocket?.Disconnect();
                _webSocket = new LeaderosWebSocket(_socketUrl);
                _webSocket.OnOpen += OnWebSocketOpen;
                _webSocket.OnMessage += OnWebSocketMessage;
                _webSocket.OnClose += OnWebSocketClose;
                _webSocket.OnError += OnWebSocketError;
                _webSocket.ConnectAsync();
            }
            catch (Exception ex)
            {
                _reconnectAttempts++;
                RocketLogger.LogError($"[LeaderosConnect] Connection failed: {ex.Message}");
                ScheduleReconnect();
            }
        }

        private void OnWebSocketOpen() => RunOnMainThread(() =>
        {
            _isConnected = true;
            _reconnectAttempts = 0;
            RocketLogger.Log("[LeaderosConnect] WebSocket connected!");

            if (_pingCoroutine != null) StopCoroutine(_pingCoroutine);
            _pingCoroutine = StartCoroutine(KeepAliveCoroutine());
        });

        private void OnWebSocketMessage(string data) => RunOnMainThread(() => HandleMessage(data));

        private void OnWebSocketClose(string reason) => RunOnMainThread(() =>
        {
            RocketLogger.Log($"[LeaderosConnect] WebSocket closed: {reason}");
            HandleConnectionLost();
        });

        private void OnWebSocketError(string error) => RunOnMainThread(() =>
        {
            RocketLogger.LogError($"[LeaderosConnect] WebSocket error: {error}");
            HandleConnectionLost();
        });

        private void HandleConnectionLost()
        {
            _isConnected = false;

            if (_pingCoroutine != null) { StopCoroutine(_pingCoroutine); _pingCoroutine = null; }

            RocketLogger.LogWarning("[LeaderosConnect] Connection lost, reconnecting...");

            if (_shouldReconnect) ScheduleReconnect();
        }

        private void ScheduleReconnect()
        {
            if (_reconnectCoroutine != null) StopCoroutine(_reconnectCoroutine);
            _reconnectAttempts++;
            int delay = Math.Min(RECONNECT_DELAY * _reconnectAttempts, 60);
            if (_reconnectAttempts < MAX_RECONNECT_ATTEMPTS)
            {
                RocketLogger.Log($"[LeaderosConnect] Retrying in {delay}s...");
                _reconnectCoroutine = StartCoroutine(ReconnectDelayed(delay));
            }
        }

        private IEnumerator ReconnectDelayed(int delay)
        {
            yield return new WaitForSeconds(delay);
            ConnectToWebSocket();
        }
        #endregion

        #region Keep-Alive
        private IEnumerator KeepAliveCoroutine()
        {
            _lastPongReceived = DateTime.Now;
            _waitingForPong = false;

            while (_isConnected)
            {
                yield return new WaitForSeconds(PING_INTERVAL);

                if (!_isConnected || _webSocket == null) yield break;

                if (_waitingForPong && (DateTime.Now - _lastPongReceived).TotalSeconds > PONG_TIMEOUT)
                {
                    RocketLogger.LogWarning("[LeaderosConnect] Pong timeout, reconnecting...");
                    HandleConnectionLost();
                    yield break;
                }

                _waitingForPong = true;
                SendPing();

                float elapsed = 0f;
                while (elapsed < PONG_TIMEOUT && _waitingForPong)
                {
                    yield return new WaitForSeconds(1f);
                    elapsed += 1f;
                }

                if (_waitingForPong)
                {
                    RocketLogger.LogWarning("[LeaderosConnect] No pong, reconnecting...");
                    HandleConnectionLost();
                    yield break;
                }
            }
        }

        private void HandlePongReceived()
        {
            _waitingForPong = false;
            _lastPongReceived = DateTime.Now;
            if (Configuration.Instance.DebugMode)
                RocketLogger.Log("[LeaderosConnect] Pong received");
        }
        #endregion

        #region Message Handling
        private void HandleMessage(string data)
        {
            try
            {
                if (Configuration.Instance.DebugMode)
                    RocketLogger.Log($"[LeaderosConnect] Message: {data}");

                if (string.IsNullOrWhiteSpace(data)) return;

                var msg = JsonConvert.DeserializeObject<WebSocketMessage>(data);
                if (msg == null) return;

                switch (msg.@event)
                {
                    case "pusher:connection_established":
                        HandleConnectionEstablished(msg.data);
                        break;
                    case "pusher:subscription_succeeded":
                        RocketLogger.Log($"[LeaderosConnect] Subscribed to: {msg.channel}");
                        break;
                    case "pusher:subscription_error":
                        RocketLogger.LogError($"[LeaderosConnect] Subscription error on channel: {msg.channel}, Data: {msg.data}");
                        break;
                    case "pusher:pong":
                        HandlePongReceived();
                        break;
                    case "ping":
                        RocketLogger.Log("[LeaderosConnect] Ping received from server!");
                        break;
                    case "send-commands":
                        HandleSendCommandsEvent(msg.data);
                        break;
                    default:
                        if (Configuration.Instance.DebugMode)
                            RocketLogger.Log($"[LeaderosConnect] Event: {msg.@event}");
                        break;
                }
            }
            catch (Exception ex)
            {
                RocketLogger.LogError($"[LeaderosConnect] Message handling error: {ex.Message}");
            }
        }

        private void HandleConnectionEstablished(object data)
        {
            try
            {
                if (data == null) return;
                var connData = JsonConvert.DeserializeObject<ConnectionData>(data.ToString());
                _socketId = connData?.socket_id;

                if (!string.IsNullOrEmpty(_socketId))
                {
                    RocketLogger.Log($"[LeaderosConnect] Socket ID: {_socketId}");
                    AuthenticateAndSubscribe();
                }
                else
                {
                    RocketLogger.LogError("[LeaderosConnect] No socket ID received");
                }
            }
            catch (Exception ex)
            {
                RocketLogger.LogError($"[LeaderosConnect] Connection established error: {ex.Message}");
            }
        }
        #endregion

        #region Authentication
        private void AuthenticateAndSubscribe()
        {
            if (string.IsNullOrEmpty(_socketId))
            {
                RocketLogger.LogError("[LeaderosConnect] No socket ID, cannot authenticate.");
                return;
            }

            string jsonData = JsonConvert.SerializeObject(new { socket_id = _socketId, channel_name = _channelName });

            if (Configuration.Instance.DebugMode)
                RocketLogger.Log("[LeaderosConnect] Authenticating...");

            Task.Run(() => PostHttpRequest(AUTH_ENDPOINT, jsonData, "application/json", (code, response) =>
            {
                if (code == 200)
                {
                    RocketLogger.Log("[LeaderosConnect] Auth successful!");
                    RunOnMainThread(() => HandleAuthResponse(response));
                }
                else
                {
                    RocketLogger.LogError($"[LeaderosConnect] Auth failed. Code: {code}, Response: {response}");
                    RunOnMainThread(() => TryDirectSubscription());
                }
            }));
        }

        private void HandleAuthResponse(string response)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(response)) { TryDirectSubscription(); return; }

                var auth = JsonConvert.DeserializeObject<AuthResponse>(response);

                if (auth != null && !string.IsNullOrEmpty(auth.auth))
                {
                    var authToken = auth.auth;
                    var channelData = auth.channel_data;
                    RunOnMainThread(() => SubscribeToChannel(authToken, channelData));
                }
                else
                    TryDirectSubscription();
            }
            catch (Exception ex)
            {
                RocketLogger.LogError($"[LeaderosConnect] Auth response error: {ex.Message}");
                TryDirectSubscription();
            }
        }

        private void SubscribeToChannel(string auth, string channelData = null)
        {
            SendMessage(new { @event = "pusher:subscribe", data = new { auth, channel = _channelName, channel_data = channelData } });
        }

        private void TryDirectSubscription()
        {
            RocketLogger.Log("[LeaderosConnect] Direct subscription...");
            SendMessage(new { @event = "pusher:subscribe", data = new { channel = _channelName } });
        }
        #endregion

        #region Message Sending
        private void SendMessage(object message)
        {
            try
            {
                if (_webSocket == null || !_isConnected)
                {
                    RocketLogger.LogError("[LeaderosConnect] WebSocket not connected.");
                    return;
                }
                _webSocket.Send(JsonConvert.SerializeObject(message));
            }
            catch (Exception ex)
            {
                RocketLogger.LogError($"[LeaderosConnect] Send error: {ex.Message}");
                HandleConnectionLost();
            }
        }

        private void SendPing() => SendMessage(new { @event = "pusher:ping", data = new { } });
        #endregion

        #region HTTP Helper
        public delegate void HttpCallback(int code, string response);

        private void PostHttpRequest(string url, string body, string contentType, HttpCallback callback)
        {
            try
            {
                if (Configuration.Instance.DebugMode)
                {
                    RocketLogger.Log($"[LeaderosConnect] HTTP POST → {url}");
                    RocketLogger.Log($"[LeaderosConnect] HTTP Headers → X-API-Key: {Configuration.Instance.ApiKey?.Substring(0, Math.Min(6, Configuration.Instance.ApiKey?.Length ?? 0))}... | Content-Type: {contentType}");
                    RocketLogger.Log($"[LeaderosConnect] HTTP Body → {body}");
                }

                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = contentType;
                request.Headers["X-API-Key"] = Configuration.Instance.ApiKey;
                request.Accept = "application/json";
                request.Timeout = 10000;

                byte[] bytes = Encoding.UTF8.GetBytes(body);
                request.ContentLength = bytes.Length;

                using (var stream = request.GetRequestStream())
                    stream.Write(bytes, 0, bytes.Length);

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    string responseBody = reader.ReadToEnd();

                    if (Configuration.Instance.DebugMode)
                    {
                        RocketLogger.Log($"[LeaderosConnect] HTTP Response ← Code: {(int)response.StatusCode}");
                        RocketLogger.Log($"[LeaderosConnect] HTTP Response ← Body: {responseBody}");
                    }

                    callback((int)response.StatusCode, responseBody);
                }
            }
            catch (WebException ex)
            {
                int code = -1;
                string resp = "";
                if (ex.Response is HttpWebResponse r)
                {
                    code = (int)r.StatusCode;
                    using (var reader = new StreamReader(r.GetResponseStream()))
                        resp = reader.ReadToEnd();
                }

                if (Configuration.Instance.DebugMode)
                {
                    RocketLogger.LogError($"[LeaderosConnect] HTTP WebException ← Code: {code}");
                    RocketLogger.LogError($"[LeaderosConnect] HTTP WebException ← Body: {resp}");
                    RocketLogger.LogError($"[LeaderosConnect] HTTP WebException ← Message: {ex.Message}");
                }

                callback(code, resp);
            }
            catch (Exception ex)
            {
                RocketLogger.LogError($"[LeaderosConnect] HTTP error: {ex.Message}");
                callback(-1, "");
            }
        }
        #endregion

        #region Public Command Methods
        public void ExecuteStatusCommand(IRocketPlayer caller)
        {
            string status = $"[LeaderosConnect] Connection: {(_isConnected ? "Connected" : "Disconnected")} | " +
                            $"Socket: {_socketId ?? "None"} | " +
                            $"Attempts: {_reconnectAttempts}";
            RocketLogger.Log(status);
        }

        public void ExecuteReconnectCommand(IRocketPlayer caller)
        {
            _reconnectAttempts = 0;
            _webSocket?.Disconnect();
            StartCoroutine(ConnectDelayed(2f));
            RocketLogger.Log("[LeaderosConnect] Reconnecting...");
        }

        public void ExecuteDebugCommand(IRocketPlayer caller)
        {
            Configuration.Instance.DebugMode = !Configuration.Instance.DebugMode;
            Configuration.Save();
            RocketLogger.Log($"[LeaderosConnect] Debug: {(Configuration.Instance.DebugMode ? "ON" : "OFF")}");
        }
        #endregion

        #region Data Classes
        public class WebSocketMessage
        {
            public string @event { get; set; }
            public string channel { get; set; }
            public object data { get; set; }
        }

        public class ConnectionData
        {
            public string socket_id { get; set; }
            public int activity_timeout { get; set; }
        }

        public class AuthResponse
        {
            public string auth { get; set; }
            public string channel_data { get; set; }
        }

        public class CommandEventData
        {
            public string[] commands { get; set; }
        }

        public class ValidationResponse
        {
            public ValidatedCommand[] commands { get; set; }
        }

        public class ValidatedCommand
        {
            public string command { get; set; }
            public string username { get; set; }
        }
        #endregion
    }

    #region WebSocket Client
    public class LeaderosWebSocket
    {
        private System.Net.WebSockets.ClientWebSocket _ws;
        private System.Threading.CancellationTokenSource _cts;
        private readonly string _url;
        private bool _running = false;

        public delegate void OpenHandler();
        public delegate void MessageHandler(string message);
        public delegate void CloseHandler(string reason);
        public delegate void ErrorHandler(string error);

        public event OpenHandler OnOpen;
        public event MessageHandler OnMessage;
        public event CloseHandler OnClose;
        public event ErrorHandler OnError;

        public LeaderosWebSocket(string url) { _url = url; }

        public void ConnectAsync()
        {
            _cts = new System.Threading.CancellationTokenSource();
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    _ws = new System.Net.WebSockets.ClientWebSocket();
                    await _ws.ConnectAsync(new Uri(_url), _cts.Token);
                    _running = true;
                    OnOpen?.Invoke();
                    await ReceiveLoop();
                }
                catch (Exception ex)
                {
                    OnError?.Invoke(ex.Message);
                }
            });
        }

        private async System.Threading.Tasks.Task ReceiveLoop()
        {
            var buffer = new byte[8192];
            var sb = new System.Text.StringBuilder();

            try
            {
                while (_running && _ws.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);

                    if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                    {
                        _running = false;
                        OnClose?.Invoke("Server closed connection");
                        break;
                    }

                    sb.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        OnMessage?.Invoke(sb.ToString());
                        sb.Clear();
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _running = false;
                OnError?.Invoke(ex.Message);
            }
        }

        public void Send(string message)
        {
            if (_ws == null || _ws.State != System.Net.WebSockets.WebSocketState.Open) return;
            var bytes = System.Text.Encoding.UTF8.GetBytes(message);
            _ws.SendAsync(new ArraySegment<byte>(bytes), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
        }

        public void Disconnect()
        {
            try
            {
                _running = false;
                _cts?.Cancel();
                if (_ws?.State == System.Net.WebSockets.WebSocketState.Open)
                    _ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Disconnecting", System.Threading.CancellationToken.None).Wait(2000);
                _ws?.Dispose();
                _ws = null;
            }
            catch { }
        }
    }
    #endregion
}