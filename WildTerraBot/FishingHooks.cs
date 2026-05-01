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
                        WTSocketBot.PublicLogger.LogWarning(string.Format(WildTerraBot.Properties.Resources.FishingHooksLockRedMismatchFormat, stepIndex, FishBrain.GetKeyName(tipID)));
                        FishBrain.LockedProfile = null;
                    }

                    // Verde: a dica do jogo é correta. Se ela divergir do lock, o lock está incorreto.
                    if (isSuccess && lockedAction != tipID)
                    {
                        WTSocketBot.PublicLogger.LogWarning(string.Format(WildTerraBot.Properties.Resources.FishingHooksLockGreenMismatchFormat, stepIndex, FishBrain.GetKeyName(tipID), FishBrain.GetKeyName(lockedAction)));
                        FishBrain.LockedProfile = null;
                    }
                }

                if (isSuccess)
                {
                    // === VERDE (Confirmação) ===
                    FishBrain.RecordConfirmedStep(stepIndex, visualID, tipID);

                    actionToTake = tipID;
                    WTSocketBot.PublicLogger.LogInfo(string.Format(WildTerraBot.Properties.Resources.FishingHooksGreenStepFormat, stepIndex, visualID, FishBrain.GetVisualName(visualID), FishBrain.GetKeyName(actionToTake)));

                    // Tenta travar o peixe se a dedução ficar única
                    FishBrain.EvaluateLockOnGreen(stepIndex, visualID, tipID, WTSocketBot.PublicLogger);

                    if (FishBrain.IsLocked)
                    {
                        // A partir daqui, seguimos a sequência do peixe travado (para este passo costuma ser igual ao tipID)
                        actionToTake = FishBrain.GetLockedAction(stepIndex);
                        WTSocketBot.PublicLogger.LogWarning(string.Format(WildTerraBot.Properties.Resources.FishingHooksLockExecutingFullSequenceFormat, stepIndex, FishBrain.GetKeyName(actionToTake)));
                    }
                }
                else
                {
                    // === VERMELHO (Eliminação) ===
                    WTSocketBot.PublicLogger.LogWarning(string.Format(WildTerraBot.Properties.Resources.FishingHooksRedStepWrongHintFormat, stepIndex, visualID, FishBrain.GetVisualName(visualID), FishBrain.GetKeyName(tipID)));

                    if (FishBrain.IsLocked)
                    {
                        actionToTake = FishBrain.GetLockedAction(stepIndex);
                        WTSocketBot.PublicLogger.LogWarning(string.Format(WildTerraBot.Properties.Resources.FishingHooksLockExecutingIgnoringRedHintFormat, stepIndex, FishBrain.GetKeyName(actionToTake)));
                    }
                    else
                    {
                        // Passamos o ID da tecla MENTIROSA para a IA filtrar
                        actionToTake = FishBrain.PredictMoveWithElimination(stepIndex, visualID, tipID, WTSocketBot.PublicLogger);

                        if (FishBrain.IsLocked)
                        {
                            actionToTake = FishBrain.GetLockedAction(stepIndex);
                            WTSocketBot.PublicLogger.LogWarning(string.Format(WildTerraBot.Properties.Resources.FishingHooksLockProfileExecutingFullSequenceFormat, stepIndex, FishBrain.LockedProfile.Name, FishBrain.GetKeyName(actionToTake)));
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
