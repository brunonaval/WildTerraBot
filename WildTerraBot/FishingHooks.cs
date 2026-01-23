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
            // Apenas reseta a sessão, não ativa o bot (controle manual permitido)
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
            // Trava de segurança: Se o Dashboard não mandou ligar, não faz nada.
            if (!WTSocketBot.IsFishingBotActive) return;

            if (__args == null || __args.Length < 3) return;

            try
            {
                // === CORREÇÃO: Declarando as variáveis corretamente ===
                object visualObj = __args[0]; // Visual (0=Bite, 1=Lift...)
                object tipObj = __args[1];    // Dica do Jogo (Objeto Enum) - NECESSÁRIO NO FINAL
                bool isSuccess = (bool)__args[2];

                int visualID = Convert.ToInt32(visualObj);
                int tipID = Convert.ToInt32(tipObj); // Valor numérico da dica

                int actionToTake = -1;

                if (isSuccess)
                {
                    // === VERDE (Verdade) ===
                    actionToTake = tipID;

                    // Adiciona ao histórico (Só confiamos no verde para identificar o peixe)
                    FishBrain.AddToHistory(visualID);

                    WTSocketBot.PublicLogger.LogInfo($"[VERDE] Visual: {visualID} | Ação: {FishBrain.GetKeyName(actionToTake)}");
                }
                else
                {
                    // === VERMELHO (Mentira) ===
                    WTSocketBot.PublicLogger.LogWarning($"[VERMELHO] O jogo tentou enganar com Visual {visualID}. Consultando IA...");

                    // Chama a IA para decidir com base no histórico que já temos
                    actionToTake = FishBrain.PredictFinalMove(WTSocketBot.PublicLogger);
                }

                if (actionToTake == 4) actionToTake = 3; // Any vira Wait

                // Execução
                if (actionToTake == 3)
                {
                    WTSocketBot.PublicLogger.LogInfo("[SILÊNCIO] Mantendo Wait.");
                    return;
                }

                if (WTSocketBot.CmdFishingUseMethod != null)
                {
                    // CORREÇÃO: Aqui usamos o tipObj.GetType() que agora existe!
                    object enumValue = Enum.ToObject(tipObj.GetType(), actionToTake);

                    WTSocketBot.PublicLogger.LogMessage($"[ENVIANDO] CmdFishingUse({FishBrain.GetKeyName(actionToTake)})");
                    WTSocketBot.CmdFishingUseMethod.Invoke(__instance, new object[] { enumValue });
                }
            }
            catch { }
        }
    }
}