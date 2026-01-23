using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace WildTerraBot
{
    // ENUMS
    public enum FishBite { Bite = 0, Lift = 1, Side = 2, Down = 3, Drag = 4 }
    public enum FishingUse { DragOut = 0, Pull = 1, Strike = 2, None = 3, Any = 4 }

    [BepInPlugin("com.seunick.wildterra.socket", "WT UDP Clean 123", "9.123.0")]
    public class WTSocketBot : BaseUnityPlugin
    {
        public static WTSocketBot Instance;
        public static ManualLogSource PublicLogger;
        public static bool IsFishingBotActive = false;
        public static MethodInfo CmdFishingUseMethod = null;
        public static bool HasDumped = false;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this.gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(this.gameObject);
            PublicLogger = Logger;

            // Inicializa Banco de Dados
            FishBrain.Initialize();

            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var typeWT = Type.GetType("WTPlayer, Assembly-CSharp");
                if (typeWT != null) CmdFishingUseMethod = typeWT.GetMethod("CmdFishingUse", flags);

                Harmony.CreateAndPatchAll(typeof(PlayerHooks));
                Harmony.CreateAndPatchAll(typeof(FishingHooks));
                Logger.LogInfo(">>> BOT 9.123: ORGANIZED CODE + SECRET DUMP READY <<<");
            }
            catch (Exception ex) { Logger.LogError($"[CRITICAL] Erro no Awake: {ex.Message}"); }
        }

        void Update()
        {
            // Gatilho F9 Manual
            if (Input.GetKeyDown(KeyCode.F9))
            {
                DumpDeepSecrets("MANUAL F9");
            }
        }

        // === O NOVO DUMP (LÊ O SEGREDO NA MEMÓRIA) ===
        public void DumpDeepSecrets(string origin = "AUTO")
        {
            try
            {
                PublicLogger.LogWarning($">>> [{origin}] DUMP PROFUNDO INICIADO... <<<");
                var fishes = Resources.LoadAll<WTFish>("");

                if (fishes == null || fishes.Length == 0)
                {
                    PublicLogger.LogWarning("Nenhum peixe encontrado nos Resources."); return;
                }

                var ordered = fishes
                    .Where(f => f != null)
                    .OrderBy(f => (f.itemTypes != null && f.itemTypes.Length > 0 && f.itemTypes[0] != null) ? f.itemTypes[0].name : f.name)
                    .ToArray();

                PublicLogger.LogInfo($"[DUMP] Lendo {ordered.Length} peixes. Procurando 'useToStayHooked'...");

                foreach (var fish in ordered)
                {
                    string fishName = (fish.itemTypes != null && fish.itemTypes.Length > 0 && fish.itemTypes[0] != null) ? fish.itemTypes[0].name : fish.name;
                    var sb = new StringBuilder();
                    sb.Append($"[PEIXE] {fishName} SEQ: ");

                    if (fish.actions != null)
                    {
                        for (int i = 0; i < fish.actions.Length; i++)
                        {
                            var a = fish.actions[i];
                            if (a == null) continue;

                            // LÊ OS DOIS CAMPOS PARA DESCOBRIR O PADRÃO REAL
                            int mainVal = (int)a.useToCatch;       // Verde
                            int altVal = (int)a.useToStayHooked;   // Segredo (Vermelho/Safe)

                            string mainKey = FishBrain.GetKeyName(mainVal);
                            string altKey = FishBrain.GetKeyName(altVal);
                            string bite = a.fishBite.ToString();

                            sb.Append($" -> [{bite} : {mainKey} / Alt:{altKey}]");
                        }
                    }
                    PublicLogger.LogInfo(sb.ToString());
                }
                PublicLogger.LogWarning(">>> DUMP FINALIZADO. COPIE O LOG! <<<");
                HasDumped = true;
            }
            catch (Exception ex) { PublicLogger.LogError("[DUMP] Erro Fatal: " + ex); }
        }
    }
}