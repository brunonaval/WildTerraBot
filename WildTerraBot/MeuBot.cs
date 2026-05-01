using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic; // Necessário para HashSet
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace WildTerraBot
{
    // ENUMS GLOBAIS
    public enum FishBite { Bite = 0, Lift = 1, Side = 2, Down = 3, Drag = 4 }
    public enum FishingUse { DragOut = 0, Pull = 1, Strike = 2, None = 3, Any = 4 }

    [BepInPlugin("com.seunick.wildterra.socket", "WT UDP Final 126", "9.126.0")]
    public class WTSocketBot : BaseUnityPlugin
    {
        public const string PluginVersion = "1.0.0";
        public static WTSocketBot Instance;
        public static ManualLogSource PublicLogger;
        // ====== LICENSING (API) ======
        internal static ConfigEntry<bool> EnableLicensing;
        internal static ConfigEntry<string> ApiBaseUrl;
        internal static ConfigEntry<string> LicenseKey;
        internal static ConfigEntry<string> DeviceId;
        internal static ConfigEntry<string> AppName;
        internal static ConfigEntry<string> AppVersion;
        public static bool IsFishingBotActive = false; // Começa FALSO para permitir jogo manual
        public static MethodInfo CmdFishingUseMethod = null;
        public static bool HasDumped = false;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this.gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(this.gameObject);
            PublicLogger = Logger;


            // ====== LICENSING (API) ======
            EnableLicensing = Config.Bind("Licensing", "EnableLicensing", true,
                WildTerraBot.Properties.Resources.MeuBotConfigEnableLicensingDescription);

            ApiBaseUrl = Config.Bind("Licensing", "ApiBaseUrl", "https://wildterralicensing.onrender.com",
                WildTerraBot.Properties.Resources.MeuBotConfigApiBaseUrlDescription);

            LicenseKey = Config.Bind("Licensing", "LicenseKey", "",
                WildTerraBot.Properties.Resources.MeuBotConfigLicenseKeyDescription);

            DeviceId = Config.Bind("Licensing", "DeviceId", "",
                WildTerraBot.Properties.Resources.MeuBotConfigDeviceIdDescription);

            AppName = Config.Bind("Licensing", "AppName", "wildterra-bot",
                WildTerraBot.Properties.Resources.MeuBotConfigAppNameDescription);

            AppVersion = Config.Bind("Licensing", "AppVersion", PluginVersion,
                WildTerraBot.Properties.Resources.MeuBotConfigAppVersionDescription);

            // gera/persiste DeviceId 1x
            if (string.IsNullOrWhiteSpace(DeviceId.Value))
            {
                DeviceId.Value = LicenseGate.MakeDeviceId();
                Config.Save();
            }

            // --- LICENÇA PRIMEIRO ---
            if (EnableLicensing.Value)
            {
                if (!LicenseGate.Validar(Logger))
                {
                    Logger.LogError(WildTerraBot.Properties.Resources.MeuBotLicenseInvalidBotDisabled);
                    Destroy(this.gameObject);
                    return;
                }
            }
            else
            {
                Logger.LogWarning(WildTerraBot.Properties.Resources.MeuBotLicensingDevMode);
            }

            // Só depois de licenciado

            DontDestroyOnLoad(this.gameObject);

            FishBrain.Initialize();

            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var typeWT = Type.GetType("WTPlayer, Assembly-CSharp");
                if (typeWT != null) CmdFishingUseMethod = typeWT.GetMethod("CmdFishingUse", flags);

                Harmony.CreateAndPatchAll(typeof(PlayerHooks));
                Harmony.CreateAndPatchAll(typeof(FishingHooks));
                Harmony.CreateAndPatchAll(typeof(SkillUseLogger));
                Harmony.CreateAndPatchAll(typeof(HarvestHooks));
                Logger.LogInfo(WildTerraBot.Properties.Resources.MeuBotLicenseOkHarmonyActive);
            }
            catch (Exception ex) { Logger.LogError(string.Format(WildTerraBot.Properties.Resources.MeuBotCriticalErrorFormat, ex.Message)); }
        }

        void Update()
        {
            // Gatilho Manual F9 para ler os peixes da cena
            if (Input.GetKeyDown(KeyCode.F9))
            {
                PublicLogger.LogWarning(WildTerraBot.Properties.Resources.MeuBotF9DetectedScanning);
                DumpFromScene("MANUAL F9");
            }
        }

        // === NOVO DUMP: LÊ DO CENÁRIO (WTWorldFishing) ===
        // Isso resolve o problema de Resources.LoadAll vir vazio
        public void DumpFromScene(string origin = "AUTO")
        {
            try
            {
                PublicLogger.LogWarning(string.Format(WildTerraBot.Properties.Resources.MeuBotScanningFishingAreasFormat, origin));

                // Procura todos os pontos de pesca ativos no mundo ao redor
                // Isso pega direto da memória RAM do que está carregado
                var fishingSpots = FindObjectsOfType<WTWorldFishing>();

                if (fishingSpots == null || fishingSpots.Length == 0)
                {
                    PublicLogger.LogWarning(WildTerraBot.Properties.Resources.MeuBotDumpNoFishingSpots);
                    return;
                }

                PublicLogger.LogInfo(string.Format(WildTerraBot.Properties.Resources.MeuBotDumpFishingSpotsFoundFormat, fishingSpots.Length));

                HashSet<string> processedFish = new HashSet<string>();

                foreach (var spot in fishingSpots)
                {
                    if (spot.fishingArea == null || spot.fishingArea.fishes == null) continue;

                    PublicLogger.LogInfo(string.Format(WildTerraBot.Properties.Resources.MeuBotDumpAreaFormat, spot.fishingArea.name));

                    foreach (var fish in spot.fishingArea.fishes)
                    {
                        if (fish == null) continue;

                        // Nome do peixe (item ou nome direto)
                        string fishName = (fish.itemTypes != null && fish.itemTypes.Length > 0 && fish.itemTypes[0] != null) ? fish.itemTypes[0].name : fish.name;

                        // Evita duplicatas no log (mesmo peixe em várias áreas)
                        if (processedFish.Contains(fishName)) continue;
                        processedFish.Add(fishName);

                        var sb = new StringBuilder();
                        sb.Append(string.Format(WildTerraBot.Properties.Resources.MeuBotDumpFishSequencePrefixFormat, fishName));

                        if (fish.actions != null)
                        {
                            for (int i = 0; i < fish.actions.Length; i++)
                            {
                                var a = fish.actions[i];
                                if (a == null) continue;

                                // === O GABARITO OFICIAL (USE TO STAY HOOKED) ===
                                int mainVal = (int)a.useToCatch;       // Verde
                                int altVal = (int)a.useToStayHooked;   // Vermelho/Safe

                                string mainKey = FishBrain.GetKeyName(mainVal);
                                string altKey = FishBrain.GetKeyName(altVal);
                                string bite = a.fishBite.ToString();

                                sb.Append(string.Format(WildTerraBot.Properties.Resources.MeuBotDumpFishActionFormat, bite, mainKey, altKey));
                            }
                        }
                        PublicLogger.LogInfo(sb.ToString());
                    }
                }
                PublicLogger.LogWarning(WildTerraBot.Properties.Resources.MeuBotDumpSceneCompleted);
                HasDumped = true;
            }
            catch (Exception ex) { PublicLogger.LogError(string.Format(WildTerraBot.Properties.Resources.MeuBotDumpFatalErrorFormat, ex)); }
        }
    }
}
