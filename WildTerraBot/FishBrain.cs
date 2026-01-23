using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;

namespace WildTerraBot
{
    public static class FishBrain
    {
        public static Dictionary<string, int[]> KnownPatterns = new Dictionary<string, int[]>();
        public static List<int> SessionHistory = new List<int>();

        public static void Initialize()
        {
            // Padrões conhecidos (PDF/Docx)
            KnownPatterns["Jellyfish"] = new int[] { 0 };
            KnownPatterns["Shrimp"] = new int[] { 2, 0 };
            KnownPatterns["Alburnus"] = new int[] { 3, 2 };
            KnownPatterns["Crucian"] = new int[] { 3, 0 };
            KnownPatterns["Roach"] = new int[] { 3, 3, 0 };
            KnownPatterns["Carp"] = new int[] { 3, 3, 2, 0 };
            KnownPatterns["Pike"] = new int[] { 3, 2, 1, 0 };
            KnownPatterns["SeaBass"] = new int[] { 3, 2, 0, 1 };
            KnownPatterns["Trout"] = new int[] { 3, 3, 2, 1, 0 };
            KnownPatterns["Halibut"] = new int[] { 3, 1, 1, 0 };
            KnownPatterns["Cod"] = new int[] { 3, 3, 3, 0 };
            KnownPatterns["Catfish"] = new int[] { 1, 1, 1 };
            KnownPatterns["Flounder"] = new int[] { 1, 2, 2, 2 };
            KnownPatterns["Salmon"] = new int[] { 3, 3, 1, 3, 2 };
            KnownPatterns["Pufferfish"] = new int[] { 3, 3, 2, 3, 1 };
            KnownPatterns["Wolffish"] = new int[] { 1, 2, 1, 3, 2, 1 };
            KnownPatterns["Piranha"] = new int[] { 3, 3, 2 };
        }

        public static void ResetSession() { if (SessionHistory.Count > 0) SessionHistory.Clear(); }

        public static void AddHistory(int action)
        {
            if (SessionHistory.Count == 0 || SessionHistory.Last() != action) SessionHistory.Add(action);
        }

        public static int PredictNextMove(int forbiddenKey, ManualLogSource logger)
        {
            if (SessionHistory.Count == 0) return 3; // Wait

            var candidates = new List<int>();
            foreach (var kvp in KnownPatterns)
            {
                int[] seq = kvp.Value;
                if (seq.Length > SessionHistory.Count)
                {
                    bool match = true;
                    for (int i = 0; i < SessionHistory.Count; i++) if (seq[i] != SessionHistory[i]) { match = false; break; }

                    if (match)
                    {
                        int nextStep = seq[SessionHistory.Count];
                        if (nextStep != forbiddenKey) candidates.Add(nextStep);
                    }
                }
            }

            if (candidates.Count > 0)
            {
                var bestMove = candidates.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
                logger.LogWarning($"[IA] Decisão (Baseada em {candidates.Count} peixes): {GetKeyName(bestMove)}");
                return bestMove;
            }
            return 3; // Wait
        }

        public static string GetKeyName(int val)
        {
            if (val == 0) return "E";
            if (val == 1) return "R";
            if (val == 2) return "T";
            if (val == 3) return "Wait";
            if (val == 4) return "Any";
            return val.ToString();
        }
    }
}