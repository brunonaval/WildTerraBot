using BepInEx.Logging;
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using UnityEngine;

namespace WildTerraBot
{
    internal static class LicenseGate
    {
        [Serializable]
        private class VerifyRequest
        {
            public string licenseKey;
            public string deviceId;
            public string app;
            public string ver;
        }

        [Serializable]
        private class VerifyResponse
        {
            public bool active;
            public string validUntilUtc;
            public string serverTimeUtc;
            public string message;
            public string reason;
        }

        // HttpClient reusável (evita leak de socket)
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        /// <summary>
        /// Gera um deviceId estável baseado no deviceUniqueIdentifier do Unity (hash SHA-256, truncado).
        /// </summary>
        internal static string MakeDeviceId()
        {
            var raw = SystemInfo.deviceUniqueIdentifier ?? "unknown-device";
            // hash curto e seguro para URL/DB
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(raw);
                var hash = sha.ComputeHash(bytes);
                // 16 bytes => 32 hex chars (curto o bastante)
                var sb = new StringBuilder(32);
                for (int i = 0; i < 16; i++)
                    sb.Append(hash[i].ToString("x2"));
                return "WT-" + sb.ToString();
            }
        }

        /// <summary>
        /// Valida a licença no seu servidor (POST /license/verify).
        /// - 1 licença = 1 device (o servidor faz o bind automático no 1º uso)
        /// - se já estiver vinculada a outro device, retorna false (device_limit_reached)
        /// </summary>
        public static bool Validar(ManualLogSource log)
        {
            try
            {
                // força TLS 1.2+ (alguns runtimes antigos do Unity precisam disso)
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                }
                catch { /* ignore */ }

                // lê config do plugin
                var api = (WTSocketBot.ApiBaseUrl?.Value ?? "https://wildterralicensing.onrender.com").Trim().TrimEnd('/');
                var licenseKey = (WTSocketBot.LicenseKey?.Value ?? "").Trim();
                var deviceId = (WTSocketBot.DeviceId?.Value ?? "").Trim();
                var app = (WTSocketBot.AppName?.Value ?? "wildterra-bot").Trim();
                var ver = (WTSocketBot.AppVersion?.Value ?? "1.0.0").Trim();

                if (string.IsNullOrWhiteSpace(licenseKey))
                {
                    log.LogError("[LICENSING] LicenseKey vazio. Abra o arquivo de config do BepInEx e cole sua chave.");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(deviceId))
                {
                    deviceId = MakeDeviceId();
                    // tenta persistir de volta (se já estiver bindado no Awake, aqui não deve acontecer)
                    try
                    {
                        if (WTSocketBot.DeviceId != null)
                            WTSocketBot.DeviceId.Value = deviceId;
                    }
                    catch { /* ignore */ }
                }

                var url = api + "/license/verify";

                var req = new VerifyRequest
                {
                    licenseKey = licenseKey,
                    deviceId = deviceId,
                    app = app,
                    ver = ver
                };

                var json = JsonUtility.ToJson(req);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var resp = _http.PostAsync(url, content).GetAwaiter().GetResult();
                var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (!resp.IsSuccessStatusCode)
                {
                    log.LogError($"[LICENSING] HTTP {(int)resp.StatusCode} ao validar licença. Resposta: {body}");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(body))
                {
                    log.LogError("[LICENSING] Resposta vazia do servidor ao validar licença.");
                    return false;
                }

                VerifyResponse data;
                try
                {
                    data = JsonUtility.FromJson<VerifyResponse>(body);
                }
                catch (Exception ex)
                {
                    log.LogError($"[LICENSING] Falha ao interpretar JSON do servidor. Body: {body}");
                    log.LogError(ex);
                    return false;
                }

                if (data != null && data.active)
                {
                    log.LogInfo($"[LICENSING] OK ({data.reason ?? "ok"}) — válida até {data.validUntilUtc} (UTC).");
                    return true;
                }

                var reason = data?.reason ?? "unknown";
                var msg = data?.message ?? "License invalid";
                log.LogError($"[LICENSING] NEGADO ({reason}): {msg}");
                return false;
            }
            catch (Exception ex)
            {
                log.LogError("[LICENSING] Erro inesperado ao validar licença:");
                log.LogError(ex);
                return false;
            }
        }
    }
}
