using HarmonyLib;
using System;

namespace WildTerraBot
{
    internal static class FishingHooks
    {
        [HarmonyPatch(typeof(WTPlayer), "UserCode_TargetShowFishingActions")]
        [HarmonyPostfix]
        static void OnShowActions()
        {
            FishBrain.ResetSession();
        }

        [HarmonyPatch(typeof(WTPlayer), "UserCode_TargetAddFishingResult")]
        [HarmonyPostfix]
        static void OnResult()
        {
            FishBrain.ResetSession();
        }

        [HarmonyPatch(typeof(WTPlayer), "UserCode_TargetAddFishAction")]
        [HarmonyPostfix]
        static void OnUserCodeFishAction(WTPlayer __instance, object[] __args)
        {
            // Trava do Dashboard
            if (!WTSocketBot.IsFishingBotActive) return;

            if (__args == null || __args.Length < 3) return;

            try
            {
                // ARGUMENTOS:
                // [0] = FishBite (A Figura Visual: Bite, Lift, etc)
                // [1] = FishingUse (A Tecla Sugerida: E, R, T...)
                // [2] = bool (Sucesso/Verde ou Falha/Vermelho)

                object visualObj = __args[0]; // NOVO: Pegamos o visual
                object tipObj = __args[1];    // A tecla

                int visualID = Convert.ToInt32(visualObj); // 0=Bite, 1=Lift...
                int serverTip = Convert.ToInt32(tipObj);   // 0=E, 1=R...
                bool isSuccess = (bool)__args[2];

                int actionToTake = -1;

                if (isSuccess)
                {
                    // VERDE: Pode confiar na dica
                    actionToTake = serverTip;
                    WTSocketBot.PublicLogger.LogInfo($"[VERDE] Visual: {visualID} | Jogo pede: {FishBrain.GetKeyName(actionToTake)}");
                }
                else
                {
                    // VERMELHO: O servidor mente.
                    // Usamos o Visual ID atual + Histórico Visual para descobrir a verdade.
                    WTSocketBot.PublicLogger.LogWarning($"[VERMELHO] Visual: {visualID} | Jogo mentiu pedindo: {FishBrain.GetKeyName(serverTip)}");
                    actionToTake = FishBrain.PredictNextMove(visualID, serverTip, WTSocketBot.PublicLogger);
                }

                if (actionToTake == 4) actionToTake = 3;

                // Atualiza histórico com o Visual que vimos
                FishBrain.AddHistory(visualID);

                //xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx

                // Executa
                if (actionToTake == 3)
                {
                    WTSocketBot.PublicLogger.LogInfo("[SILÊNCIO] Mantendo Wait.");
                    return;
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