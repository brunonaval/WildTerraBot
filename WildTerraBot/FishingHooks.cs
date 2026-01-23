using HarmonyLib;
using System;

namespace WildTerraBot
{
    internal static class FishingHooks
    {
        [HarmonyPatch(typeof(WTPlayer), "UserCode_TargetShowFishingActions")]
        [HarmonyPostfix]
        static void OnShowActions() { FishBrain.ResetSession(); WTSocketBot.IsFishingBotActive = true; }

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
                object tipObj = __args[1];
                int serverTip = Convert.ToInt32(tipObj);
                bool isSuccess = (bool)__args[2];
                int actionToTake = -1;

                if (isSuccess)
                {
                    actionToTake = serverTip;
                    WTSocketBot.PublicLogger.LogInfo($"[VERDE] Jogo pede: {FishBrain.GetKeyName(actionToTake)}");
                }
                else
                {
                    WTSocketBot.PublicLogger.LogWarning($"[VERMELHO] Proibido: {FishBrain.GetKeyName(serverTip)}");
                    actionToTake = FishBrain.PredictNextMove(serverTip, WTSocketBot.PublicLogger);
                }

                if (actionToTake == 4) actionToTake = 3; // Any -> Wait
                FishBrain.AddHistory(actionToTake);

                if (actionToTake == 3)
                {
                    WTSocketBot.PublicLogger.LogInfo("[SILÊNCIO] Mantendo Wait.");
                    return; // Não envia nada
                }

                if (WTSocketBot.CmdFishingUseMethod != null)
                {
                    object enumValue = Enum.ToObject(tipObj.GetType(), actionToTake);
                    WTSocketBot.PublicLogger.LogMessage($"[ENVIANDO] CmdFishingUse({FishBrain.GetKeyName(actionToTake)})");
                    WTSocketBot.CmdFishingUseMethod.Invoke(__instance, new object[] { enumValue });
                }
            }
            catch { }
        }
    }
}