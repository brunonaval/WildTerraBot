using System;
using UnityEngine;

namespace WildTerraBot
{
    public static class WTBankingUtils
    {
        public static WTStructure FindBestBankChest(Vector3 anchorPos, float radius, string partialName = "")
        {
            WTStructure best = null;
            float bestSqr = radius * radius;

            WTObject[] structures = WTObject.GetStructures();
            if (structures == null) return null;

            foreach (var s in structures)
            {
                if (!(s is WTStructure ws) || !ws.isActiveAndEnabled) continue;

                if (!string.IsNullOrEmpty(partialName) &&
                    ws.name.IndexOf(partialName, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (ws.worldType == null || ws.worldType.containerSlots <= 0) continue;

                float sqr = (ws.transform.position - anchorPos).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    best = ws;
                    bestSqr = sqr;
                }
            }

            return best;
        }
    }
}
