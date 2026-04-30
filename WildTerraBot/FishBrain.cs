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
        // Histórico por passo: visuais observados (verde e vermelho)
        private static readonly Dictionary<int, int> ObservedVisualByStep = new Dictionary<int, int>();
        // Passos confirmados (verde): visual + ação correta
        private struct ConfirmedStep { public int Visual; public int Action; public ConfirmedStep(int v, int a) { Visual = v; Action = a; } }
        private static readonly Dictionary<int, ConfirmedStep> ConfirmedByStep = new Dictionary<int, ConfirmedStep>();
        // Índice do passo atual na sessão de pesca (0-based)
        private static int SessionStepIndex = 0;
        // Se não for nulo, significa que já deduzimos o peixe e estamos seguindo a sequência inteira
        public static FishProfile LockedProfile = null;
        public static bool IsLocked => LockedProfile != null;

        // Constantes Visuais
        private const int BITE = 0, LIFT = 1, SIDE = 2, DOWN = 3, DRAG = 4;
        // Constantes Ações
        private const int E = 0, R = 1, T = 2, WAIT = 3;

        public static void Initialize() { ActiveProfiles.Clear(); }
        public static void ResetSession()
        {
            ObservedVisualByStep.Clear();
            ConfirmedByStep.Clear();
            SessionStepIndex = 0;
            LockedProfile = null;
        }

        /// <summary>
        /// Registra o passo atual (visual observado) e avança o contador de passos.
        /// Retorna o índice do passo (0-based) referente ao evento recebido.
        /// </summary>
        public static int BeginStep(int visualID)
        {
            int step = SessionStepIndex;
            ObservedVisualByStep[step] = visualID;
            SessionStepIndex++;
            return step;
        }

        /// <summary>
        /// Registra um passo confirmado (verde): visual + ação correta (a dica do jogo era verdadeira).
        /// </summary>
        public static void RecordConfirmedStep(int stepIndex, int visualID, int actionID)
        {
            ObservedVisualByStep[stepIndex] = visualID;
            ConfirmedByStep[stepIndex] = new ConfirmedStep(visualID, actionID);
        }

        /// <summary>
        /// Retorna a ação prevista pelo peixe travado (LOCK). Se o passo exceder a sequência, retorna WAIT.
        /// </summary>
        public static int GetLockedAction(int stepIndex)
        {
            if (LockedProfile == null) return WAIT;
            if (stepIndex < 0 || stepIndex >= LockedProfile.ActionSequence.Length) return WAIT;
            return LockedProfile.ActionSequence[stepIndex];
        }

        private static void TryLockIfUnique(List<FishProfile> candidates, ManualLogSource logger)
        {
            if (LockedProfile != null) return;
            if (candidates != null && candidates.Count == 1)
            {
                LockedProfile = candidates[0];
                logger.LogWarning($"[IA] LOCK: Peixe deduzido com certeza: '{LockedProfile.Name}'. A partir de agora seguirei a sequência completa (E/R/T/Wait) sem depender das dicas do UI.");
            }
        }

        private static List<FishProfile> GetCandidates(int currentStepIndex, int currentVisualID, int? expectedAction, int? forbiddenAction)
        {
            var result = new List<FishProfile>();

            foreach (var fish in ActiveProfiles)
            {
                // Deve ter passos suficientes para o passo atual
                if (currentStepIndex >= fish.VisualSequence.Length) continue;
                if (currentStepIndex >= fish.ActionSequence.Length) continue;

                // Visual do passo atual sempre é um dado real do peixe
                if (fish.VisualSequence[currentStepIndex] != currentVisualID) continue;

                // Todos os visuais observados (inclusive passos vermelhos) devem bater
                bool ok = true;
                foreach (var kv in ObservedVisualByStep)
                {
                    int step = kv.Key;
                    int vis = kv.Value;

                    if (step >= fish.VisualSequence.Length) { ok = false; break; }
                    if (fish.VisualSequence[step] != vis) { ok = false; break; }
                }
                if (!ok) continue;

                // Todos os passos confirmados (verde) precisam bater em visual + ação
                foreach (var kv in ConfirmedByStep)
                {
                    int step = kv.Key;
                    var conf = kv.Value;

                    if (step >= fish.VisualSequence.Length || step >= fish.ActionSequence.Length) { ok = false; break; }
                    if (fish.VisualSequence[step] != conf.Visual) { ok = false; break; }
                    if (fish.ActionSequence[step] != conf.Action) { ok = false; break; }
                }
                if (!ok) continue;

                // Restrições do passo atual:
                if (expectedAction.HasValue && fish.ActionSequence[currentStepIndex] != expectedAction.Value) continue;
                if (forbiddenAction.HasValue && fish.ActionSequence[currentStepIndex] == forbiddenAction.Value) continue;

                result.Add(fish);
            }

            return result;
        }

        private static int ChooseActionFromCandidates(List<FishProfile> candidates, int stepIndex)
        {
            if (candidates == null || candidates.Count == 0) return WAIT;
            if (candidates.Count == 1) return candidates[0].ActionSequence[stepIndex];

            // Se todas as opções restantes pedem a mesma tecla neste passo, podemos executar com segurança
            var uniqueActions = candidates.Select(c => c.ActionSequence[stepIndex]).Distinct().ToList();
            if (uniqueActions.Count == 1) return uniqueActions[0];

            // Caso ambíguo: escolhe a ação mais frequente; em empate, prefere WAIT (mais conservador)
            var grouped = candidates
                .GroupBy(c => c.ActionSequence[stepIndex])
                .Select(g => new { Action = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Action == WAIT ? 0 : 1)
                .ToList();

            return grouped[0].Action;
        }


        // --- CONFIGURA O CONTEXTO (RIO, OCEANO OU DESERTO) ---
        public static void SetContext(string location, string bait)
        {
            ActiveProfiles.Clear();
            ResetSession();
            location = NormalizeToken(location);
            bait = NormalizeBait(bait);

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

            else if (
                location == "battle field island" ||
                location == "battlefield island" ||
                location == "abandonedbattlefield" ||
                location == "abandoned battlefield")
            {
                LoadBattleFieldIslandProfiles(bait);
            }


        }

        // --- NORMALIZAÇÃO DE STRINGS (evita falhas por variação de UI/idioma/plural) ---
        private static string NormalizeToken(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Trim().ToLowerInvariant();
            // Normaliza múltiplos espaços
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s;
        }

        /// <summary>
        /// Normaliza o nome da isca vindo do Dashboard/UI.
        /// Ex.: "Worm" -> "worms", "Maggot" -> "maggots".
        /// Isso é crítico: se não bater com as chaves do switch, ActiveProfiles fica vazio e a IA sempre falha no Vermelho.
        /// </summary>
        private static string NormalizeBait(string bait)
        {
            bait = NormalizeToken(bait);
            if (bait == "") return "";

            // Correções comuns (singular/plural)
            if (bait == "worm" || bait == "worms") return "worms";
            if (bait == "maggot" || bait == "maggots") return "maggots";
            if (bait == "pea" || bait == "peas") return "peas";
            if (bait == "shrimp" || bait == "shrimps") return "shrimp";
            if (bait == "corn seed" || bait == "corn seeds" || bait == "corn") return "corn seeds";

            // Alguns itens do jogo podem vir com variações de capitalização/espacos
            if (bait == "featherfishing spoon" || bait == "feather fishing spoon") return "feather fishing spoon";

            // Perfis que usam o nome do próprio peixe como isca (pike bait)
            if (bait == "crucian") return "crucian";
            if (bait == "roach") return "roach";
            if (bait == "alburnus") return "alburnus";
            if (bait == "sprat") return "sprat";
            if (bait == "capelin") return "capelin";
            if (bait == "grave beetle") return "grave beetle";
            if (bait == "mussel") return "mussel";

            return bait; // fallback (mantém o texto, pode ser que já esteja correto)
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
            var pCatFish = new FishProfile("CatFish", new[] { SIDE, DOWN, DRAG, DRAG, LIFT }, new[] { WAIT, R, R, WAIT, R });

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



        // === PERFIS DO BATTLE FIELD ISLAND / ABANDONED BATTLEFIELD ===
        private static void LoadBattleFieldIslandProfiles(string bait)
        {
            var pJellyFish = new FishProfile("JellyFish", new[] { SIDE }, new[] { E });
            var pPiranha = new FishProfile("Piranha", new[] { LIFT, LIFT, DRAG }, new[] { WAIT, WAIT, T });
            var pPufferfish = new FishProfile("Pufferfish", new[] { BITE, BITE, SIDE, BITE, BITE }, new[] { WAIT, WAIT, T, WAIT, R });
            var pBlackHalibut = new FishProfile("Black Halibut", new[] { BITE, LIFT, SIDE, DOWN }, new[] { WAIT, R, R, E });
            var pWhiteHalibut = new FishProfile("White Halibut", new[] { BITE, LIFT, SIDE, DOWN, DOWN }, new[] { WAIT, R, R, WAIT, E });
            var pMussel = new FishProfile("Mussel", new[] { LIFT, SIDE }, new[] { T, R });
            var pSeaBass = new FishProfile("SeaBass", new[] { DOWN, LIFT, DRAG }, new[] { WAIT, T, R });
            var pFlounder = new FishProfile("Flounder", new[] { DOWN, DRAG, DRAG, LIFT, DRAG }, new[] { R, T, WAIT, T, T });
            var pWolffish = new FishProfile("Wolffish", new[] { BITE, DOWN, SIDE, DOWN, LIFT, LIFT }, new[] { R, T, R, WAIT, T, R });
            var pCod = new FishProfile("Cod", new[] { DOWN, SIDE, DOWN }, new[] { WAIT, WAIT, E });

            switch (bait)
            {
                case "worms":
                    ActiveProfiles.Add(pJellyFish);
                    break;

                case "maggots":
                    ActiveProfiles.Add(pJellyFish);
                    break;

                case "grave beetle":
                    ActiveProfiles.Add(pJellyFish);
                    ActiveProfiles.Add(pPiranha);
                    ActiveProfiles.Add(pPufferfish);
                    break;

                case "feather fishing spoon":
                    ActiveProfiles.Add(pJellyFish);
                    ActiveProfiles.Add(pBlackHalibut);
                    ActiveProfiles.Add(pWhiteHalibut);
                    break;

                case "corn seeds":
                    ActiveProfiles.Add(pJellyFish);
                    ActiveProfiles.Add(pMussel);
                    ActiveProfiles.Add(pSeaBass);
                    ActiveProfiles.Add(pFlounder);
                    break;

                case "mussel":
                    ActiveProfiles.Add(pJellyFish);
                    ActiveProfiles.Add(pWolffish);
                    break;

                case "sprat":
                    ActiveProfiles.Add(pSeaBass);
                    ActiveProfiles.Add(pCod);
                    break;

                case "capelin":
                    ActiveProfiles.Add(pSeaBass);
                    ActiveProfiles.Add(pCod);
                    break;

                case "shrimp":
                    ActiveProfiles.Add(pCod);
                    break;
            }
        }





        // === LÓGICA DE INTELIGÊNCIA ARTIFICIAL (ELIMINAÇÃO COM RETROCESSO) ===
        public static void EvaluateLockOnGreen(int stepIndex, int visualID, int correctActionID, ManualLogSource logger)
        {
            // Já registramos o passo confirmado fora; aqui apenas tentamos travar se ficar único
            var candidates = GetCandidates(stepIndex, visualID, correctActionID, null);
            TryLockIfUnique(candidates, logger);
        }

        /// <summary>
        /// Vermelho: a dica do jogo (wrongActionID) é garantidamente ERRADA.
        /// Deduzimos a ação correta eliminando peixes incompatíveis e, se o peixe ficar único, travamos nele.
        /// </summary>
        public static int PredictMoveWithElimination(int stepIndex, int visualID, int wrongActionID, ManualLogSource logger)
        {
            logger.LogInfo($"[IA] Analisando Vermelho. Passo: {stepIndex}. Visual: {visualID} ({GetVisualName(visualID)}). Erro sugerido: {GetKeyName(wrongActionID)}");

            // 1) Filtra candidatos usando: visuais observados + passos verdes confirmados + (ação do passo atual != dica vermelha)
            var candidates = GetCandidates(stepIndex, visualID, null, wrongActionID);

            // Se ficar vazio, tentamos sem a restrição "forbiddenAction" (perfil/local/isca pode estar divergente)
            if (candidates.Count == 0)
            {
                logger.LogWarning($"[IA] Nenhum candidato após filtrar pela dica vermelha. Tentando apenas com visuais/histórico (sem eliminar pela dica)...");
                candidates = GetCandidates(stepIndex, visualID, null, null);
            }

            if (candidates.Count == 0)
            {
                logger.LogWarning($"[IA] Ainda sem candidatos. Usando WAIT como fallback.");
                return WAIT;
            }

            TryLockIfUnique(candidates, logger);

            int chosen = ChooseActionFromCandidates(candidates, stepIndex);

            if (candidates.Count == 1)
            {
                logger.LogWarning($"[IA] DEDUÇÃO (Passo {stepIndex}): '{candidates[0].Name}'. Executando: {GetKeyName(chosen)}");
            }
            else
            {
                var actions = candidates.Select(c => GetKeyName(c.ActionSequence[stepIndex])).Distinct().ToList();
                logger.LogWarning($"[IA] Ambíguo (Passo {stepIndex}): {candidates.Count} candidatos. Ações possíveis: {string.Join(", ", actions)}. Executando: {GetKeyName(chosen)}");
            }

            return chosen;
        }

        // Compatibilidade (mantido para não quebrar chamadas antigas)
        public static int PredictFinalMove(ManualLogSource logger) => WAIT;

        public static string GetKeyName(int val)
        {
            if (val == 0) return "E";
            if (val == 1) return "R";
            if (val == 2) return "T";
            if (val == 3) return "Wait";
            return val.ToString();
        }

        public static string GetVisualName(int visualID)
        {
            if (visualID == BITE) return "BITE";
            if (visualID == LIFT) return "LIFT";
            if (visualID == SIDE) return "SIDE";
            if (visualID == DOWN) return "DOWN";
            if (visualID == DRAG) return "DRAG";
            return visualID.ToString();
        }
    }
}