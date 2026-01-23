using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;

namespace WildTerraBot
{
    public class FishProfile
    {
        public int[] VisualSequence; // A identidade do peixe (Verde)
        public int[] ActionSequence; // O que apertar

        public FishProfile(int[] visual, int[] action)
        {
            VisualSequence = visual;
            ActionSequence = action;
        }
    }

    public static class FishBrain
    {
        public static List<FishProfile> KnownProfiles = new List<FishProfile>();
        public static List<int> SessionVisualHistory = new List<int>(); // Guarda só as verdades (Verdes)

        public static void Initialize()
        {
            KnownProfiles.Clear();

            // === PEIXES DO RIO (WORM) - GABARITO CORRIGIDO ===

            // 1. Crucian
            // Sequência: Lift(1) -> Bite(0)
            // Ação:      Wait(3) -> E(0)
            KnownProfiles.Add(new FishProfile(
                new int[] { 1, 0 },
                new int[] { 3, 0 }
            ));

            // 2. Roach
            // Sequência: Bite(0) -> Bite(0) -> Lift(1)
            // Ação:      Wait(3) -> Wait(3) -> E(0)
            KnownProfiles.Add(new FishProfile(
                new int[] { 0, 0, 1 },
                new int[] { 3, 3, 0 }
            ));

            // 3. Carp
            // Sequência: Bite(0) -> Bite(0) -> Side(2) -> Down(3)
            // Ação:      Wait(3) -> Wait(3) -> T(2)    -> E(0)
            KnownProfiles.Add(new FishProfile(
                new int[] { 0, 0, 2, 3 },
                new int[] { 3, 3, 2, 0 }
            ));

            // 4. Trout
            // Sequência: Bite(0) -> Down(3) -> Side(2) -> Lift(1) -> Drag(4)
            // Ação:      Wait(3) -> Wait(3) -> T(2)    -> R(1)    -> E(0)
            KnownProfiles.Add(new FishProfile(
                new int[] { 0, 3, 2, 1, 4 },
                new int[] { 3, 3, 2, 1, 0 }
            ));
        }

        public static void ResetSession()
        {
            SessionVisualHistory.Clear();
        }

        // Nova Lógica: Usa o histórico para identificar e finaliza o peixe
        public static int PredictFinalMove(ManualLogSource logger)
        {
            // O histórico atual contém a sequência VERDADEIRA até agora (ex: 1, 0)
            int[] currentHistory = SessionVisualHistory.ToArray();

            foreach (var fish in KnownProfiles)
            {
                // Verifica se o histórico bate com o começo desse peixe
                if (IsMatchingHistory(fish.VisualSequence, currentHistory))
                {
                    // ACHAMOS! É ESSE PEIXE.
                    // Se deu vermelho, é hora de executar a AÇÃO FINAL desse peixe.
                    // Pegamos a última tecla cadastrada para ele.
                    int finalAction = fish.ActionSequence.Last();

                    logger.LogWarning($"[IA] Peixe Identificado pelo Histórico ({string.Join(",", currentHistory)}) -> Executando Final: {GetKeyName(finalAction)}");
                    return finalAction;
                }
            }

            logger.LogWarning("[IA] Padrão não reconhecido no histórico. Mantendo Wait.");
            return 3; // Wait
        }

        private static bool IsMatchingHistory(int[] fishSeq, int[] history)
        {
            if (history.Length == 0) return false;
            // Se o histórico é maior que o peixe, não é ele
            if (history.Length > fishSeq.Length) return false;

            for (int i = 0; i < history.Length; i++)
            {
                if (fishSeq[i] != history[i]) return false;
            }
            return true;
        }

        public static void AddToHistory(int visualID)
        {
            SessionVisualHistory.Add(visualID);
        }

        public static string GetKeyName(int val)
        {
            if (val == 0) return "E";
            if (val == 1) return "R";
            if (val == 2) return "T";
            if (val == 3) return "Wait";
            return val.ToString();
        }
    }
}