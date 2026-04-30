using HarmonyLib;

namespace WildTerraBot
{
    /// <summary>
    /// Hook para detectar o RESULTADO real do gather (o jogo manda via TargetRPC).
    /// Usamos isso para:
    ///  - encerrar estado de coleta no bot
    ///  - liberar rota (para não ficar preso em auto-move)
    /// </summary>
    [HarmonyPatch(typeof(WTPlayer), "UserCode_TargetGatherResult")]
    internal static class HarvestHooks
    {
        [HarmonyPostfix]
        static void Postfix(WTPlayer __instance, JobResult gatherResult)
        {
            try
            {
                if (UDPRunner.Instance != null)
                    UDPRunner.Instance.OnGatherResult(__instance, gatherResult);
            }
            catch { }
        }
    }
}
