using System;
using System.Collections.Generic;
using System.Text;
using Crestron.SimplSharp;
using Crestron.SimplSharp.Net.Http;
using Crestron.SimplSharp.Net.Https;
using Newtonsoft.Json;
using PifflersCrestronLibrary.Logger;

namespace PifflersCrestronLibrary.Devices.Aten
{
    /// <summary>
    /// Aten VP2730 (Video-Matrix, HTTPS-REST + SocketIO).
    /// Aktuell abgedeckt aus dem Contract der Pausenhalle: Routing (Input->Output),
    /// Video-Freeze und Video-Mute (Blank) pro Output. State-Feedback per Polling
    /// (SocketIO-Anbindung kommt in v2, wenn wir Push-Events brauchen).
    ///
    /// Auth-Flow: POST /api/v2.0/auth/tokens mit Body {"authorization":"base64(user:pass)"}
    /// -> credential im Response; alle folgenden Requests tragen Header
    /// <c>Authorization: &lt;credential&gt;</c>. Bei 401 wird einmal automatisch reauthentifiziert.
    ///
    /// Referenz: APIs/vp2730_restful_api_2021-01-14.pdf (im Projekt-Repo).
    /// </summary>
    public class VP2730 : IDisposable
    {
        private const string ApiBase = "/api/v2.0";
        private const int DefaultPollMs = 3000;

        private readonly string host;
        private readonly string user;
        private readonly string pass;
        private readonly string friendlyName;
        private readonly HttpsClient http;

        private string credential;                            // aktuell gültiger API-Token
        private CTimer pollTimer;
        private int pollMs = DefaultPollMs;

        // State-Cache: output-id -> aktueller Zustand
        private readonly Dictionary<string, int> lastInputByOutput = new Dictionary<string, int>();
        private readonly Dictionary<string, bool> lastFreezeByOutput = new Dictionary<string, bool>();
        private readonly Dictionary<string, bool> lastMuteByOutput = new Dictionary<string, bool>();

        // 1 = Aus (default), 2 = An -- laut API-PDF sind die validen Werte {1,2}. Bei
        // Bedarf im Feld-Test drehen (siehe SetFreezeOnValue/SetMuteOnValue).
        private int freezeOnValue = 2;
        private int freezeOffValue = 1;
        private int muteOnValue = 2;
        private int muteOffValue = 1;

        public delegate void RouteChangedDelegate(string outputId, int inputId);
        public event RouteChangedDelegate RouteChanged;

        public delegate void OutputBoolChangedDelegate(string outputId, bool value);
        public event OutputBoolChangedDelegate FreezeChanged;
        public event OutputBoolChangedDelegate MuteChanged;

        public bool Authenticated { get { return !string.IsNullOrEmpty(credential); } }

        public VP2730(string host, string user, string pass, string friendlyName = "VP2730")
        {
            this.host = (host ?? string.Empty).Trim();
            this.user = user ?? string.Empty;
            this.pass = pass ?? string.Empty;
            this.friendlyName = string.IsNullOrWhiteSpace(friendlyName) ? "VP2730" : friendlyName;

            http = new HttpsClient
            {
                HostVerification = false,          // Aten liefert Self-Signed-Cert
                PeerVerification = false,
                KeepAlive = true,
                TimeoutEnabled = true,
                Timeout = 10,
            };

            Debug.Log("[VP2730] [" + this.friendlyName + "] created (" + this.host + ")");
        }

        /// <summary>Konfiguriert die Roh-Werte fuer Freeze on/off (default {2,1}). Nur setzen, wenn Feld-Test es verlangt.</summary>
        public void ConfigureFreezeValues(int on, int off) { freezeOnValue = on; freezeOffValue = off; }
        public void ConfigureMuteValues(int on, int off)   { muteOnValue = on;   muteOffValue = off;   }

        // ---------- Public API ----------------------------------------------

        public bool Connect()
        {
            if (Authenticate())
            {
                StartPolling();
                return true;
            }
            return false;
        }

        public void Disconnect()
        {
            StopPolling();
            credential = null;
        }

        /// <summary>Route Input auf Output. Beide IDs entsprechen der Config (video.sources / video.outputs).</summary>
        public bool Route(int inputId, string outputId)
        {
            var body = "{\"connections\":[{\"id\":\"" + outputId + "\",\"videoInput\":\"" + inputId + "\"}]}";
            var ok = Patch(ApiBase + "/video/connections", body);
            if (ok)
            {
                lastInputByOutput[outputId] = inputId;
                var handler = RouteChanged; if (handler != null) handler(outputId, inputId);
            }
            return ok;
        }

        public bool SetFreeze(string outputId, bool on) { return SetOutputField(outputId, "freeze", on ? freezeOnValue : freezeOffValue, on, lastFreezeByOutput, FreezeChanged); }
        public bool SetMute(string outputId, bool on)   { return SetOutputField(outputId, "blank",  on ? muteOnValue  : muteOffValue,   on, lastMuteByOutput,   MuteChanged);   }

        public int  GetLastInput(string outputId)  { int v;  return lastInputByOutput.TryGetValue(outputId, out v)  ? v : 0; }
        public bool GetLastFreeze(string outputId) { bool v; return lastFreezeByOutput.TryGetValue(outputId, out v) && v; }
        public bool GetLastMute(string outputId)   { bool v; return lastMuteByOutput.TryGetValue(outputId, out v)   && v; }

        // ---------- Auth ----------------------------------------------------

        private bool Authenticate()
        {
            try
            {
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + pass));
                var body = "{\"authorization\":\"" + b64 + "\"}";
                var status = RawSend("POST", ApiBase + "/auth/tokens", body, includeAuth: false, out var response);
                if (status < 200 || status >= 300)
                {
                    Debug.Error("[VP2730] [" + friendlyName + "] Auth failed: HTTP " + status);
                    credential = null; return false;
                }
                var parsed = JsonConvert.DeserializeObject<Dictionary<string, string>>(response);
                if (parsed == null || !parsed.TryGetValue("credential", out credential) || string.IsNullOrEmpty(credential))
                {
                    Debug.Error("[VP2730] [" + friendlyName + "] Auth response ohne credential: " + response);
                    credential = null; return false;
                }
                Debug.Log("[VP2730] [" + friendlyName + "] authenticated.");
                return true;
            }
            catch (Exception e)
            {
                Debug.Error("[VP2730] [" + friendlyName + "] Authenticate Exception: " + e.Message);
                credential = null; return false;
            }
        }

        // ---------- HTTP ----------------------------------------------------

        private bool Patch(string path, string body)   { return SendJson("PATCH",  path, body); }
        private bool Post(string path, string body)    { return SendJson("POST",   path, body); }
        private bool Get(string path, out string body) { var s = RawSend("GET", path, null, includeAuth: true, out body); return s >= 200 && s < 300; }

        private bool SendJson(string method, string path, string body)
        {
            var status = RawSend(method, path, body, includeAuth: true, out _);
            if (status == 401)
            {
                Debug.Warn("[VP2730] [" + friendlyName + "] 401 -> reauthenticate + retry");
                if (!Authenticate()) return false;
                status = RawSend(method, path, body, includeAuth: true, out _);
            }
            return status >= 200 && status < 300;
        }

        private int RawSend(string method, string path, string body, bool includeAuth, out string response)
        {
            response = string.Empty;
            try
            {
                var req = new HttpsClientRequest
                {
                    Url = new UrlParser("https://" + host + path),
                    RequestType = MethodToRequestType(method),
                    KeepAlive = true,
                };
                req.Header.SetHeaderValue("Content-Type", "application/json");
                req.Header.SetHeaderValue("Accept", "application/json");
                if (includeAuth && !string.IsNullOrEmpty(credential))
                    req.Header.SetHeaderValue("Authorization", credential);
                if (!string.IsNullOrEmpty(body))
                    req.ContentString = body;

                var res = http.Dispatch(req);
                if (res == null) { Debug.Error("[VP2730] [" + friendlyName + "] " + method + " " + path + " -> null response"); return -1; }
                response = res.ContentString ?? string.Empty;
                return res.Code;
            }
            catch (Exception e)
            {
                Debug.Error("[VP2730] [" + friendlyName + "] " + method + " " + path + " Exception: " + e.Message);
                return -1;
            }
        }

        private static Crestron.SimplSharp.Net.Https.RequestType MethodToRequestType(string method)
        {
            switch ((method ?? "GET").ToUpperInvariant())
            {
                case "GET":    return Crestron.SimplSharp.Net.Https.RequestType.Get;
                case "POST":   return Crestron.SimplSharp.Net.Https.RequestType.Post;
                case "PATCH":  return Crestron.SimplSharp.Net.Https.RequestType.Patch;
                case "PUT":    return Crestron.SimplSharp.Net.Https.RequestType.Put;
                case "DELETE": return Crestron.SimplSharp.Net.Https.RequestType.Delete;
                default:       return Crestron.SimplSharp.Net.Https.RequestType.Get;
            }
        }

        // ---------- Polling / State-Diff ------------------------------------

        public void SetPollingInterval(int ms) { pollMs = Math.Max(500, ms); if (pollTimer != null) StartPolling(); }

        private void StartPolling()
        {
            StopPolling();
            pollTimer = new CTimer(_ => PollOnce(), null, pollMs, pollMs);
        }

        private void StopPolling()
        {
            if (pollTimer != null) { pollTimer.Stop(); pollTimer.Dispose(); pollTimer = null; }
        }

        private void PollOnce()
        {
            // Routing
            if (Get(ApiBase + "/video/connections", out var connectionsJson))
                ParseConnections(connectionsJson);
            // Outputs (freeze/blank)
            if (Get(ApiBase + "/video/outputs", out var outputsJson))
                ParseOutputs(outputsJson);
        }

        private void ParseConnections(string json)
        {
            try
            {
                var parsed = JsonConvert.DeserializeObject<Dictionary<string, List<Dictionary<string, string>>>>(json);
                if (parsed == null || !parsed.ContainsKey("connections")) return;
                foreach (var c in parsed["connections"])
                {
                    if (!c.TryGetValue("id", out var id)) continue;
                    if (!c.TryGetValue("videoInput", out var vi) || !int.TryParse(vi, out var input)) continue;
                    int prev; var known = lastInputByOutput.TryGetValue(id, out prev);
                    if (!known || prev != input)
                    {
                        lastInputByOutput[id] = input;
                        var h = RouteChanged; if (h != null) h(id, input);
                    }
                }
            }
            catch (Exception e) { Debug.Error("[VP2730] ParseConnections: " + e.Message); }
        }

        private void ParseOutputs(string json)
        {
            try
            {
                // Struktur: {"outputs":[{"id":"0","freeze":{"value":1},"blank":{"value":1}, ...}, ...]}
                var root = JsonConvert.DeserializeObject<Dictionary<string, List<Dictionary<string, object>>>>(json);
                if (root == null || !root.ContainsKey("outputs")) return;
                foreach (var o in root["outputs"])
                {
                    if (!o.TryGetValue("id", out var idObj)) continue;
                    var id = idObj != null ? idObj.ToString() : null;
                    if (id == null) continue;
                    var freezeVal = ReadNestedValue(o, "freeze");
                    var blankVal  = ReadNestedValue(o, "blank");
                    if (freezeVal.HasValue) UpdateBool(id, freezeVal.Value == freezeOnValue, lastFreezeByOutput, FreezeChanged);
                    if (blankVal.HasValue)  UpdateBool(id, blankVal.Value  == muteOnValue,   lastMuteByOutput,   MuteChanged);
                }
            }
            catch (Exception e) { Debug.Error("[VP2730] ParseOutputs: " + e.Message); }
        }

        private static int? ReadNestedValue(Dictionary<string, object> parent, string key)
        {
            if (!parent.TryGetValue(key, out var raw) || raw == null) return null;
            var nested = raw as Newtonsoft.Json.Linq.JObject;
            if (nested == null) return null;
            var value = nested["value"];
            return value != null ? (int?)value.ToObject<int>() : null;
        }

        private void UpdateBool(string outputId, bool newVal, Dictionary<string, bool> cache, OutputBoolChangedDelegate ev)
        {
            bool prev; var known = cache.TryGetValue(outputId, out prev);
            if (!known || prev != newVal)
            {
                cache[outputId] = newVal;
                if (ev != null) ev(outputId, newVal);
            }
        }

        // ---------- Output-Set (Freeze/Blank) -------------------------------

        private bool SetOutputField(string outputId, string field, int rawValue, bool desiredBool, Dictionary<string, bool> cache, OutputBoolChangedDelegate ev)
        {
            var body = "{\"outputs\":[{\"id\":\"" + outputId + "\",\"" + field + "\":{\"value\":" + rawValue + "}}]}";
            var ok = Patch(ApiBase + "/video/outputs", body);
            if (ok)
            {
                cache[outputId] = desiredBool;
                if (ev != null) ev(outputId, desiredBool);
            }
            return ok;
        }

        public void Dispose()
        {
            StopPolling();
        }
    }
}
