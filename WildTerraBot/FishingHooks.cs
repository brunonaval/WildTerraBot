using HarmonyLib;
using System;

namespace WildTerraBot
{
    internal static class FishingHooks
    {
        [HarmonyPatch(typeof(WTPlayer), "UserCode_TargetShowFishingActions")]
        [HarmonyPostfix]
        static void OnShowActions() { FishBrain.ResetSession(); }

        [HarmonyPatch(typeof(WTPlayer), "UserCode_TargetAddFishingResult")]
        [HarmonyPostfix]
        static void OnResult() { FishBrain.ResetSession(); }

        [HarmonyPatch(typeof(WTPlayer), "UserCode_TargetAddFishAction")]
        [HarmonyPostfix]
        static void OnUserCodeFishAction(WTPlayer __instance, object[] __args)
        {
            if (!WTSocketBot.IsFishingBotActive) return;
            if (__args == null || __args.Length < 3) return;

            try
            {
                object visualObj = __args[0];
                object tipObj = __args[1];
                bool isSuccess = (bool)__args[2]; // True=Verde, False=Vermelho

                int visualID = Convert.ToInt32(visualObj);
                int tipID = Convert.ToInt32(tipObj); // A tecla sugerida

                int actionToTake = -1;

                if (isSuccess)
                {
                    // === VERDE (Confirmação) ===
                    actionToTake = tipID;
                    FishBrain.AddToHistory(visualID);
                    WTSocketBot.PublicLogger.LogInfo($"[VERDE] Visual: {visualID} | OK: {FishBrain.GetKeyName(actionToTake)}");
                }
                else
                {
                    // === VERMELHO (Eliminação) ===
                    // Passamos o ID da tecla MENTIROSA para a IA filtrar
                    WTSocketBot.PublicLogger.LogWarning($"[VERMELHO] Jogo mentiu sugerindo: {FishBrain.GetKeyName(tipID)}. Filtrando...");

                    actionToTake = FishBrain.PredictMoveWithElimination(tipID, WTSocketBot.PublicLogger);
                }

                if (actionToTake == 4) actionToTake = 3;

                if (actionToTake != 3) // Se não for Wait, executa
                {
                    if (WTSocketBot.CmdFishingUseMethod != null)
                    {
                        object enumValue = Enum.ToObject(tipObj.GetType(), actionToTake);
                        WTSocketBot.CmdFishingUseMethod.Invoke(__instance, new object[] { enumValue });
                    }
                }
            }
            catch { }
        }
    }
}