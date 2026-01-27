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

                // Índice do passo atual (0-based) dentro da sequência do peixe
                int stepIndex = FishBrain.BeginStep(visualID);

                // Se já estivermos travados em um peixe, fazemos uma checagem de sanidade usando a cor do UI
                if (FishBrain.IsLocked)
                {
                    int lockedAction = FishBrain.GetLockedAction(stepIndex);

                    // Vermelho: a dica do jogo é errada. Se a dica errada coincidir com nossa ação travada, o lock está incorreto.
                    if (!isSuccess && lockedAction == tipID)
                    {
                        WTSocketBot.PublicLogger.LogWarning($"[IA][LOCK] Passo {stepIndex}: UI marcou '{FishBrain.GetKeyName(tipID)}' como VERMELHO, mas esta era a ação travada. Destravando e recalculando...");
                        FishBrain.LockedProfile = null;
                    }

                    // Verde: a dica do jogo é correta. Se ela divergir do lock, o lock está incorreto.
                    if (isSuccess && lockedAction != tipID)
                    {
                        WTSocketBot.PublicLogger.LogWarning($"[IA][LOCK] Passo {stepIndex}: UI confirmou '{FishBrain.GetKeyName(tipID)}' como VERDE, mas o lock previa '{FishBrain.GetKeyName(lockedAction)}'. Destravando e voltando a confiar no UI...");
                        FishBrain.LockedProfile = null;
                    }
                }

                if (isSuccess)
                {
                    // === VERDE (Confirmação) ===
                    FishBrain.RecordConfirmedStep(stepIndex, visualID, tipID);

                    actionToTake = tipID;
                    WTSocketBot.PublicLogger.LogInfo($"[VERDE] Passo: {stepIndex} | Visual: {visualID} ({FishBrain.GetVisualName(visualID)}) | OK: {FishBrain.GetKeyName(actionToTake)}");

                    // Tenta travar o peixe se a dedução ficar única
                    FishBrain.EvaluateLockOnGreen(stepIndex, visualID, tipID, WTSocketBot.PublicLogger);

                    if (FishBrain.IsLocked)
                    {
                        // A partir daqui, seguimos a sequência do peixe travado (para este passo costuma ser igual ao tipID)
                        actionToTake = FishBrain.GetLockedAction(stepIndex);
                        WTSocketBot.PublicLogger.LogWarning($"[IA][LOCK] Passo {stepIndex}: Executando {FishBrain.GetKeyName(actionToTake)} (seguindo sequência completa).");
                    }
                }
                else
                {
                    // === VERMELHO (Eliminação) ===
                    WTSocketBot.PublicLogger.LogWarning($"[VERMELHO] Passo: {stepIndex} | Visual: {visualID} ({FishBrain.GetVisualName(visualID)}) | Dica ERRADA: {FishBrain.GetKeyName(tipID)}. Filtrando...");

                    if (FishBrain.IsLocked)
                    {
                        actionToTake = FishBrain.GetLockedAction(stepIndex);
                        WTSocketBot.PublicLogger.LogWarning($"[IA][LOCK] Passo {stepIndex}: Executando {FishBrain.GetKeyName(actionToTake)} (ignorando dica vermelha).");
                    }
                    else
                    {
                        // Passamos o ID da tecla MENTIROSA para a IA filtrar
                        actionToTake = FishBrain.PredictMoveWithElimination(stepIndex, visualID, tipID, WTSocketBot.PublicLogger);

                        if (FishBrain.IsLocked)
                        {
                            actionToTake = FishBrain.GetLockedAction(stepIndex);
                            WTSocketBot.PublicLogger.LogWarning($"[IA][LOCK] Passo {stepIndex}: Travado em '{FishBrain.LockedProfile.Name}'. Executando {FishBrain.GetKeyName(actionToTake)} e seguindo sequência completa.");
                        }
                    }
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