using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;

namespace WildTerraBot
{
    public class FishProfile
    {
        public string Name;
        public int[] VisualSequence;
        public int[] ActionSequence;

        public FishProfile(string name, int[] visual, int[] action)
        {
            Name = name;
            VisualSequence = visual;
            ActionSequence = action;
        }
    }

    public static class FishBrain
    {
        public static List<FishProfile> ActiveProfiles = new List<FishProfile>();
        public static List<int> SessionVisualHistory = new List<int>();

        // Constantes Visuais
        private const int BITE = 0, LIFT = 1, SIDE = 2, DOWN = 3, DRAG = 4;
        // Constantes Ações
        private const int E = 0, R = 1, T = 2, WAIT = 3;

        public static void Initialize() { ActiveProfiles.Clear(); }
        public static void ResetSession() { SessionVisualHistory.Clear(); }

        // --- CONFIGURA O CONTEXTO (RIO, OCEANO OU DESERTO) ---
        public static void SetContext(string location, string bait)
        {
            ActiveProfiles.Clear();
            location = location.Trim().ToLower();
            bait = bait.Trim().ToLower();

            if (location == "river")
            {
                LoadRiverProfiles(bait);
            }
            else if (location == "ocean")
            {
                LoadOceanProfiles(bait);
            }
            else if (location == "desert")
            {
                LoadDesertProfiles(bait);
            }
        }

        // === PERFIS DO RIO ===
        private static void LoadRiverProfiles(string bait)
        {
            var pCrucian = new FishProfile("Crucian", new[] { LIFT, BITE }, new[] { WAIT, E });
            var pRoach = new FishProfile("Roach", new[] { BITE, BITE, LIFT }, new[] { WAIT, WAIT, E });
            var pCarp = new FishProfile("Carp", new[] { BITE, BITE, SIDE, DOWN }, new[] { WAIT, WAIT, T, E });
            var pTrout = new FishProfile("Trout", new[] { BITE, DOWN, SIDE, LIFT, DRAG }, new[] { WAIT, WAIT, T, R, E });
            var pAlburnus = new FishProfile("Alburnus", new[] { DRAG }, new[] { E });
            var pIde = new FishProfile("Ide", new[] { BITE, BITE, DRAG }, new[] { WAIT, WAIT, E });
            var pPike = new FishProfile("Pike", new[] { DRAG, SIDE, DRAG, DRAG }, new[] { WAIT, T, R, E });
            var pCatFish = new FishProfile("CatFish", new[] { SIDE, DOWN, DRAG, DRAG, LIFT }, new[] { WAIT, R, R, WAIT, E });

            switch (bait)
            {
                case "worms":
                    ActiveProfiles.Add(pCrucian);
                    ActiveProfiles.Add(pRoach);
                    ActiveProfiles.Add(pCarp);
                    ActiveProfiles.Add(pTrout);
                    break;
                case "maggots":
                    ActiveProfiles.Add(pAlburnus);
                    ActiveProfiles.Add(pCrucian);
                    ActiveProfiles.Add(pRoach);
                    break;
                case "peas":
                    ActiveProfiles.Add(pCarp);
                    ActiveProfiles.Add(pIde);
                    break;
                case "crucian":
                case "roach":
                    ActiveProfiles.Add(pPike);
                    break;
                case "alburnus":
                    ActiveProfiles.Add(pTrout);
                    ActiveProfiles.Add(pCatFish);
                    break;
                case "shrimp":
                    ActiveProfiles.Add(pCarp);
                    ActiveProfiles.Add(pTrout);
                    ActiveProfiles.Add(pPike);
                    break;
            }
        }

        // === PERFIS DO OCEANO ===
        private static void LoadOceanProfiles(string bait)
        {
            var pJellyFish = new FishProfile("JellyFish", new[] { SIDE }, new[] { E });
            var pSprat = new FishProfile("Sprat", new[] { BITE }, new[] { E });
            var pCapelin = new FishProfile("Capelin", new[] { BITE, SIDE }, new[] { WAIT, E });
            var pShrimpFish = new FishProfile("Shrimp", new[] { SIDE }, new[] { E });
            var pBlackHalibut = new FishProfile("Black Halibut", new[] { BITE, LIFT, SIDE, DOWN }, new[] { WAIT, R, R, E });
            var pWhiteHalibut = new FishProfile("White Halibut", new[] { BITE, LIFT, SIDE, DOWN, DOWN }, new[] { WAIT, R, R, WAIT, E });
            var pSeaBass = new FishProfile("SeaBass", new[] { DOWN, LIFT, DRAG }, new[] { WAIT, T, R });
            var pCod = new FishProfile("Cod", new[] { DOWN, SIDE, DOWN }, new[] { WAIT, WAIT, E });

            switch (bait)
            {
                case "worms":
                    ActiveProfiles.Add(pJellyFish);
                    break;
                case "maggots":
                    ActiveProfiles.Add(pJellyFish);
                    ActiveProfiles.Add(pSprat);
                    ActiveProfiles.Add(pCapelin);
                    ActiveProfiles.Add(pShrimpFish);
                    break;
                case "feather fishing spoon":
                    ActiveProfiles.Add(pJellyFish);
                    ActiveProfiles.Add(pBlackHalibut);
                    ActiveProfiles.Add(pWhiteHalibut);
                    break;
                case "sprat":
                case "capelin":
                    ActiveProfiles.Add(pSeaBass);
                    ActiveProfiles.Add(pCod);
                    break;
                case "grave beetle":
                case "mussel":
                    ActiveProfiles.Add(pJellyFish);
                    break;
                case "corn seeds":
                    ActiveProfiles.Add(pJellyFish);
                    ActiveProfiles.Add(pSeaBass);
                    break;
                case "shrimp":
                    ActiveProfiles.Add(pCod);
                    break;
            }
        }

        // === PERFIS DO DESERTO ===
        private static void LoadDesertProfiles(string bait)
        {
            var pCarp = new FishProfile("Carp", new[] { BITE, BITE, SIDE, DOWN }, new[] { WAIT, WAIT, T, E });
            var pTrout = new FishProfile("Trout", new[] { BITE, DOWN, SIDE, LIFT, DRAG }, new[] { WAIT, WAIT, T, R, E });
            var pAlburnus = new FishProfile("Alburnus", new[] { DRAG }, new[] { E });
            var pPike = new FishProfile("Pike", new[] { DRAG, SIDE, DRAG, DRAG }, new[] { WAIT, T, R, E });
            var pSalmon = new FishProfile("Salmon", new[] { BITE, DRAG, DOWN, LIFT, SIDE }, new[] { WAIT, WAIT, R, WAIT, T });

            switch (bait)
            {
                case "worms":
                    ActiveProfiles.Add(pCarp);
                    ActiveProfiles.Add(pTrout);
                    break;
                case "maggots":
                    ActiveProfiles.Add(pAlburnus);
                    break;
                case "peas":
                case "corn seeds":
                    ActiveProfiles.Add(pCarp);
                    break;
                case "shrimp":
                    ActiveProfiles.Add(pCarp);
                    ActiveProfiles.Add(pPike);
                    ActiveProfiles.Add(pTrout);
                    ActiveProfiles.Add(pSalmon);
                    break;
                case "crucian":
                case "roach":
                    ActiveProfiles.Add(pPike);
                    break;
                case "alburnus":
                    ActiveProfiles.Add(pTrout);
                    break;
            }
        }

        // === LÓGICA DE INTELIGÊNCIA ARTIFICIAL (ELIMINAÇÃO COM RETROCESSO) ===
        public static int PredictMoveWithElimination(int wrongActionID, ManualLogSource logger)
        {
            int currentIndex = SessionVisualHistory.Count;

            logger.LogInfo($"[IA] Analisando Vermelho. Passo: {currentIndex}. Erro sugerido: {GetKeyName(wrongActionID)}");

            // 1. Tenta resolver no passo atual
            int result = RunElimination(currentIndex, wrongActionID, logger);

            // 2. Se falhou (Wait) e temos histórico, assume que o último "Verde" foi falso positivo (Backtracking)
            if (result == WAIT && currentIndex > 0)
            {
                logger.LogWarning($"[IA] Falha no Passo {currentIndex}. Tentando Retrocesso para Passo {currentIndex - 1}...");

                // Tenta rodar a eliminação como se estivéssemos um passo atrás
                result = RunElimination(currentIndex - 1, wrongActionID, logger);

                if (result != WAIT)
                {
                    logger.LogWarning("[IA] Retrocesso BEM SUCEDIDO! O último 'Verde' era uma armadilha. Corrigindo histórico...");
                    // Corrige o histórico removendo a mentira (o último visual adicionado)
                    SessionVisualHistory.RemoveAt(currentIndex - 1);
                }
            }

            return result;
        }

        // Método auxiliar que roda a lógica de eliminação para um índice específico
        private static int RunElimination(int stepIndex, int wrongActionID, ManualLogSource logger)
        {
            foreach (var fish in ActiveProfiles)
            {
                // Verifica se o histórico bate (até o limite do stepIndex)
                if (!IsMatchingHistory(fish.VisualSequence, SessionVisualHistory.ToArray(), stepIndex)) continue;

                // Verifica se o peixe tem passos suficientes
                if (stepIndex >= fish.ActionSequence.Length) continue;

                // Descobre o que esse peixe pediria nesse passo
                int actionThisFishWants = fish.ActionSequence[stepIndex];

                // Se o peixe pede EXATAMENTE a ação que deu ERRO, ele é eliminado
                if (actionThisFishWants == wrongActionID)
                {
                    // logger.LogInfo($"[IA] Eliminando '{fish.Name}' pois pediria '{GetKeyName(wrongActionID)}' (Mentira).");
                    continue;
                }

                // Se chegamos aqui, achamos um peixe que pede algo DIFERENTE da mentira
                logger.LogWarning($"[IA] DEDUÇÃO (Passo {stepIndex}): Só pode ser '{fish.Name}'! Executando: {GetKeyName(actionThisFishWants)}");
                return actionThisFishWants;
            }
            return WAIT;
        }

        // Função de suporte para compatibilidade
        public static int PredictFinalMove(ManualLogSource logger)
        {
            return PredictMoveWithElimination(3, logger);
        }

        // Verifica o histórico até um limite específico (necessário para o Backtracking)
        private static bool IsMatchingHistory(int[] fishSeq, int[] history, int limit)
        {
            if (limit > fishSeq.Length) return false;

            // Verifica apenas até o limite (ignorando itens extras se estivermos retrocedendo)
            for (int i = 0; i < limit; i++)
            {
                if (i >= history.Length) break;
                if (fishSeq[i] != history[i]) return false;
            }
            return true;
        }

        // Sobrecarga para verificar histórico completo (usado internamente em outras partes se necessário)
        private static bool IsMatchingHistory(int[] fishSeq, int[] history)
        {
            return IsMatchingHistory(fishSeq, history, history.Length);
        }

        public static void AddToHistory(int visualID) => SessionVisualHistory.Add(visualID);

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