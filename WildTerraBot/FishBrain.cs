using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;

namespace WildTerraBot
{
    // Estrutura para relacionar o Visual com a Ação
    public class FishProfile
    {
        public int[] VisualSequence; // A figura que aparece (0=Bite, 1=Lift...)
        public int[] ActionSequence; // O botão correto (0=E, 3=Wait...)

        public FishProfile(int[] visual, int[] action)
        {
            VisualSequence = visual;
            ActionSequence = action;
        }
    }

    public static class FishBrain
    {
        public static List<FishProfile> KnownProfiles = new List<FishProfile>();
        public static List<int> SessionVisualHistory = new List<int>(); // Histórico das FIGURAS vistas
        public static int StepsCount = 0;

        public static void Initialize()
        {
            KnownProfiles.Clear();

            // === PEIXES DO RIO (ISCA: WORM) ===

            // 1. Crucian (Crucian)
            // Visual: Lift(1) -> Down(3)
            // Ação:   Wait(3) -> E(0)
            KnownProfiles.Add(new FishProfile(
                new int[] { 1, 3 },
                new int[] { 3, 0 }
            ));

            // 2. Roach (Pardelha)
            // Visual: Bite(0) -> Bite(0) -> Lift(1)
            // Ação:   Wait(3) -> Wait(3) -> E(0)
            KnownProfiles.Add(new FishProfile(
                new int[] { 0, 0, 1 },
                new int[] { 3, 3, 0 }
            ));

            // 3. Carp (Carpa)
            // Visual: Bite(0) -> Bite(0) -> Side(2) -> Down(3)
            // Ação:   Wait(3) -> Wait(3) -> T(2)    -> E(0)
            KnownProfiles.Add(new FishProfile(
                new int[] { 0, 0, 2, 3 },
                new int[] { 3, 3, 2, 0 }
            ));

            // 4. Trout (Truta)
            // Visual: Bite(0) -> Down(3) -> Side(2) -> Lift(1) -> Drag(4)
            // Ação:   Wait(3) -> Wait(3) -> T(2)    -> R(1)    -> E(0)
            KnownProfiles.Add(new FishProfile(
                new int[] { 0, 3, 2, 1, 4 },
                new int[] { 3, 3, 2, 1, 0 }
            ));
        }

        public static void ResetSession()
        {
            SessionVisualHistory.Clear();
            StepsCount = 0;
        }

        // Prevê o movimento com base no visual atual e no histórico
        public static int PredictNextMove(int currentVisualID, int forbiddenKey, ManualLogSource logger)
        {
            // Cria uma lista temporária com o histórico + visual atual para conferência
            var currentCheck = new List<int>(SessionVisualHistory);
            currentCheck.Add(currentVisualID);
            int currentStepIndex = currentCheck.Count - 1;

            foreach (var fish in KnownProfiles)
            {
                // Se o peixe tem menos passos do que já demos, ignora
                if (fish.VisualSequence.Length <= currentStepIndex) continue;

                // Verifica se a sequência visual bate perfeitamente até agora
                bool match = true;
                for (int i = 0; i <= currentStepIndex; i++)
                {
                    if (fish.VisualSequence[i] != currentCheck[i])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    // ACHAMOS O PEIXE PELO VISUAL!
                    // A resposta é a ação correspondente no mesmo index
                    int correctAction = fish.ActionSequence[currentStepIndex];

                    logger.LogWarning($"[IA] Peixe Identificado! Visual: {currentVisualID} -> Ação Correta: {GetKeyName(correctAction)}");
                    return correctAction;
                }
            }

            // Se não achou (ou for o primeiro passo sem match), retorna Wait por segurança
            return 3;
        }

        public static void AddHistory(int visualID)
        {
            SessionVisualHistory.Add(visualID);
            StepsCount++;
        }

        public static string GetKeyName(int val)
        {
            if (val == 0) return "E";
            if (val == 1) return "R";
            if (val == 2) return "T";
            if (val == 3) return "Wait";
            if (val == 4) return "Drag"; // Apenas visual, não é tecla
            return val.ToString();
        }
    }
}