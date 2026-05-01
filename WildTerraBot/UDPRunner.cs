using Mirror;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using UnityEngine.AI;
using System.Linq;

namespace WildTerraBot
{
    public class UDPRunner : MonoBehaviour
    {

        // Singleton simples para hooks (HarvestHooks etc.)
        public static UDPRunner Instance;


        public Player MeuPersonagem;
        private UdpClient udpSender;
        private UdpClient udpListener;
        private Thread listenThread;
        private volatile bool running = false;
        private int _portaEscutaComandos = 8889;
        private int _portaEnvioTelemetria = 8888;
        private PlayerInspectService _playerInspectService;


        private const int UDP_BASE_TELEMETRY_PORT = 8888;
        private const int UDP_MAX_INSTANCES = 10;



        private bool _botAtivo = false;
        private bool _useMount = false;
        private bool _modoColeta = false;
        private bool _modoHunter = false;
        private bool _modoPesca = false;
        private bool _returningHome = false;
        // ===== DEBUG MONTARIA =====
        private const bool DBG_MOUNT = true;
        private float _dbgNextAutoMountLog = 0f;
        private bool? _dbgLastMounted = null;

        // Throttle de log em thread de rede (não usa Time.time)
        private int _dbgNextMoveCmdLogAt = 0;
        private const int DBG_MOVE_CMD_THROTTLE_MS = 1000;

        private void DbgMount(string msg)
        {
            if (!DBG_MOUNT) return;
            try
            {
                if (WTSocketBot.PublicLogger != null) WTSocketBot.PublicLogger.LogInfo(msg);
                else Debug.Log(msg);
            }
            catch
            {
                try { Debug.Log(msg); } catch { }
            }
        }


        // === COMBAT DEBUG LOGS (somente logs; não altera comportamento) ===
        private const bool DBG_COMBAT = true;
        private float _dbgNextDefenseOffWarn = 0f;
        private float _dbgNextCombatDecisionLog = 0f;
        private float _dbgNextCombatMoveLog = 0f;
        private float _dbgNextCombatAttackLog = 0f;
        private float _dbgCombatLastDist = -1f;
        private float _dbgCombatLastDistTime = 0f;

        private float _dbgNextInvalidTargetWarn = 0f;
        private float _dbgNextAggroDumpLog = 0f;
        private float _dbgNextPulseWaitLog = 0f;
        private float _dbgNextCmdErrLog = 0f;
        private float _dbgNextPickAggressorLog = 0f;

        private struct AggroCand
        {
            public WTMob mob;
            public float dist;
            public bool targetingMe;
            public int aggro;
        }


        private void DbgCombat(string msg)
        {
            if (!DBG_COMBAT) return;
            try { WTSocketBot.PublicLogger.LogInfo(msg); } catch { }
        }

        private string MobDbg(WTMob mob)
        {
            if (mob == null) return "null";
            string hp = "?";
            string st = "?";
            try { hp = mob.health.ToString(CultureInfo.InvariantCulture); } catch { }
            try { st = mob.state; } catch { }
            Vector3 p = Vector3.zero;
            try { p = mob.transform.position; } catch { }
            return $"{mob.name} hp={hp} st={st} pos=({p.x:F1},{p.z:F1})";
        }

        private string AgentDbg(WTPlayer me)
        {
            try
            {
                if (me == null || me.agent == null) return "agent=null";
                var a = me.agent;
                return $"agent[enabled={a.enabled} hasPath={a.hasPath} rem={a.remainingDistance:F2} vel={a.velocity.magnitude:F2} pathStatus={a.pathStatus}]";
            }
            catch { return "agent=?"; }
        }


        private void DbgDumpAggressors(WTPlayer me, float radius, int max = 4)
        {
            if (!DBG_COMBAT) return;
            if (Time.time < _dbgNextAggroDumpLog) return;
            _dbgNextAggroDumpLog = Time.time + 1.5f;

            try
            {
                if (me == null)
                {
                    DbgCombat("[AGGRO] me=null");
                    return;
                }

                Vector3 myPos = me.transform.position;
                int myId = me.worldId;

                var mobs = FindObjectsOfType<WTMob>();
                var cand = new List<AggroCand>(32);

                foreach (var mob in mobs)
                {
                    if (mob == null) continue;

                    float d = 9999f;
                    try { d = Vector3.Distance(myPos, mob.transform.position); } catch { }
                    if (d > radius) continue;

                    bool targetingMe = false;
                    try { targetingMe = (mob.target == me); } catch { }

                    int aggroVal = -1;
                    if (_aggroByIdField != null)
                    {
                        try
                        {
                            var dict = _aggroByIdField.GetValue(mob) as System.Collections.IDictionary;
                            if (dict != null && dict.Contains(myId))
                            {
                                object aggroObj = dict[myId];
                                FieldInfo valField = aggroObj.GetType().GetField("value");
                                if (valField != null) aggroVal = (int)valField.GetValue(aggroObj);
                            }
                        }
                        catch { }
                    }

                    // Só lista quem realmente tem vínculo com a gente (target ou aggro)
                    if (!targetingMe && aggroVal < 0) continue;

                    cand.Add(new AggroCand { mob = mob, dist = d, targetingMe = targetingMe, aggro = aggroVal });
                }

                if (cand.Count == 0)
                {
                    float dmgAgo = Time.time - _lastDamageTime;
                    string tname = (me.target != null ? me.target.name : "null");
                    DbgCombat($"[AGGRO] nenhum agressor em {radius:F1}m | dmgAgo={dmgAgo:F1}s me.target={tname}");
                    return;
                }

                // Ordena por aggro desc, depois por distância asc.
                cand = cand.OrderByDescending(c => c.aggro).ThenBy(c => c.dist).Take(Mathf.Max(1, max)).ToList();

                var sb = new StringBuilder();
                sb.Append($"[AGGRO] candidatos top={cand.Count} (radius={radius:F1}): ");
                for (int i = 0; i < cand.Count; i++)
                {
                    var c = cand[i];
                    sb.Append($"#{i + 1} {MobDbg(c.mob)} d={c.dist:F1} targetingMe={c.targetingMe} aggro={c.aggro}; ");
                }
                DbgCombat(sb.ToString());
            }
            catch (Exception ex)
            {
                try { DbgCombat($"[AGGRO-ERR] {ex.GetType().Name}: {ex.Message}"); } catch { }
            }
        }


        private Vector3 _homeCoordsBackup = Vector3.zero;
        private string _alvoHunterTipo = "";
        private string _armaPreferida = "";
        private string _nomeVaraPesca = "";
        private string _nomeIscaPesca = "";
        private string _armaPescaDefensiva = "";

        // Fishing anchor (posição/direção) para retomar exatamente após combate
        private Vector3 _fishingAnchorPos = Vector3.zero;
        private float _fishingAnchorYaw = 0f;
        private bool _fishingAnchorValid = false;
        private bool _resumeFishingAfterCombat = false;
        private float _nextReturnToAnchorPulse = 0f;

        // Pós-combate na pesca: priorizar esfolar/loot antes de retornar à âncora e retomar o arremesso
        private enum PostCombatFishingState { None, MoveToCorpse, Skinning }
        private PostCombatFishingState _postCombatFishingState = PostCombatFishingState.None;
        private WTObject _postCombatCorpse = null;
        private ScriptableSkill _postCombatSkinSkill = null;
        private float _postCombatSkinFinishTime = 0f;
        private float _postCombatCorpseTimeout = 0f;

        // Retarget throttling: evita varrer mobs todo frame
        private float _nextRetargetScan = 0f;
        private const float RETARGET_SCAN_INTERVAL = 0.35f;
        private const float RETARGET_DAMAGE_WINDOW = 1.2f;   // considerar que estamos sendo hitados agora
        private const float RETARGET_SCAN_RADIUS = 10.0f;    // raio para procurar atacante
        private const float RETARGET_CLOSE_BONUS = 0.8f;     // folga extra em cima do engageDist
        private const float RETARGET_CLOSER_MARGIN = 0.6f;   // só troca se o novo alvo for claramente mais perto


        private WTMob _combatTarget = null;
        private float _nextAttackPulse = 0f;
        private float _nextEquipCheck = 0f;
        private const float ATTACK_PULSE = 0.30f;
        private int _lastKnownHp = -1;
        private float _lastDamageTime = 0f;
        private bool _reportouCombate = false;











        private float _nextCastCheck = 0f;
        private TrainingModeController _trainingController;
        private float _nextDebugLog = 0f;
        private bool _isMountingRoutineActive = false;
        private float _pauseMovementUntil = 0f;
        private const float MOUNT_ANIMATION_TIME = 4.0f;
        private const float HARVEST_MOUNT_DISTANCE = 15.0f;
        private float _nextMountPulse = 0f; // evita spam de ToggleMount
        private Vector3? _lastMoveTarget = null;

        // ===== HARVEST: modo "clique do jogo" (WorldObjectTryAction desde longe + useSkillWhenCloser) =====
        private WTObject _harvestTarget = null;
        private float _harvestTimeout = 0f;     // segurança: não ficar preso eternamente
        private float _nextHarvestTry = 0f;     // throttle: evita spam de WorldObjectTryAction
        private string _harvestPartialName = "";




        // ===== HARVEST: anti-travamento por "flank" (chegar por outro lado do recurso) =====
        private bool _harvestFlanking = false;
        private Vector3 _harvestFlankPoint;
        private float _harvestStartTime = 0f;
        private float _harvestBestDistC = 9999f;
        private float _harvestLastProgressTime = 0f;
        private float _harvestNextWatchTick = 0f;
        private int _harvestFlankAttempts = 0;
        private readonly Dictionary<int, float> _harvestBlacklistUntil = new Dictionary<int, float>(); // worldId (fallback: instanceId) -> time
        private bool _harvestStartedMounted = false;
        private bool _harvestRetriedAfterDismount = false;
        private int _harvestNearTargetRetryCount = 0;
        private float _harvestLostInteractionSince = 0f;
        private float _harvestLastActionTime = 0f;
        private float _harvestArmedStallSince = 0f;
        private int _harvestArmedStallRetryCount = 0;
        private bool _harvestMicroAdjusting = false;

        private const float HARVEST_TOTAL_TIMEOUT = 22.0f;
        private const float HARVEST_WATCH_TICK = 0.80f;
        private const float HARVEST_PROGRESS_MIN_DELTA_C = 0.18f;
        private const float HARVEST_NO_PROGRESS_TIME = 5.50f;
        private const float HARVEST_MIN_DISTC_FOR_STUCK = 1.15f;
        private const float HARVEST_ARRIVE_DIST = 0.75f;
        private const int HARVEST_MAX_FLANK_ATTEMPTS = 3;
        private const float HARVEST_BLACKLIST_TIME = 90.0f;
        private const float HARVEST_SUCCESS_COOLDOWN_TIME = 4.0f;
        private const float HARVEST_POST_DISMOUNT_RETRY_DELAY = 0.45f;
        private const float HARVEST_NEAR_TARGET_STALL_RETRY_TIME = 1.75f;
        private const float HARVEST_NEAR_TARGET_ABORT_TIME = 4.50f;
        private const int HARVEST_MAX_NEAR_TARGET_RETRIES = 2;
        private const float HARVEST_ARMED_STALL_RETRY_TIME = 1.60f;
        private const float HARVEST_ARMED_STALL_ABORT_TIME = 5.50f;
        private const float HARVEST_ARMED_STALL_REMDIST_MAX = 0.90f;
        private const float HARVEST_MICROADJUST_STOPPING_DISTANCE = 0.05f;



        private float nextStatsSend = 0f;
        private float nextBagSend = 0f;
        private float nextRadarScan = 0f;
        private float nextDropCheck = 0f;
        private float nextEatCheck = 0f;
        private const float AUTO_EAT_COOLDOWN = 1.0f;
        private int _eatThreshold = 30;
        private bool _resting = false;
        private bool _depositando = false;
        // ===== HEAL TRAINING (modo cura) =====
        private HealTrainingController _healTrainer = null;

        // ===== TAME DEBUG: estado + efeitos do target =====
        private MobStateEffectLogger _tameDbg = null;
        private TamingController _tamingController = null;
        private bool _tamingDefenseActive = false;
        private WTMob _tamingDefenseTarget = null;



        // Anti-Spam Variables
        private float _nextBagFullAlert = 0f;
        private string _lastHarvestLog = ""; // Controla repetição do log de coleta
        private int _queuedHarvestWorldId = 0;
        private int _activeHarvestWorldId = 0;
        private int _dbgNextHarvestBusyLogAt = 0;
        private string _lastHuntLog = "";    // Controla repetição do log de caça

        private HashSet<string> bonusPendentes = new HashSet<string>();
        private HashSet<string> itensSeguros = new HashSet<string>();
        private HashSet<string> itensDropar = new HashSet<string>();
        private HashSet<string> itensComer = new HashSet<string>();
        private HashSet<string> itensComerStatus = new HashSet<string>();
        private readonly AutoStatusFoodController _autoStatusFood = new AutoStatusFoodController();
        private HashSet<string> itensColeta = new HashSet<string>();
        private string _lastHarvestListPayload = "";
        private ConcurrentQueue<Vector2> moveQueue = new ConcurrentQueue<Vector2>();
        private ConcurrentQueue<string> actionQueue = new ConcurrentQueue<string>();
        public ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>();

        // Reflection
        private MethodInfo _dropMethod = null;
        private MethodInfo _useMethod = null;
        private MethodInfo _swapEquipMethod = null;
        private FieldInfo _foodArrayField = null;
        private MethodInfo _toggleMountMethod = null;
        private MethodInfo _isMountedMethod = null;
        private MethodInfo _inCombatMethod = null;
        private MethodInfo _setTargetMethod = null;
        private MethodInfo _cmdSkillToPoint = null;
        private FieldInfo _aggroByIdField = null;
        private FieldInfo _fiWorldObject = null;
        private FieldInfo _fiEntityType = null;
        private MethodInfo _isFishingMethod = null;
        private MethodInfo _getEquippedRightHand = null;
        private MethodInfo _getEquippedLeftHand = null;
        private MethodInfo _getEquippedAmmo = null;
        private MethodInfo _isFishingPoleEquippedMethod = null;
        private MethodInfo _equipmentOperationsAllowedMethod = null;
        private FieldInfo _isFishingBaitField = null;
        private MethodInfo _findEquipableSlotForMethod = null;


        private FieldInfo _useSkillWhenCloserField = null;

        private static readonly Regex _bTagNumber = new Regex("<b>(\\d+)</b>", RegexOptions.Compiled);

        private static IEnumerable<int[]> EnumerarParesPortaUdp()
        {
            for (int indiceInstancia = 0; indiceInstancia < UDP_MAX_INSTANCES; indiceInstancia++)
            {
                int portaTelemetria = UDP_BASE_TELEMETRY_PORT + (indiceInstancia * 2);
                int portaComando = portaTelemetria + 1;
                yield return new[] { portaComando, portaTelemetria };
            }
        }




        void Start()
        {


            Instance = this;

            StartCoroutine(AutoDumpDelay());

            udpSender = new UdpClient();
            try
            {
                bool portaConfigurada = false;

                foreach (var pair in EnumerarParesPortaUdp())

                {
                    try
                    {
                        int portaComando = pair[0];
                        int portaTelemetria = pair[1];

                        udpListener = new UdpClient(new IPEndPoint(IPAddress.Loopback, portaComando));
                        _portaEscutaComandos = portaComando;
                        _portaEnvioTelemetria = portaTelemetria;
                        portaConfigurada = true;
                        break;
                    }
                    catch (SocketException)
                    {
                        // Porta ocupada por outra instância do bot. Tenta o próximo par.
                    }
                }

                if (!portaConfigurada)
                    throw new SocketException((int)SocketError.AddressAlreadyInUse);


                running = true;
                listenThread = new Thread(() => ListenLoop(udpListener));
                listenThread.IsBackground = true;
                listenThread.Start();
                WTSocketBot.PublicLogger.LogInfo(string.Format(WildTerraBot.Properties.Resources.UdpRunnerUdpConfiguredFormat, _portaEscutaComandos, _portaEnvioTelemetria));
            }
            catch (Exception ex) { WTSocketBot.PublicLogger.LogError(string.Format(WildTerraBot.Properties.Resources.UdpRunnerUdpPortErrorFormat, ex.Message)); }


            Type tipoWT = typeof(WTPlayer);
            Type tipoPlayer = typeof(Player);
            Type tipoEntity = typeof(Entity);
            Type tipoMob = typeof(WTMob);
            Type tipoEquipItem = Type.GetType("WTEquipmentItem, Assembly-CSharp") ?? typeof(WTEquipmentItem);
            Type tipoBaseEquip = Type.GetType("EquipmentItem, Assembly-CSharp");

            try
            {
                _dropMethod = tipoWT.GetMethod("CmdDropInventoryItem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) ?? tipoWT.GetMethod("UserCode_CmdDropInventoryItem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _useMethod = tipoPlayer.GetMethod("CmdUseInventoryItem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) ?? tipoWT.GetMethod("CmdUseInventoryItem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _swapEquipMethod = tipoPlayer.GetMethod("CmdSwapInventoryEquip", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _foodArrayField = tipoPlayer.GetField("food", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _isMountedMethod = tipoPlayer.GetMethod("IsMounted", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? tipoWT.GetMethod("IsMounted", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _toggleMountMethod = tipoPlayer.GetMethod("CmdToggleMount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? tipoWT.GetMethod("CmdToggleMount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _inCombatMethod = tipoEntity.GetMethod("InCombat", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _setTargetMethod = tipoPlayer.GetMethod("CmdSetTarget", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? tipoWT.GetMethod("CmdSetTarget", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _cmdSkillToPoint = tipoPlayer.GetMethod("CmdSkillToPoint", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) ?? tipoWT.GetMethod("CmdSkillToPoint", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _aggroByIdField = tipoMob.GetField("aggroById", BindingFlags.Instance | BindingFlags.NonPublic);
                _fiWorldObject = tipoEntity.GetField("worldObject", BindingFlags.Instance | BindingFlags.NonPublic);
                _fiEntityType = tipoEntity.GetField("entityType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _isFishingMethod = tipoWT.GetMethod("IsFishing", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _getEquippedRightHand = tipoWT.GetMethod("GetEquippedRightHand");
                _getEquippedLeftHand = tipoWT.GetMethod("GetEquippedLeftHand");
                _getEquippedAmmo = tipoWT.GetMethod("GetEquippedAmmo");
                _isFishingPoleEquippedMethod = tipoWT.GetMethod("IsFishingPoleEquipped", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _equipmentOperationsAllowedMethod = tipoPlayer.GetMethod("EquipmentOperationsAllowed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (tipoEquipItem != null) _isFishingBaitField = tipoEquipItem.GetField("isFishingBait", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (tipoBaseEquip != null) _findEquipableSlotForMethod = tipoBaseEquip.GetMethod("FindEquipableSlotFor", BindingFlags.Instance | BindingFlags.Public);
                _useSkillWhenCloserField = tipoPlayer.GetField("useSkillWhenCloser", BindingFlags.Instance | BindingFlags.NonPublic);
            }
            catch (Exception ex) { WTSocketBot.PublicLogger.LogError(string.Format(WildTerraBot.Properties.Resources.UdpRunnerReflectionErrorFormat, ex.Message)); }

            // Heal trainer (usa somente APIs do jogo: CmdSetTarget / TryUseSkill)
            _healTrainer = new HealTrainingController(
                (player, itemName, targetSlot) => CheckAndEquipItem(player, itemName, targetSlot),
                (m) => { try { WTSocketBot.PublicLogger.LogInfo(m); } catch { } },
                (m) => { try { WTSocketBot.PublicLogger.LogWarning(m); } catch { } }
            );

            _trainingController = new TrainingModeController(
                (m) => { try { WTSocketBot.PublicLogger.LogInfo(m); } catch { } },
                (m) => { try { WTSocketBot.PublicLogger.LogWarning(m); } catch { } },
                CmdSkillToPoint
            );

            // Logger de estados/efeitos do TARGET (para domar). Apenas diagnóstico.
            _tameDbg = new MobStateEffectLogger(
                (m) => { try { WTSocketBot.PublicLogger.LogInfo(m); } catch { } },
                0.25f
            );

            _tamingController = new TamingController(
                TamingLog,
                CheckIsMounted,
                ToggleMount,
                MoveToXZ,
                (player, itemName, targetSlot) => CheckAndEquipItem(player, itemName, targetSlot),
                CmdSetTarget,
                CmdSkillToPoint,
                (player, skillIndex) => { try { player.TryUseSkill(skillIndex, false, false); } catch { } }
            );


        }





        IEnumerator AutoDumpDelay()
        {
            yield return new WaitForSeconds(15.0f);
            if (WTSocketBot.Instance != null && !WTSocketBot.HasDumped)
            {
                WTSocketBot.Instance.DumpFromScene("AUTO-START-15s");
                WTSocketBot.HasDumped = true;
            }
        }

        void OnDestroy() { running = false; if (udpListener != null) udpListener.Close(); if (udpSender != null) udpSender.Close(); }

        private void ListenLoop(UdpClient listener)
        {
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (running)
            {
                try
                {
                    byte[] bytes = listener.Receive(ref remote);
                    string msg = Encoding.ASCII.GetString(bytes);
                    string[] p = msg.Split(';');

                    if (p[0] == "DUMP") mainThreadActions.Enqueue(() => WTSocketBot.Instance.DumpFromScene("UDP-TRIGGER"));
                    else if (p[0] == "MOVE")
                    {
                        moveQueue.Enqueue(new Vector2(float.Parse(p[1], CultureInfo.InvariantCulture), float.Parse(p[2], CultureInfo.InvariantCulture)));


                        // Fix: MOVE indica deslocamento "baseline". Se estávamos presos em modo coleta/caça,
                        // isso bloqueia o auto-mount de rota. Então limpamos os modos aqui.
                        if (_modoColeta || _modoHunter)
                        {
                            if (DBG_MOUNT) DbgMount(string.Format(WildTerraBot.Properties.Resources.UdpRunnerCmdMoveClearingModesFormat, _modoColeta, _modoHunter));
                            ResetModes();
                        }

                        if (DBG_MOUNT)
                        {
                            int now = Environment.TickCount;
                            if (now >= _dbgNextMoveCmdLogAt)
                            {
                                _dbgNextMoveCmdLogAt = now + DBG_MOVE_CMD_THROTTLE_MS;
                                DbgMount(string.Format(WildTerraBot.Properties.Resources.UdpRunnerCmdMoveToFormat, p[1], p[2], _useMount, _modoColeta, _modoHunter, _modoPesca, _returningHome));
                            }
                        }
                        // MOVE é o baseline (rota). Não deve desligar caça/coleta; apenas cancela modos exclusivos.
                        _returningHome = false;
                        _modoPesca = false;
                    }
                    else if (p[0] == "HARVEST" && p.Length >= 2)
                    {
                        int requestedWorldId = 0;
                        if (p.Length >= 4) int.TryParse(p[3], out requestedWorldId);

                        if (DBG_MOUNT)
                            DbgMount(string.Format(WildTerraBot.Properties.Resources.UdpRunnerCmdHarvestBeforeFormat, p[1], requestedWorldId, _modoColeta, _modoHunter, _modoPesca, _returningHome));

                        if (HasHarvestInFlightUnsafe())
                        {
                            int now = Environment.TickCount;
                            if (now >= _dbgNextHarvestBusyLogAt)
                            {
                                _dbgNextHarvestBusyLogAt = now + 1500;
                                DbgMount(string.Format(WildTerraBot.Properties.Resources.UdpRunnerCmdHarvestIgnoredBusyFormat, SafeName(_harvestTarget), _activeHarvestWorldId, p[1], requestedWorldId));
                            }
                            continue;
                        }

                        while (actionQueue.TryDequeue(out _)) { }
                        actionQueue.Enqueue(p[1]);
                        Interlocked.Exchange(ref _queuedHarvestWorldId, requestedWorldId);

                        if (p.Length >= 3)
                        {
                            _armaPreferida = p[2].Trim();
                            string logMsg = string.Format(WildTerraBot.Properties.Resources.UdpRunnerHarvestTargetFormat, p[1], _armaPreferida, requestedWorldId);
                            if (_lastHarvestLog != logMsg)
                            {
                                WTSocketBot.PublicLogger.LogInfo(logMsg);
                                _lastHarvestLog = logMsg;
                            }
                        }

                        ResetModes();
                        _modoColeta = true;

                        if (DBG_MOUNT)
                            DbgMount(string.Format(WildTerraBot.Properties.Resources.UdpRunnerCmdHarvestAfterFormat, p[1], requestedWorldId, _modoColeta, _modoHunter, _modoPesca, _returningHome));
                    }
                    else if (p[0] == "HUNT" && p.Length >= 2)
                    {
                        _alvoHunterTipo = p[1].Trim();
                        _armaPreferida = (p.Length >= 3) ? p[2].Trim() : "";
                        ResetModes();
                        _modoHunter = true;

                        // --- CORREÇÃO: ANTI-SPAM DE LOG ---
                        string logMsg = string.Format(WildTerraBot.Properties.Resources.UdpRunnerHunterTargetFormat, _alvoHunterTipo, _armaPreferida);
                        if (_lastHuntLog != logMsg)
                        {
                            WTSocketBot.PublicLogger.LogInfo(logMsg);
                            _lastHuntLog = logMsg;
                        }
                    }
                    // ... dentro do loop while(running) ...
                    else if (p[0] == "FISHING")
                    {
                        if (p.Length >= 2 && p[1] == "ON")
                        {
                            ResetModes();
                            _modoPesca = true;

                            // Formato esperado: FISHING;ON;Vara;Isca;Local;ArmaDef
                            _nomeVaraPesca = (p.Length >= 3) ? p[2].Trim() : "";
                            _nomeIscaPesca = (p.Length >= 4) ? p[3].Trim() : "";
                            string localPesca = (p.Length >= 5) ? p[4].Trim() : "River"; // Padrão River
                            _armaPescaDefensiva = (p.Length >= 6) ? p[5].Trim() : "";

                            // Durante pesca, a arma defensiva é a do txtWeaponName (Dashboard)
                            _armaPreferida = _armaPescaDefensiva;

                            // Captura âncora imediatamente ao ativar (posição/direção atual do jogador)
                            mainThreadActions.Enqueue(() =>
                            {
                                if (MeuPersonagem is WTPlayer wt) CaptureFishingAnchor(wt);
                                _resumeFishingAfterCombat = false;
                                ClearPostCombatSkinning();
                            });

                            // CONFIGURA O CÉREBRO
                            FishBrain.SetContext(localPesca, _nomeIscaPesca);

                            WTSocketBot.IsFishingBotActive = true;
                            WTSocketBot.PublicLogger.LogInfo(string.Format(WildTerraBot.Properties.Resources.UdpRunnerFishingEnabledFormat, localPesca, _nomeIscaPesca, FishBrain.ActiveProfiles.Count));
                        }
                        else
                        {
                            _modoPesca = false;
                            WTSocketBot.IsFishingBotActive = false;
                            _resumeFishingAfterCombat = false;
                            ClearPostCombatSkinning();
                            _fishingAnchorValid = false;
                            _armaPescaDefensiva = "";
                            WTSocketBot.PublicLogger.LogInfo(WildTerraBot.Properties.Resources.UdpRunnerFishingDisabled);
                        }





                    }
                    // ...
                    else if (p[0] == "RETURN_HOME")
                    {
                        ResetModes(); _returningHome = true; _botAtivo = true; _lastMoveTarget = null;
                        if (p.Length >= 3) { try { float hx = float.Parse(p[1], CultureInfo.InvariantCulture); float hz = float.Parse(p[2], CultureInfo.InvariantCulture); _homeCoordsBackup = new Vector3(hx, 0, hz); WTSocketBot.PublicLogger.LogInfo(string.Format(WildTerraBot.Properties.Resources.UdpRunnerHomeCoordinatesReceivedFormat, hx, hz)); } catch { } }
                        else { WTSocketBot.PublicLogger.LogInfo(WildTerraBot.Properties.Resources.UdpRunnerHomeReturningClaimPoint); }
                    }
                    else if (p[0] == "BOT_STATUS")
                    {
                        _botAtivo = (p[1] == "ON");
                        if (!_botAtivo) { ResetModes(); _lastMoveTarget = null; StopAllCoroutines(); _isMountingRoutineActive = false; _combatTarget = null; WTSocketBot.IsFishingBotActive = false; EnviarCombateStatus(false); }
                    }
                    else if (p[0] == "MOUNT_CONFIG") { _useMount = (p[1] == "ON"); }
                    else if (p[0] == "RESET_MODES")
                    {
                        // Cancela estados internos (caça/coleta/pesca/returnHome) para liberar navegação/montaria em deslocamentos especiais (ex.: banco).
                        ResetModes();
                        while (actionQueue.TryDequeue(out _)) { }
                        _combatTarget = null;
                        _resumeFishingAfterCombat = false;
                        ClearPostCombatSkinning();
                        _fishingAnchorValid = false;
                    }

                    else if (p[0] == "INSPECT_PLAYER")
                    {
                        string requestedPlayerName = p.Length >= 2 ? p[1].Trim() : "";

                        mainThreadActions.Enqueue(() =>
                        {
                            try
                            {
                                if (_playerInspectService == null)
                                    _playerInspectService = new PlayerInspectService();

                                if (MeuPersonagem == null)
                                {
                                    EnviarMensagem("INSPECT_ERROR;Personagem local indisponivel.");
                                    return;
                                }

                                if (_playerInspectService.TryBuildReport(MeuPersonagem, requestedPlayerName, out string resolvedPlayerName, out string report, out string error))
                                {
                                    EnviarInspectReport(requestedPlayerName, resolvedPlayerName, report);
                                }
                                else
                                {
                                    EnviarMensagem("INSPECT_ERROR;" + SanitizeUdpToken(error));
                                }
                            }
                            catch (Exception ex)
                            {
                                EnviarMensagem("INSPECT_ERROR;" + SanitizeUdpToken(ex.Message));
                            }
                        });
                    }

                    else if (p[0] == "TEST_MOUNT") { mainThreadActions.Enqueue(() => { if (MeuPersonagem is WTPlayer wtPlayer) ToggleMount(wtPlayer); }); }
                    else if (p[0] == "SAFE_LIST") { string[] itens = p[1].Split('~'); mainThreadActions.Enqueue(() => { itensSeguros.Clear(); foreach (var i in itens) itensSeguros.Add(i.Trim()); }); }
                    else if (p[0] == "DROP_LIST")
                    {
                        string[] itens = p[1].Split('~');
                        mainThreadActions.Enqueue(() =>
                        {
                            itensDropar.Clear();

                            foreach (var i in itens)
                            {
                                var tok = NormalizeListToken(i);
                                if (!string.IsNullOrWhiteSpace(tok))
                                    itensDropar.Add(tok);
                            }
                        });
                    }
                    else if (p[0] == "EAT_LIST") { string[] itens = p[1].Split('~'); mainThreadActions.Enqueue(() => { itensComer.Clear(); foreach (var i in itens) if (!string.IsNullOrWhiteSpace(i)) itensComer.Add(i.Trim()); }); }
                    else if (p[0] == "EAT_STATUS_LIST") { string payload = (p.Length >= 2) ? p[1] : ""; string[] itens = payload.Split('~'); mainThreadActions.Enqueue(() => { itensComerStatus.Clear(); foreach (var i in itens) if (!string.IsNullOrWhiteSpace(i)) itensComerStatus.Add(i.Trim()); _autoStatusFood.ReplaceConfiguredItems(itensComerStatus); try { WTSocketBot.PublicLogger.LogInfo(string.Format(WildTerraBot.Properties.Resources.UdpRunnerAutoEatStatusListReceivedFormat, itensComerStatus.Count, string.Join(", ", itensComerStatus))); } catch { } }); }
                    else if (p[0] == "HARVEST_LIST") { string payload = (p.Length >= 2) ? p[1] : ""; string[] itens = payload.Split('~'); mainThreadActions.Enqueue(() => { string normPayload = payload ?? ""; if (normPayload != _lastHarvestListPayload) { _lastHarvestListPayload = normPayload; itensColeta.Clear(); foreach (var i in itens) { var tok = NormalizeListToken(i); if (!string.IsNullOrWhiteSpace(tok)) itensColeta.Add(tok); } try { WTSocketBot.PublicLogger.LogInfo(string.Format(WildTerraBot.Properties.Resources.UdpRunnerCollectListReceivedWithExampleFormat, itensColeta.Count, string.Join(", ", itensColeta.Take(10)))); } catch { WTSocketBot.PublicLogger.LogInfo(string.Format(WildTerraBot.Properties.Resources.UdpRunnerCollectListReceivedFormat, itensColeta.Count)); } } }); }
                    else if (p[0] == "EAT_THRESHOLD") { if (int.TryParse(p[1], out int val)) _eatThreshold = val; }
                    else if (p[0] == "DEPOSIT_ALL") { string alvo = p.Length >= 2 ? p[1] : ""; mainThreadActions.Enqueue(() => IniciarDepositoProximo(alvo)); }
                    else if (p[0] == "TAMING")
                    {
                        if (p.Length >= 2 && string.Equals(p[1], "OFF", StringComparison.OrdinalIgnoreCase))
                        {
                            mainThreadActions.Enqueue(() => { try { _tamingDefenseActive = false; _tamingDefenseTarget = null; _tamingController?.Disable(); } catch { } });
                        }
                        else if (p.Length >= 2 && string.Equals(p[1], "ON", StringComparison.OrdinalIgnoreCase))
                        {
                            string mode = (p.Length >= 3) ? p[2].Trim() : "PACIFICO";
                            string trapName = (p.Length >= 4) ? p[3].Trim() : "";
                            string targetsPayload = (p.Length >= 5) ? (p[4] ?? "") : "";
                            string combatWeaponName = (p.Length >= 6) ? p[5].Trim() : "";
                            var cfg = new TamingController.Config();
                            cfg.Mode = string.IsNullOrWhiteSpace(mode) ? "PACIFICO" : mode;
                            cfg.TrapName = trapName;
                            cfg.CombatWeaponName = combatWeaponName;
                            cfg.TargetNames = targetsPayload.Split('~').Select(s => NormalizeListToken(s)).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

                            mainThreadActions.Enqueue(() =>
                            {
                                try
                                {
                                    ResetModes();
                                    _combatTarget = null;
                                    _tamingDefenseActive = false;
                                    _tamingDefenseTarget = null;
                                    try { _trainingController?.Disable(); } catch { }
                                    _tamingController?.Enable(cfg);
                                }
                                catch { }
                            });
                        }
                    }
                    else if (p[0] == "TRAINING")
                    {
                        // TRAINING;ON;skillsEnabled;buffEnabled;recoveryEnabled;buffRefreshSeconds;hpThreshold;spThreshold;skills(~);buffItems(~);recovery(~)
                        // TRAINING;OFF
                        if (p.Length >= 2 && string.Equals(p[1], "OFF", StringComparison.OrdinalIgnoreCase))
                        {
                            mainThreadActions.Enqueue(() =>
                            {
                                try { _trainingController?.Disable(); } catch { }
                            });
                        }
                        else if (p.Length >= 2 && string.Equals(p[1], "ON", StringComparison.OrdinalIgnoreCase))
                        {
                            var cfg = TrainingModeConfig.FromUdpParts(p);
                            mainThreadActions.Enqueue(() =>
                            {
                                try
                                {
                                    string error;
                                    if (!cfg.Validate(out error))
                                    {
                                        try { WTSocketBot.PublicLogger.LogWarning(string.Format(WildTerraBot.Properties.Resources.UdpRunnerTrainingInvalidConfigFormat, error)); } catch { }
                                        return;
                                    }

                                    ResetModes();
                                    _combatTarget = null;
                                    _returningHome = false;
                                    _resumeFishingAfterCombat = false;
                                    ClearPostCombatSkinning();
                                    _fishingAnchorValid = false;
                                    _tamingDefenseActive = false;
                                    _tamingDefenseTarget = null;

                                    try { _healTrainer?.Disable(); } catch { }
                                    try { _tamingController?.Disable(); } catch { }

                                    _trainingController?.Enable(cfg);
                                }
                                catch { }
                            });
                        }
                    }
                    else if (p[0] == "HEALTRAIN")
                    {
                        // HEALTRAIN;ON;weapon;targetMode;radius;skills(~);targets(~);followEnabled;followSkill;followTargetHpPct;followDistance;selfRecoveryItems(~);selfRecoveryHpPct;selfRecoveryResumeHpPct
                        // HEALTRAIN;OFF
                        if (p.Length >= 2 && string.Equals(p[1], "OFF", StringComparison.OrdinalIgnoreCase))
                        {
                            mainThreadActions.Enqueue(() => { try { _healTrainer?.Disable(); } catch { } });

                        }
                        else if (p.Length >= 2 && string.Equals(p[1], "ON", StringComparison.OrdinalIgnoreCase))
                        {
                            string weapon = (p.Length >= 3) ? p[2].Trim() : "";
                            string mode = (p.Length >= 4) ? p[3].Trim() : "PET";
                            int radius = 18;
                            if (p.Length >= 5) int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out radius);
                            string skillsPayload = (p.Length >= 6) ? (p[5] ?? "") : "";
                            string targetsPayload = (p.Length >= 7) ? (p[6] ?? "") : "";


                            bool followEnabled = false;
                            if (p.Length >= 8)
                            {
                                string raw = (p[7] ?? "").Trim();
                                followEnabled =
                                    raw == "1" ||
                                    raw.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
                                    raw.Equals("ON", StringComparison.OrdinalIgnoreCase);
                            }

                            string followSkill = (p.Length >= 9) ? p[8].Trim() : "";

                            int followTargetHpPct = 75;
                            if (p.Length >= 10) int.TryParse(p[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out followTargetHpPct);

                            float followDistance = 4.5f;
                            if (p.Length >= 11) float.TryParse(p[10], NumberStyles.Float, CultureInfo.InvariantCulture, out followDistance);

                            string selfRecoveryItemsPayload = (p.Length >= 12) ? (p[11] ?? "") : "";

                            int selfRecoveryHpPct = 40;
                            if (p.Length >= 13) int.TryParse(p[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out selfRecoveryHpPct);

                            int selfRecoveryResumeHpPct = 55;
                            if (p.Length >= 14) int.TryParse(p[13], NumberStyles.Integer, CultureInfo.InvariantCulture, out selfRecoveryResumeHpPct);






                            var cfg = new HealTrainingController.Config();
                            cfg.WeaponName = weapon;
                            cfg.TargetMode = string.IsNullOrWhiteSpace(mode) ? "PET" : mode;
                            cfg.Radius = Math.Max(1, radius);
                            cfg.SkillNames = skillsPayload.Split('~').Select(s => (s ?? "").Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                            cfg.TargetNames = targetsPayload.Split('~').Select(s => (s ?? "").Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

                            cfg.FollowTopTargetEnabled = followEnabled;
                            cfg.FollowSkillName = followSkill;
                            cfg.FollowTargetHpPct = Mathf.Clamp(followTargetHpPct, 1, 100);
                            cfg.FollowDistance = Mathf.Max(0.5f, followDistance);
                            cfg.SelfRecoveryItemNames = selfRecoveryItemsPayload.Split('~').Select(s => (s ?? "").Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                            cfg.SelfRecoveryHpPct = Mathf.Clamp(selfRecoveryHpPct, 1, 100);
                            cfg.SelfRecoveryResumeHpPct = Mathf.Clamp(selfRecoveryResumeHpPct, 1, 100);









                            mainThreadActions.Enqueue(() =>
                            {
                                try
                                {

                                    ResetModes(); // não misturar com pesca/coleta/caça
                                    _combatTarget = null;
                                    _returningHome = false;
                                    _resumeFishingAfterCombat = false;
                                    ClearPostCombatSkinning();
                                    _fishingAnchorValid = false;
                                    _tamingDefenseActive = false;
                                    _tamingDefenseTarget = null;




                                    try { _trainingController?.Disable(); } catch { }
                                    try { _tamingController?.Disable(); } catch { }

                                    _healTrainer?.Enable(cfg);

                                }
                                catch { }


                            });


                        }


                    }



                }
                catch { }
            }
        }

        private void ResetModes()
        {
            if (DBG_MOUNT)
                DbgMount(string.Format(WildTerraBot.Properties.Resources.UdpRunnerResetModesBeforeFormat, _modoColeta, _modoHunter, _modoPesca, _returningHome));

            _modoColeta = false;
            _modoHunter = false;
            _returningHome = false;
            _modoPesca = false;

            if (DBG_MOUNT)
                DbgMount(string.Format(WildTerraBot.Properties.Resources.UdpRunnerResetModesAfterFormat, _modoColeta, _modoHunter, _modoPesca, _returningHome));
        }

        void Update()
        {
            if (MeuPersonagem == null) return;
            WTPlayer wtPlayer = MeuPersonagem as WTPlayer;
            if (wtPlayer == null) return;

            if (DBG_MOUNT)
            {
                bool mounted = CheckIsMounted(wtPlayer);
                if (_dbgLastMounted == null || mounted != _dbgLastMounted.Value)
                {
                    DbgMount(string.Format(WildTerraBot.Properties.Resources.UdpRunnerMountStateFormat, (mounted ? WildTerraBot.Properties.Resources.UdpRunnerMounted : WildTerraBot.Properties.Resources.UdpRunnerDismounted), _modoColeta, _modoHunter, _useMount, (_lastMoveTarget.HasValue ? _lastMoveTarget.Value.ToString() : "null")));
                    _dbgLastMounted = mounted;
                }
            }

            while (mainThreadActions.TryDequeue(out var action)) action.Invoke();








            // Modo treinamento é exclusivo (não mistura com pesca/coleta/rota/cura/doma).
            if (_trainingController != null && _trainingController.IsEnabled)
            {
                TryAutoEat(wtPlayer);
                _trainingController.Tick(wtPlayer);
                return;
            }

            // Modo cura é exclusivo (não mistura com pesca/coleta/rota).
            if (_healTrainer != null && _healTrainer.IsEnabled)
            {
                // Mesmo com o BOT OFF, queremos auto-alimentação (aba Food) durante treino de cura.
                // (TryAutoEat já tem cooldown e respeita a lista EAT_LIST + threshold.)
                TryAutoEat(wtPlayer);
                _healTrainer.Tick(wtPlayer);
                return;


            }

            bool emCombateReal = CheckInCombat(wtPlayer);
            int currentHp = wtPlayer.health;
            bool tomandoDano = (_lastKnownHp != -1 && currentHp < _lastKnownHp);
            if (tomandoDano) _lastDamageTime = Time.time;
            _lastKnownHp = currentHp;

            try
            {
                if (Time.time >= nextStatsSend) { nextStatsSend = Time.time + 0.1f; EnviarStats(); }
                if (Time.time >= nextRadarScan) { nextRadarScan = Time.time + 0.5f; EnviarRadar(); }
                if (Time.time >= nextBagSend) { nextBagSend = Time.time + 1.0f; EnviarBag(); }
            }
            catch { }

            if (_tamingController != null && _tamingController.IsEnabled)
            {
                bool tamingActiveContext = false;
                bool tamingBusyBefore = false;
                bool tamingPausedByDefense = false;
                WTMob tameTarget = null;

                try { tamingBusyBefore = _tamingController.IsBusy; } catch { }
                try { tamingPausedByDefense = _tamingController.IsPausedByDefense; } catch { }
                try { tameTarget = _tamingController.CurrentTarget; } catch { }
                tamingActiveContext = true;


                WTMob externalThreat = null;
                if (tamingActiveContext)
                {
                    try { externalThreat = PickTamingDefenseTarget(wtPlayer, tameTarget, 8f); } catch { }
                }

                if (externalThreat != null)
                {
                    if (!_tamingDefenseActive || _tamingDefenseTarget != externalThreat)
                    {
                        _tamingDefenseActive = true;
                        _tamingDefenseTarget = externalThreat;
                        try
                        {
                            _tamingController.PauseForDefense($"agressor externo '{externalThreat.name}' dist={Vector3.Distance(wtPlayer.transform.position, externalThreat.transform.position):F1}");
                        }
                        catch { }
                        TamingLog($"[TAMING-DEF] agressor externo detectado alvo='{externalThreat.name}' dist={Vector3.Distance(wtPlayer.transform.position, externalThreat.transform.position):F1}");
                    }
                }

                if (_tamingDefenseActive)
                {
                    bool defenseDone = false;

                    if (_tamingDefenseTarget == null || !IsValidTarget(_tamingDefenseTarget))
                    {
                        defenseDone = true;
                    }
                    else
                    {
                        _combatTarget = _tamingDefenseTarget;

                        string defenseWeapon = "";
                        try { defenseWeapon = _tamingController.CombatWeaponName; } catch { defenseWeapon = ""; }
                        if (string.IsNullOrWhiteSpace(defenseWeapon)) defenseWeapon = _armaPreferida;

                        if (!string.IsNullOrWhiteSpace(defenseWeapon))
                        {

                            _nextEquipCheck = 0f;
                            CheckAndEquipItem(wtPlayer, defenseWeapon, 0, ignoreThrottle: true);
                        }

                        bool fightingDefense = RunCombatLogic(wtPlayer, false);
                        if (fightingDefense)
                        {
                            if (!_reportouCombate) { EnviarCombateStatus(true); }

                            if (moveQueue != null)
                            {
                                Vector2 pendingMove;
                                while (moveQueue.TryDequeue(out pendingMove))
                                {
                                    _lastMoveTarget = new Vector3(pendingMove.x, wtPlayer.transform.position.y, pendingMove.y);
                                }
                            }
                            return;
                        }

                        if (_combatTarget == null || !IsValidTarget(_combatTarget))
                        {
                            defenseDone = true;
                        }
                        else
                        {
                            return;
                        }
                    }

                    if (defenseDone)
                    {
                        TamingLog($"[TAMING-DEF] combate defensivo concluído target='{(_tamingDefenseTarget != null ? _tamingDefenseTarget.name : "null")}'");
                        _tamingDefenseActive = false;
                        _tamingDefenseTarget = null;
                        _combatTarget = null;
                        try { _tamingController.ResumeAfterDefense(wtPlayer); } catch { }
                    }
                }

                _tameDbg?.Tick(wtPlayer);

                bool tamingBusy = false;
                try { tamingBusy = _tamingController.Tick(wtPlayer); } catch (Exception ex) { TamingLog($"[TAMING] exception {ex.GetType().Name}: {ex.Message}"); }

                bool tamingStillBlocking = false;
                try { tamingStillBlocking = _tamingController.IsBusy || _tamingController.IsPausedByDefense || _tamingController.CurrentTarget != null; } catch { tamingStillBlocking = tamingBusyBefore || tamingBusy; }

                if (tamingBusyBefore || tamingBusy || tamingStillBlocking)
                {
                    if (_combatTarget != null) _combatTarget = null;

                    if (moveQueue != null)
                    {
                        Vector2 pendingMove;
                        while (moveQueue.TryDequeue(out pendingMove))
                        {
                            _lastMoveTarget = new Vector3(pendingMove.x, wtPlayer.transform.position.y, pendingMove.y);
                        }
                    }

                    return;
                }
            }

            // Defesa/combate devem rodar quando o BOT principal estiver ON
            // ou quando a pesca independente estiver ativa.
            // Motivo: a pesca é iniciada via FISHING;ON sem BOT_STATUS;ON,
            // então _botAtivo pode ficar false mesmo com _modoPesca ativo.
            // Ainda preservamos _botAtivo para cobrir deslocamentos fora dos modos específicos.
            bool combatePermitido = _botAtivo || _modoPesca;


            // Se não é permitido, garantimos que não fica "preso" em alvo antigo nem reportando combate.
            if (!combatePermitido)
            {
                if (_combatTarget != null) _combatTarget = null;
                if (_reportouCombate) EnviarCombateStatus(false);
            }

            // LÓGICA DE DEFESA
            // LÓGICA DE DEFESA (não roda durante DOMAR; domar já controla ataque/equip)


            if (combatePermitido && (tomandoDano || emCombateReal))
            {
                if (DBG_COMBAT && !_botAtivo && !_modoPesca && !_modoColeta && !_modoHunter && !_returningHome)
                {
                    if (Time.time >= _dbgNextDefenseOffWarn)
                    {
                        _dbgNextDefenseOffWarn = Time.time + 3.0f;
                        DbgCombat($"[DEFESA] ATIVOU com BOT OFF | tomandoDano={tomandoDano} emCombateReal={emCombateReal} hp={currentHp} lastDamageAgo={(Time.time - _lastDamageTime):F1}s target={(wtPlayer.target != null ? wtPlayer.target.name : "null")}");
                    }
                }
                // Se estava pescando, precisamos retomar a pesca exatamente no mesmo ponto/direção após o combate
                if (_modoPesca)
                {
                    _resumeFishingAfterCombat = true;
                    _nextEquipCheck = 0f; // permite equipar arma imediatamente ao ser atacado durante a pesca
                    if (!_fishingAnchorValid) CaptureFishingAnchor(wtPlayer);

                    // Garante que a arma defensiva da pesca seja usada (txtWeaponName)
                    if (!string.IsNullOrEmpty(_armaPescaDefensiva)) _armaPreferida = _armaPescaDefensiva;
                }

                if (_combatTarget == null)
                {
                    _combatTarget = PickAggressorSmart(wtPlayer, 8f);

                    if (DBG_COMBAT && Time.time >= _dbgNextPickAggressorLog)
                    {
                        _dbgNextPickAggressorLog = Time.time + 1.0f;
                        DbgCombat($"[COMBAT] PickAggressorSmart -> {(_combatTarget != null ? MobDbg(_combatTarget) : "null")} | tomandoDano={tomandoDano} emCombateReal={emCombateReal} me.target={(wtPlayer.target != null ? wtPlayer.target.name : "null")}");
                    }

                    // Se estamos tomando dano/em combate mas não achou agressor, dumpa candidatos para diagnóstico.
                    if (_combatTarget == null && (tomandoDano || emCombateReal))
                    {
                        DbgDumpAggressors(wtPlayer, 8f, 4);
                    }
                }

                if (_combatTarget != null || emCombateReal)
                {
                    // Tenta equipar arma se definida
                    if (!string.IsNullOrEmpty(_armaPreferida))
                    {
                        bool forceEquip = false;
                        try { forceEquip = (_tamingController != null && _tamingController.IsEnabled); } catch { forceEquip = false; }
                        if (forceEquip) _nextEquipCheck = 0f;
                        CheckAndEquipItem(wtPlayer, _armaPreferida, 0, ignoreThrottle: forceEquip);

                    }

                    bool lutando = RunCombatLogic(wtPlayer, _modoHunter);
                    if (lutando)
                    {
                        // DIAGNÓSTICO: se o target já está inválido mas o combate retornou "lutando",
                        // o Update vai retornar antes de chegar no bloco que limpa _combatTarget.
                        if (DBG_COMBAT && _combatTarget != null && !IsValidTarget(_combatTarget) && Time.time >= _dbgNextInvalidTargetWarn)
                        {
                            _dbgNextInvalidTargetWarn = Time.time + 0.8f;
                            DbgCombat($"[COMBAT-WARN] Update retornando com alvo inválido (limpeza será pulada neste tick) | tomandoDano={tomandoDano} emCombateReal={emCombateReal} hp={currentHp} alvo={MobDbg(_combatTarget)}");
                            DbgDumpAggressors(wtPlayer, 8f, 4);
                        }


                        if (!_reportouCombate) { EnviarCombateStatus(true); }
                        return; // SAI DO UPDATE
                    }
                }
            }

            if (_reportouCombate) EnviarCombateStatus(false);

            if (_combatTarget != null)
            {
                if (!IsValidTarget(_combatTarget))
                {
                    if (_fiWorldObject != null)
                    {
                        WTObject corpse = _fiWorldObject.GetValue(_combatTarget) as WTObject;
                        if (corpse != null && corpse.isActiveAndEnabled)
                        {
                            // Se o combate aconteceu durante a pesca, priorizamos esfolar antes de voltar à âncora
                            if (_modoPesca && _resumeFishingAfterCombat)
                            {
                                if (ShouldSkinDuringFishing(corpse))
                                {
                                    TryStartPostCombatSkinning(wtPlayer, corpse);
                                }
                                else
                                {
                                    WTSocketBot.PublicLogger.LogInfo($"[PESCA] Pós-combate: corpse '{corpse.name}' ignorado (não está em txtListaColeta). Retomando pesca...");
                                }
                            }
                            else
                            {
                                TrySkinCorpse(wtPlayer, corpse);
                            }
                        }
                    }
                    _combatTarget = null;
                }
            }
            else if (_modoHunter)
            {

                bool alvoValido = _combatTarget != null && IsValidTarget(_combatTarget);
                if (!alvoValido || !IsMobType(_combatTarget, _alvoHunterTipo))
                {
                    _combatTarget = BuscarMobPorTipo(wtPlayer, _alvoHunterTipo);
                }
            }

            // CAÇA PROATIVA: se estamos em modo hunter e já temos um alvo válido, engajar mesmo sem tomar dano.
            // Isso evita o cenário do arco/flecha onde o bot só começa a atirar depois do primeiro hit manual.
            if (combatePermitido && _modoHunter && _combatTarget != null && IsValidTarget(_combatTarget))
            {
                if (!string.IsNullOrEmpty(_armaPreferida))
                {

                    bool forceEquip = false;
                    try { forceEquip = (_tamingController != null && _tamingController.IsEnabled); } catch { forceEquip = false; }
                    if (forceEquip) _nextEquipCheck = 0f;
                    CheckAndEquipItem(wtPlayer, _armaPreferida, 0, ignoreThrottle: forceEquip);

                }

                bool lutandoHunter = RunCombatLogic(wtPlayer, true);
                if (lutandoHunter)
                {
                    if (!_reportouCombate) { EnviarCombateStatus(true); }
                    return;
                }
            }

            else if (combatePermitido && _combatTarget == null)
            {
                float range = _modoPesca ? 5f : 12f;
                _combatTarget = PickAggressorSmart(wtPlayer, range);
            }

            if (_modoPesca)
            {
                // Pós-combate na pesca: se houver esfolagem pendente, priorize isso imediatamente
                if (_postCombatFishingState != PostCombatFishingState.None && !emCombateReal && !tomandoDano)
                {
                    bool doneSkinNow = HandlePostCombatSkinning(wtPlayer);
                    if (!doneSkinNow) return;
                }


                // Se houve combate durante a pesca, volta para posição/direção e reequipa a vara antes de continuar.
                if (_resumeFishingAfterCombat && !emCombateReal && !tomandoDano && (Time.time - _lastDamageTime) > 0.75f)
                {
                    // Prioridade: se houver corpse para esfolar (pós-combate), faça isso ANTES de retornar à âncora.
                    if (_postCombatFishingState != PostCombatFishingState.None)
                    {
                        bool doneSkin = HandlePostCombatSkinning(wtPlayer);
                        if (!doneSkin) return; // ainda movendo/esfolando
                    }

                    bool ok = HandleReturnToFishingAnchor(wtPlayer);
                    if (!ok) return;

                    // Libera casting imediato após alinhar
                    _resumeFishingAfterCombat = false;
                    _nextCastCheck = 0f;
                }

                RunFishingLogic(wtPlayer);
                return;
            }

            if (!_botAtivo) return;
            if (_depositando) return;

            if (moveQueue.TryDequeue(out Vector2 nextPos))
            {
                _lastMoveTarget = new Vector3(nextPos.x, wtPlayer.transform.position.y, nextPos.y);
            }

            if (_returningHome)
            {
                Vector3 home = wtPlayer.claimPoint;
                if (home == Vector3.zero && _homeCoordsBackup != Vector3.zero) { home = _homeCoordsBackup; home.y = wtPlayer.transform.position.y; }

                if (home != Vector3.zero)
                {
                    if (Vector3.Distance(wtPlayer.transform.position, home) > 5.0f)
                    {
                        MoveToXZ(wtPlayer, home.x, home.z);
                        if (_useMount && !CheckIsMounted(wtPlayer) && !_isMountingRoutineActive) StartCoroutine(MountSequence(wtPlayer));
                    }
                    else
                    {
                        WTSocketBot.PublicLogger.LogInfo(WildTerraBot.Properties.Resources.UdpRunnerHomeArrivedPaused);
                        _returningHome = false; _botAtivo = false;
                        EnviarMensagem("BOT_STATUS;OFF");
                    }
                }
                return;
            }

            if (DBG_MOUNT && _useMount && _lastMoveTarget.HasValue && !CheckIsMounted(wtPlayer))
            {
                if (Time.time > _dbgNextAutoMountLog)
                {
                    _dbgNextAutoMountLog = Time.time + 2.0f;

                    float dist = Vector3.Distance(wtPlayer.transform.position, _lastMoveTarget.Value);
                    bool underThreat = CheckInCombat(wtPlayer) || ((Time.time - _lastDamageTime) < 5.0f) || (_combatTarget != null && IsValidTarget(_combatTarget));

                    string motivo =
                        (Time.time < _pauseMovementUntil) ? $"PAUSE({_pauseMovementUntil - Time.time:0.00}s)" :
                        (_isMountingRoutineActive) ? "MOUNTING_ROUTINE_ACTIVE" :
                        (underThreat) ? $"EM_COMBATE(dmgAgo={(Time.time - _lastDamageTime):0.0}s)" :

                        (_modoColeta || _modoHunter) ? $"BLOQUEADO_MODOS(coleta={_modoColeta},hunter={_modoHunter})" :
                        (dist <= 5.0f) ? $"DIST_CURTA({dist:0.0})" :
                        "OK_DISPARARIA";

                    DbgMount(string.Format(WildTerraBot.Properties.Resources.UdpRunnerAutoMountCheckFormat, dist, motivo));
                }
            }

            if (_isMountingRoutineActive) return;
            if (Time.time < _pauseMovementUntil) return;

            if (_useMount && !_modoColeta && !_modoHunter && _lastMoveTarget.HasValue && !CheckIsMounted(wtPlayer))

            {
                // Não tente montar se estamos em combate ou tomamos dano recentemente.
                // Motivo: o toggle de montaria costuma falhar em combate (ou cancela), gerando loop de MountSequence + PAUSE,
                // e o bot pode morrer sem reagir.
                bool underThreat = CheckInCombat(wtPlayer) || ((Time.time - _lastDamageTime) < 5.0f) || (_combatTarget != null && IsValidTarget(_combatTarget));
                if (underThreat)
                {
                    if (DBG_MOUNT && Time.time > _dbgNextAutoMountLog)
                    {
                        _dbgNextAutoMountLog = Time.time + 2.0f;
                        DbgMount(string.Format(WildTerraBot.Properties.Resources.UdpRunnerAutoMountSuppressCombatFormat, (Time.time - _lastDamageTime), CheckInCombat(wtPlayer), (_combatTarget != null ? MobDbg(_combatTarget) : "null")));
                    }
                    // deixa o Update seguir; a lógica de defesa vai rodar acima
                }
                else
                {
                    float simpleDist = Vector3.Distance(wtPlayer.transform.position, _lastMoveTarget.Value);
                    if (simpleDist > 5.0f) { StartCoroutine(MountSequence(wtPlayer)); return; }


                }

            }

            if (Time.time >= nextDropCheck) { nextDropCheck = Time.time + 0.5f; ProcessarDrops(wtPlayer); }
            if (Time.time >= nextEatCheck) { TryAutoEat(wtPlayer); }
            if (Time.time >= nextBagSend - 0.5f) { VerificarRequisitosPendentes(wtPlayer); CheckBagFull(); }

            if (!CheckStamina(wtPlayer)) return;

            CleanupStaleHarvestStateIfNeeded("update");

            if (actionQueue.TryDequeue(out var targetName))
            {
                _modoColeta = true;
                int requestedWorldId = Interlocked.Exchange(ref _queuedHarvestWorldId, 0);
                TryHarvestObjectNative(wtPlayer, targetName, requestedWorldId);
            }

            // Watchdog do harvest (flank anti-travamento)
            HarvestWatchdog(wtPlayer);




            if (_lastMoveTarget.HasValue && !HarvestAutoMoveActive(wtPlayer))

            {
                MoveToXZ(wtPlayer, _lastMoveTarget.Value.x, _lastMoveTarget.Value.z);
            }
        }

        // ===========================================
        // ============= ROTINA DE DEPÓSITO ==========
        // ===========================================
        void IniciarDepositoProximo(string nomeAlvo = "")
        {
            if (_depositando) return;
            WTPlayer wtPlayer = MeuPersonagem as WTPlayer;
            if (wtPlayer == null) return;

            if (DBG_MOUNT)
            {
                bool mounted = CheckIsMounted(wtPlayer);
                if (_dbgLastMounted == null || mounted != _dbgLastMounted.Value)
                {
                    DbgMount(string.Format(WildTerraBot.Properties.Resources.UdpRunnerMountStateFormat, (mounted ? WildTerraBot.Properties.Resources.UdpRunnerMounted : WildTerraBot.Properties.Resources.UdpRunnerDismounted), _modoColeta, _modoHunter, _useMount, (_lastMoveTarget.HasValue ? _lastMoveTarget.Value.ToString() : "null")));
                    _dbgLastMounted = mounted;
                }
            }

            // Busca baú próximo (raio 15m)
            WTStructure bau = WTBankingUtils.FindBestBankChest(wtPlayer.transform.position, 15.0f, nomeAlvo);

            if (bau != null)
            {
                WTSocketBot.PublicLogger.LogInfo(string.Format(WildTerraBot.Properties.Resources.UdpRunnerBankTargetFoundFormat, bau.name));
                StartCoroutine(RotinaDeposito(wtPlayer, bau));
            }
            else
            {
                WTSocketBot.PublicLogger.LogError(string.Format(WildTerraBot.Properties.Resources.UdpRunnerBankChestNotFoundFormat, nomeAlvo));
                EnviarMensagem("BANK_FINISH");
            }
        }

        IEnumerator RotinaDeposito(WTPlayer wtPlayer, WTStructure bau)
        {
            _depositando = true;
            WTSocketBot.PublicLogger.LogInfo(WildTerraBot.Properties.Resources.UdpRunnerBankMovingToChest);

            // 1. Vai até o baú
            var agent = wtPlayer.agent;
            if (agent != null && agent.enabled && agent.isOnNavMesh)
            {
                agent.isStopped = false;
                agent.stoppingDistance = 0f;
                agent.destination = bau.transform.position;
            }

            float timeout = Time.time + 10f;
            while (Vector3.Distance(wtPlayer.transform.position, bau.transform.position) > 2.8f && Time.time < timeout)
                yield return new WaitForSeconds(0.2f);

            SafeStopAgent(wtPlayer);
            wtPlayer.transform.LookAt(bau.transform); // Vira para o baú
            yield return new WaitForSeconds(0.3f);

            // 2. Tenta Abrir
            ScriptableSkill openSkill = null;
            if (bau.worldType?.actions != null)
            {
                foreach (var act in bau.worldType.actions)
                {
                    if (act?.actionSkill != null && act.actionSkill.name.Contains("OpenContainer"))
                    {
                        openSkill = act.actionSkill;
                        break;
                    }
                }
            }

            if (openSkill == null && bau.actionSkills != null)
            {
                foreach (var s in bau.actionSkills)
                {
                    if (s.name == "OpenContainer") openSkill = s;
                }
            }

            if (openSkill == null)
            {
                WTSocketBot.PublicLogger.LogError(WildTerraBot.Properties.Resources.UdpRunnerBankOpenContainerSkillNotFound);
                _depositando = false;
                EnviarMensagem("BANK_FINISH");
                yield break;
            }

            wtPlayer.WorldObjectTryAction(bau, openSkill);

            yield return new WaitForSeconds(1.0f);

            // 3. Detecta Abas e Deposita (Lógica Restaurada)
            int openedTab = 0;
            if (GameManager.instance != null && GameManager.instance.IsContainerWindowOpen(out int t))
            {
                openedTab = t;
            }

            int tabsFromType = 0;
            try { if (bau != null && bau.worldType != null) tabsFromType = bau.worldType.containerTabs; } catch { }

            int totalTabs = (tabsFromType <= 0) ? 1 : tabsFromType;
            int maxTabs = Math.Min(totalTabs, 6);
            int baseCmdTab = openedTab;

            if (tabsFromType > 0 && baseCmdTab <= 0)
            {
                baseCmdTab = 1;
            }

            WTSocketBot.PublicLogger.LogInfo(string.Format(WildTerraBot.Properties.Resources.UdpRunnerBankTabsFormat, totalTabs, baseCmdTab));

            for (int tabOffset = 0; tabOffset < maxTabs; tabOffset++)
            {
                int cmdTab;
                if (tabsFromType > 0)
                {
                    cmdTab = baseCmdTab + tabOffset;
                    if (cmdTab > totalTabs) break;
                }
                else
                {
                    cmdTab = baseCmdTab;
                }

                if (wtPlayer.inventory != null)
                {
                    for (int i = wtPlayer.inventory.Count - 1; i >= 0; i--)
                    {
                        if (i >= wtPlayer.inventory.Count) continue;
                        var slot = wtPlayer.inventory[i];

                        if (slot.amount <= 0 || slot.item.data == null) continue;
                        if (MotivoParaManter(slot.item) != null) continue;

                        // Ação 2 = Store
                        wtPlayer.CmdInventoryItemAction(i, (ItemActionType)2, cmdTab);
                        yield return new WaitForSeconds(0.12f);
                    }
                }

                if (tabsFromType > 0) yield return new WaitForSeconds(0.2f);
            }

            yield return new WaitForSeconds(0.5f);
            wtPlayer.CloseContainer();

            WTSocketBot.PublicLogger.LogInfo(WildTerraBot.Properties.Resources.UdpRunnerBankDepositFinished);
            _depositando = false;
            EnviarMensagem("BANK_FINISH");
        }

        // ===========================================


        private void CaptureFishingAnchor(WTPlayer p)
        {
            if (p == null) return;
            _fishingAnchorPos = p.transform.position;
            _fishingAnchorYaw = p.transform.eulerAngles.y;
            _fishingAnchorValid = true;
        }

        /// <summary>
        /// Retorna para a posição e direção inicial da pesca.
        /// Retorna true quando o jogador já está alinhado e com a vara equipada.
        /// </summary>
        private bool HandleReturnToFishingAnchor(WTPlayer p)
        {
            if (!_fishingAnchorValid) return true;
            if (Time.time < _nextReturnToAnchorPulse) return false;
            _nextReturnToAnchorPulse = Time.time + 0.15f;

            Vector3 cur = p.transform.position;
            Vector3 anchor = new Vector3(_fishingAnchorPos.x, cur.y, _fishingAnchorPos.z);

            float dx = cur.x - anchor.x;
            float dz = cur.z - anchor.z;
            float distXZ = Mathf.Sqrt(dx * dx + dz * dz);

            const float POS_TOL = 0.12f;
            if (distXZ > POS_TOL)
            {
                MoveToXZ(p, anchor.x, anchor.z);
                return false;
            }

            SafeStopAgent(p);

            // Snap final (quando possível)
            try
            {
                var agent = p.agent;
                if (agent != null && agent.enabled && agent.isOnNavMesh)
                    agent.Warp(anchor);
                else
                    p.transform.position = anchor;
            }
            catch { }

            // Restaura direção
            p.transform.eulerAngles = new Vector3(0f, _fishingAnchorYaw, 0f);

            // Reequipa a vara antes de continuar
            if (!string.IsNullOrEmpty(_nomeVaraPesca))
            {
                bool temVara = false;
                try { temVara = (bool)_isFishingPoleEquippedMethod.Invoke(p, null); } catch { }

                if (!temVara)
                {
                    _nextEquipCheck = 0f; // garante re-equip imediato da vara após combate
                    bool ok = CheckAndEquipItem(p, _nomeVaraPesca, 0);
                    if (!ok) return false; // aguardando equip
                }
            }

            return true;
        }

        private void RunFishingLogic(WTPlayer p)
        {
            if (Time.time < _nextCastCheck) return;
            _nextCastCheck = Time.time + 2.0f;

            if (Time.time > _nextDebugLog)
            {
                DumpEquipment(p);
                _nextDebugLog = Time.time + 5.0f;
            }

            bool gameDizTemVara = false;
            try { gameDizTemVara = (bool)_isFishingPoleEquippedMethod.Invoke(p, null); } catch { }

            if (!gameDizTemVara)
            {
                if (!string.IsNullOrEmpty(_nomeVaraPesca)) { if (!CheckAndEquipItem(p, _nomeVaraPesca, 0)) return; }
            }

            bool temIsca = IsBaitEquippedAnywhere(p, _nomeIscaPesca);

            if (!temIsca)
            {
                if (!string.IsNullOrEmpty(_nomeIscaPesca)) { if (!TryEquipBait(p, _nomeIscaPesca)) return; }
            }

            try { gameDizTemVara = (bool)_isFishingPoleEquippedMethod.Invoke(p, null); } catch { }
            if (!gameDizTemVara) { WTSocketBot.PublicLogger.LogWarning(WildTerraBot.Properties.Resources.UdpRunnerFishingWaitingRod); return; }

            if (!IsBaitEquippedAnywhere(p, _nomeIscaPesca)) { WTSocketBot.PublicLogger.LogWarning(string.Format(WildTerraBot.Properties.Resources.UdpRunnerFishingWaitingBaitFormat, _nomeIscaPesca)); return; }

            bool pescando = false;
            try { pescando = (bool)_isFishingMethod.Invoke(p, null); } catch { }
            if (pescando) return;

            // Atualiza âncora da pesca em cada novo arremesso (somente quando não estamos retomando pós-combate)
            if (!_resumeFishingAfterCombat)
            {
                CaptureFishingAnchor(p);
            }

            Vector3 alvoAgua = p.transform.position + (p.transform.forward * 3.5f);
            WTSocketBot.PublicLogger.LogInfo(WildTerraBot.Properties.Resources.UdpRunnerFishingCasting);
            CmdSkillToPoint(p, alvoAgua);
        }

        private void DumpEquipment(WTPlayer p)
        {
            if (p.equipment == null) return;
            string log = WildTerraBot.Properties.Resources.UdpRunnerDebugEquipFormat;
            for (int i = 0; i < p.equipment.Count; i++)
            {
                var slot = p.equipment[i];
                if (slot.amount > 0 && slot.item.data != null)
                {
                    string name = slot.item.data.name;
                    log += $"[{i}]:{name} | ";
                }
            }
            WTSocketBot.PublicLogger.LogWarning(log);
        }

        private bool IsBaitEquippedAnywhere(WTPlayer p, string baitName)
        {
            if (p.equipment == null) return false;
            for (int i = 0; i < p.equipment.Count; i++)
            {
                var slot = p.equipment[i];
                if (slot.amount <= 0 || slot.item.data == null) continue;
                bool isBait = false;
                if (_isFishingBaitField != null) { try { isBait = (bool)_isFishingBaitField.GetValue(slot.item.data); } catch { } }
                string name = slot.item.data.name;
                bool nameMatch = (!string.IsNullOrEmpty(baitName) && name.IndexOf(baitName, StringComparison.OrdinalIgnoreCase) >= 0);
                if (isBait || nameMatch) return true;
            }
            return false;
        }

        private bool TryEquipBait(WTPlayer p, string itemName)
        {
            if (Time.time < _nextEquipCheck) return true;


            if (_equipmentOperationsAllowedMethod != null) { try { if (!(bool)_equipmentOperationsAllowedMethod.Invoke(p, null)) return false; } catch { } }
            if (p.inventory == null) return false;
            _nextEquipCheck = Time.time + 1.5f;
            for (int i = 0; i < p.inventory.Count; i++)
            {
                var slot = p.inventory[i];
                if (slot.amount > 0 && slot.item.data != null)
                {
                    if (slot.item.data.name.IndexOf(itemName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        int targetSlot = -1;
                        if (_findEquipableSlotForMethod != null) { try { targetSlot = (int)_findEquipableSlotForMethod.Invoke(slot.item.data, new object[] { p, i }); } catch { } }
                        if (targetSlot == -1) targetSlot = 1;
                        WTSocketBot.PublicLogger.LogInfo(string.Format(WildTerraBot.Properties.Resources.UdpRunnerEquipBaitFormat, slot.item.data.name, targetSlot));
                        if (_swapEquipMethod != null) try { _swapEquipMethod.Invoke(p, new object[] { i, targetSlot }); } catch { }
                        return false;
                    }
                }
            }
            WTSocketBot.PublicLogger.LogWarning(string.Format(WildTerraBot.Properties.Resources.UdpRunnerFishingBaitNotFoundFormat, itemName));
            return false;
        }


        private bool CheckAndEquipItem(WTPlayer p, string itemName, int targetSlot, bool ignoreThrottle = false)
        {
            if (!ignoreThrottle && Time.time < _nextEquipCheck) return true;

            // 0) Se já está equipado no slot solicitado, não tente re-equipar.
            // Isso evita loop quando há 2 armas iguais no inventário (ex.: MorbiumHealerTool).
            try
            {
                if (p != null && p.equipment != null && targetSlot >= 0 && targetSlot < p.equipment.Count)
                {
                    var es = p.equipment[targetSlot];
                    if (es.amount > 0 && es.item.data != null)
                    {
                        string en = es.item.data.name ?? "";
                        if (string.Equals(en, itemName, StringComparison.OrdinalIgnoreCase) && !es.item.IsBroken())
                            return true;
                    }
                }
            }
            catch { }


            try
            {
                MethodInfo getter = _getEquippedRightHand;
                object itemStruct = getter.Invoke(p, null);
                PropertyInfo hasVal = itemStruct.GetType().GetProperty("HasValue");
                if ((bool)hasVal.GetValue(itemStruct))
                {
                    object val = itemStruct.GetType().GetProperty("Value").GetValue(itemStruct);
                    object data = val.GetType().GetField("data").GetValue(val);
                    string name = (string)data.GetType().GetField("name").GetValue(data);

                    // IMPORTANTE: não considerar "equipado" se a arma estiver quebrada.
                    // Caso contrário, no modo Cura a arma quebra e nunca troca para a próxima cópia.
                    bool broken = false;
                    try
                    {
                        var mb = val.GetType().GetMethod("IsBroken", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (mb != null) broken = (bool)mb.Invoke(val, null);
                    }
                    catch { broken = false; }

                    if (string.Equals(name, itemName, StringComparison.OrdinalIgnoreCase) && !broken)
                        return true;
                }
            }
            catch { }



            // Alguns itens podem estar no off-hand; checa também a mão esquerda.
            try
            {
                MethodInfo getter = _getEquippedLeftHand;
                if (getter != null)
                {
                    object itemStruct = getter.Invoke(p, null);
                    PropertyInfo hasVal = itemStruct.GetType().GetProperty("HasValue");
                    if ((bool)hasVal.GetValue(itemStruct))
                    {
                        object val = itemStruct.GetType().GetProperty("Value").GetValue(itemStruct);
                        object data = val.GetType().GetField("data").GetValue(val);
                        string name = (string)data.GetType().GetField("name").GetValue(data);

                        bool broken = false;
                        try
                        {
                            var mb = val.GetType().GetMethod("IsBroken", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (mb != null) broken = (bool)mb.Invoke(val, null);
                        }
                        catch { broken = false; }

                        if (string.Equals(name, itemName, StringComparison.OrdinalIgnoreCase) && !broken)
                            return true;
                    }
                }
            }
            catch { }



            if (p.inventory == null) return false;
            if (!ignoreThrottle) _nextEquipCheck = Time.time + 1.5f;

            // 1) Se já existe um item desse nome equipado e NÃO está quebrado, não swapar (evita alternância de clones iguais).
            bool alreadyEquipped = false;
            try
            {
                if (p.equipment != null)
                {
                    for (int ei = 0; ei < p.equipment.Count; ei++)
                    {
                        var es = p.equipment[ei];
                        if (es.amount <= 0 || es.item.data == null) continue;
                        if (string.Equals(es.item.data.name ?? "", itemName, StringComparison.OrdinalIgnoreCase) && !es.item.IsBroken())
                        { alreadyEquipped = true; break; }
                    }
                }
            }
            catch { }
            if (alreadyEquipped) return true;

            // 2) Procurar na bag a PRIMEIRA CÓPIA NÃO QUEBRADA (durability > 0).
            //    Se a primeira cópia encontrada estiver quebrada, continuamos procurando a próxima.
            int bestIndex = -1;

            // 2.1) Preferir match EXATO (case-insensitive), pulando quebrados
            for (int i = 0; i < p.inventory.Count; i++)
            {
                var slot = p.inventory[i];
                if (slot.amount <= 0 || slot.item.data == null) continue;
                string n = slot.item.data.name ?? "";
                if (!string.Equals(n, itemName, StringComparison.OrdinalIgnoreCase)) continue;
                if (slot.item.IsBroken()) continue; // pula quebrados
                bestIndex = i;
                break;
            }

            // 2.2) Fallback: contém (comportamento antigo), também pulando quebrados
            if (bestIndex < 0)
            {
                for (int i = 0; i < p.inventory.Count; i++)
                {
                    var slot = p.inventory[i];
                    if (slot.amount <= 0 || slot.item.data == null) continue;
                    if ((slot.item.data.name ?? "").IndexOf(itemName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (slot.item.IsBroken()) continue; // pula quebrados
                    bestIndex = i;
                    break;
                }
            }

            if (bestIndex >= 0)
            {
                var slot = p.inventory[bestIndex];
                WTSocketBot.PublicLogger.LogInfo(string.Format(WildTerraBot.Properties.Resources.UdpRunnerEquipRodFormat, slot.item.data.name, targetSlot));
                if (_swapEquipMethod != null) try { _swapEquipMethod.Invoke(p, new object[] { bestIndex, targetSlot }); } catch { }
                return false;
            }
            return false;
        }

        private void CmdSkillToPoint(WTPlayer p, Vector3 point)
        {
            if (_cmdSkillToPoint == null) return;
            try { _cmdSkillToPoint.Invoke(p, new object[] { point }); }
            catch (Exception ex)
            {
                if (DBG_COMBAT && Time.time >= _dbgNextCmdErrLog)
                {
                    _dbgNextCmdErrLog = Time.time + 1.0f;
                    DbgCombat($"[COMBAT-ERR] CmdSkillToPoint exception: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        private bool CheckAmmo(WTPlayer p) { try { var rightHand = p.GetEquippedRightHand(); if (!rightHand.HasValue || rightHand.Value.data == null) return true; WTWeaponItem weapon = rightHand.Value.data as WTWeaponItem; if (weapon == null || weapon.requiredAmmo == null) return true; int count = p.InventoryCountGroup(weapon.requiredAmmo); return count > 0; } catch { return true; } }
        private bool IsValidTarget(WTMob mob) { if (mob == null) return false; if (!mob.gameObject.activeInHierarchy) return false; if (!mob.isActiveAndEnabled) return false; if (mob.health <= 0) return false; if (mob.state == "DEAD") return false; return true; }
        private string GetMobTypeKey(WTMob mob) { if (mob == null || _fiEntityType == null) return null; var et = _fiEntityType.GetValue(mob) as WTEntityType; return et != null ? et.name : null; }
        private bool IsMobType(WTMob mob, string typeKey) { string key = GetMobTypeKey(mob); return key != null && string.Equals(key, typeKey, StringComparison.OrdinalIgnoreCase); }
        private WTMob BuscarMobPorTipo(WTPlayer me, string typeKey) { WTMob best = null; float menorDist = 9999f; foreach (var mob in FindObjectsOfType<WTMob>()) { if (!IsValidTarget(mob)) continue; if (IsMobType(mob, typeKey)) { float d = Vector3.Distance(me.transform.position, mob.transform.position); if (d < menorDist && d < 80) { menorDist = d; best = mob; } } } return best; }
        private void TrySkinCorpse(WTPlayer player, WTObject corpse) { if (corpse == null) return; var gather = corpse.worldType?.gatherSettings; if (gather == null || gather.skill == null) return; if (gather.bonusRequired != null) { bool tem = false; var mao = player.GetEquippedRightHand(); if (mao.HasValue && ItemTemBonus(mao.Value, gather.bonusRequired.name)) tem = true; if (!tem && player.inventory != null) foreach (var slot in player.inventory) if (slot.amount > 0 && ItemTemBonus(slot.item, gather.bonusRequired.name)) { tem = true; break; } if (!tem) return; } WTSocketBot.PublicLogger.LogInfo($"[HUNTER] Esfolando {corpse.name}..."); player.WorldObjectTryAction(corpse, gather.skill); }


        private static string NormalizeListToken(string s)
        {
            if (s == null) return "";
            // Trim normal whitespace first
            s = s.Trim();

            // Remove common invisible characters (BOM / zero-width / bidi marks / NBSP)
            s = s.Replace("\uFEFF", "")   // BOM
                 .Replace("\u200B", "")   // zero-width space
                 .Replace("\u200E", "")   // LRM
                 .Replace("\u200F", "")   // RLM
                 .Replace("\u00A0", " "); // NBSP -> space

            s = s.Trim();

            return s;
        }

        private bool ShouldSkinDuringFishing(WTObject corpse)
        {
            if (corpse == null) return false;
            if (itensColeta == null || itensColeta.Count == 0) return false;

            // Nome do corpse geralmente vem como "CrabCorpse", etc.
            string nome = NormalizeListToken(corpse.name ?? "");
            if (string.IsNullOrWhiteSpace(nome)) return false;

            foreach (var token in itensColeta)
            {
                if (string.IsNullOrWhiteSpace(token)) continue;
                if (nome.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            return false;
        }

        private void ClearPostCombatSkinning()
        {
            _postCombatFishingState = PostCombatFishingState.None;
            _postCombatCorpse = null;
            _postCombatSkinSkill = null;
            _postCombatSkinFinishTime = 0f;
            _postCombatCorpseTimeout = 0f;
        }

        private bool TryStartPostCombatSkinning(WTPlayer player, WTObject corpse)
        {
            if (_postCombatFishingState != PostCombatFishingState.None) return true;
            if (corpse == null || !corpse.isActiveAndEnabled) return false;

            var gather = corpse.worldType?.gatherSettings;
            if (gather == null || gather.skill == null) return false;

            // Verifica se temos o bônus/ferramenta necessária para esfolar
            if (gather.bonusRequired != null)
            {
                bool tem = false;
                var mao = player.GetEquippedRightHand();
                if (mao.HasValue && ItemTemBonus(mao.Value, gather.bonusRequired.name)) tem = true;

                if (!tem && player.inventory != null)
                {
                    foreach (var slot in player.inventory)
                    {
                        if (slot.amount > 0 && ItemTemBonus(slot.item, gather.bonusRequired.name)) { tem = true; break; }
                    }
                }

                if (!tem) return false;
            }

            _postCombatCorpse = corpse;
            _postCombatSkinSkill = gather.skill;
            _postCombatFishingState = PostCombatFishingState.MoveToCorpse;
            _postCombatCorpseTimeout = Time.time + 10.0f;

            WTSocketBot.PublicLogger.LogInfo($"[PESCA] Pós-combate: tentando esfolar {corpse.name} antes de retomar a pesca...");
            return true;
        }

        /// <summary>
        /// Executa a lógica de pós-combate na pesca: ir até o corpse, esfolar e aguardar terminar.
        /// Retorna true quando concluir/abortar e liberar o retorno à âncora.
        /// </summary>
        private bool HandlePostCombatSkinning(WTPlayer player)
        {
            if (_postCombatFishingState == PostCombatFishingState.None) return true;

            if (Time.time > _postCombatCorpseTimeout)
            {
                WTSocketBot.PublicLogger.LogWarning("[PESCA] Pós-combate: timeout ao tentar esfolar. Retomando pesca.");
                ClearPostCombatSkinning();
                return true;
            }

            if (_postCombatCorpse == null || !_postCombatCorpse.isActiveAndEnabled)
            {
                ClearPostCombatSkinning();
                return true;
            }

            float dist = Vector3.Distance(player.transform.position, _postCombatCorpse.transform.position);

            if (_postCombatFishingState == PostCombatFishingState.MoveToCorpse)
            {
                if (dist > 2.8f)
                {
                    MoveToXZ(player, _postCombatCorpse.transform.position.x, _postCombatCorpse.transform.position.z);
                    return false;
                }

                SafeStopAgent(player);
                try { player.transform.LookAt(_postCombatCorpse.transform); } catch { }

                WTSocketBot.PublicLogger.LogInfo($"[HUNTER] Esfolando {_postCombatCorpse.name}...");
                player.WorldObjectTryAction(_postCombatCorpse, _postCombatSkinSkill);

                _postCombatSkinFinishTime = Time.time + 2.8f;
                _postCombatFishingState = PostCombatFishingState.Skinning;
                return false;
            }

            if (_postCombatFishingState == PostCombatFishingState.Skinning)
            {
                if (Time.time < _postCombatSkinFinishTime) return false;
                ClearPostCombatSkinning();
                return true;
            }

            ClearPostCombatSkinning();
            return true;
        }

        private bool TryGetEquippedWeaponCastRange(WTPlayer me, out float castRange, out bool isRangedWeapon)
        {
            castRange = 0f;
            isRangedWeapon = false;
            try
            {
                var rightHand = me.GetEquippedRightHand();
                if (!rightHand.HasValue || rightHand.Value.data == null) return false;


                WTWeaponItem weapon = rightHand.Value.data as WTWeaponItem;
                if (weapon == null) return false;

                // Regra do próprio jogo: arma ranged geralmente exige munição (requiredAmmo != null)
                isRangedWeapon = (weapon.requiredAmmo != null);

                if (weapon.damageSkill != null)
                {
                    castRange = weapon.damageSkill.baseCastRange;
                }

                // Fallbacks seguros caso algum item não tenha damageSkill configurado

                if (castRange <= 0f)
                {
                    castRange = isRangedWeapon ? 10f : 1.0f;

                }

                return true;
            }
            catch
            {
                return false;

            }
        }

        private bool RunCombatLogic(WTPlayer me, bool isHunting)
        {
            if (_combatTarget == null) return false;

            float dist = Vector3.Distance(me.transform.position, _combatTarget.transform.position);

            float distCollider = dist;
            try
            {
                var colDist = _combatTarget.GetComponent<Collider>();
                if (colDist != null)
                    distCollider = Vector3.Distance(me.transform.position, colDist.ClosestPoint(me.transform.position));
            }
            catch { }

            bool isFleeing = (_combatTarget.state == "RUNNING");

            // Se arma for ranged, usamos o alcance real do skill para decidir quando aproximar.
            //bool isRanged = TryGetRangedWeaponRange(me, out float rangedRange);

            // Queremos apenas garantir que estamos dentro do alcance máximo.
            // Não fazemos "kite": se o alvo encostar, continuamos atacando.
            //float engageDist = isRanged ? Mathf.Max(2.5f, rangedRange - 0.4f) : 2.0f;


            // Distância "natural" do próprio jogo, baseada no cast range do skill da arma equipada.
            bool gotWeaponRange = TryGetEquippedWeaponCastRange(me, out float weaponRange, out bool weaponIsRanged);
            bool isRanged = gotWeaponRange && weaponIsRanged;

            // Ranged já está perfeito: mantemos a folga para evitar edge cases.
            // Melee: usa o cast range real (sem hardcode).
            float engageDist;
            if (gotWeaponRange)
            {
                engageDist = weaponIsRanged ? Mathf.Max(2.5f, weaponRange - 0.4f)
                                            : Mathf.Max(1.0f, weaponRange);
            }
            else
            {

                weaponRange = 0f;
                engageDist = 1.0f;

            }

            bool validTarget = IsValidTarget(_combatTarget);
            if (DBG_COMBAT && Time.time >= _dbgNextCombatDecisionLog)
            {
                _dbgNextCombatDecisionLog = Time.time + 0.6f;
                DbgCombat($"[COMBAT] tick | hunting={isHunting} valid={validTarget} distT={dist:F2} distC={distCollider:F2} engageDist={engageDist:F2} ranged={isRanged} range={weaponRange:F2} fleeing={isFleeing} pulseWait={Mathf.Max(0f, _nextAttackPulse - Time.time):F2}s alvo={MobDbg(_combatTarget)} {AgentDbg(me)}");
            }

            if (!validTarget && DBG_COMBAT && Time.time >= _dbgNextInvalidTargetWarn)
            {
                _dbgNextInvalidTargetWarn = Time.time + 0.8f;
                bool inCombat = CheckInCombat(me);
                float dmgAgo = Time.time - _lastDamageTime;
                string meTargetName = (me.target != null ? me.target.name : "null");

                bool activeSelf = false, inHierarchy = false, enabled = false;
                try { activeSelf = _combatTarget.gameObject.activeSelf; } catch { }
                try { inHierarchy = _combatTarget.gameObject.activeInHierarchy; } catch { }
                try { enabled = _combatTarget.isActiveAndEnabled; } catch { }

                DbgCombat($"[COMBAT-WARN] alvo inválido dentro de RunCombatLogic (Update pode retornar antes de limpar) | inCombat={inCombat} dmgAgo={dmgAgo:F1}s me.target={meTargetName} mob.activeSelf={activeSelf} inHierarchy={inHierarchy} enabled={enabled} alvo={MobDbg(_combatTarget)}");
                DbgDumpAggressors(me, 8f, 4);
            }





            // ===== RETARGET INTELIGENTE (DEFESA) =====
            // Problema observado: o bot mantém _combatTarget antigo (longe) e entra em "SEGURA" enquanto toma dano de outro mob próximo,
            // ficando parado e morrendo. Aqui priorizamos o atacante próximo quando há dano recente.
            if (!isHunting)
            {
                float dmgAgoQuick = Time.time - _lastDamageTime;
                if (dmgAgoQuick <= RETARGET_DAMAGE_WINDOW && Time.time >= _nextRetargetScan)
                {
                    _nextRetargetScan = Time.time + RETARGET_SCAN_INTERVAL;

                    WTMob closeAttacker = PickNearestTargetingMe(me, RETARGET_SCAN_RADIUS);
                    if (closeAttacker != null && closeAttacker != _combatTarget && IsValidTarget(closeAttacker))
                    {
                        float closeDist = Vector3.Distance(me.transform.position, closeAttacker.transform.position);
                        float retargetThreshold = Mathf.Max(2.8f, engageDist + RETARGET_CLOSE_BONUS);

                        // Só troca se o atacante está perto o suficiente para ser uma ameaça imediata
                        // e é claramente mais próximo que o alvo atual.
                        if (closeDist <= retargetThreshold && (dist > closeDist + RETARGET_CLOSER_MARGIN))
                        {
                            if (DBG_COMBAT)
                                DbgCombat($"[COMBAT-RETARGET] dano recente e atacante próximo -> troca alvo | curDist={dist:F1} newDist={closeDist:F1} thresh={retargetThreshold:F1} cur={MobDbg(_combatTarget)} new={MobDbg(closeAttacker)}");

                            _combatTarget = closeAttacker;
                            _dbgCombatLastDist = -1f;
                            _dbgCombatLastDistTime = 0f;

                            // reavalia imediatamente com o novo alvo
                            return RunCombatLogic(me, isHunting);
                        }
                    }
                }
            }












            // DEFESA (não-caça): evita perseguir eternamente um agressor quando não estamos caçando.
            // DEFESA (não-caça): se estamos em coleta/pesca, evitamos perseguir longe,
            // mas se o alvo estiver nos atacando, precisamos FECHAR distância para retaliar.
            // (o comportamento antigo fazia "SEGURA" parado fora do alcance e podia morrer tomando dano).
            if (!isHunting && !isFleeing && dist > (engageDist + 0.2f))
            {
                float dmgAgo = Time.time - _lastDamageTime;

                bool targetMe = false;
                try { targetMe = (_combatTarget.target == me); } catch { }

                bool inCombat = false;
                try { inCombat = CheckInCombat(me); } catch { }

                // Considera "sob ataque" se: tomou dano recentemente OU o mob está com target em nós
                // OU o próprio player está flagged em combate.
                bool underAttack = (dmgAgo <= 5.0f) || targetMe || inCombat;



                if (DBG_COMBAT && Time.time >= _dbgNextCombatMoveLog)
                {
                    _dbgNextCombatMoveLog = Time.time + 0.8f;
                    DbgCombat($"[COMBAT-DEFESA] dist>{engageDist:F1} e não caçando | dist={dist:F1} dmgAgo={dmgAgo:F1}s targetMe={targetMe} inCombat={inCombat} -> {(underAttack ? "ENGAGE" : "DESISTE")} alvo={MobDbg(_combatTarget)}");
                }

                // Sem sinais de ataque: não persegue (mantém o bot focado na coleta).
                if (!underAttack) return false;

                // Leash de defesa: aproxima para conseguir bater, mas não corre "o mapa inteiro".
                // Ajuste fino: se quiser mais agressivo, aumente 2.5f -> 3.5f por exemplo.
                float maxDefenseChase = Mathf.Max(engageDist + 1.2f, 2.5f);
                if (dist > maxDefenseChase)
                {
                    // Está longe demais: segura posição (mas olhando) para não ser arrastado.
                    me.transform.LookAt(_combatTarget.transform);
                    SafeStopAgent(me);
                    return true;
                }

                // Dentro do leash: deixa o fluxo normal aproximar e atacar (NÃO retorna aqui).
            }
            // Se está fora do alcance (ou melee longe), aproxima.
            if (dist > engageDist)
            {
                Vector3 targetPos = _combatTarget.transform.position;
                if (isFleeing && _combatTarget.agent != null)
                    targetPos += _combatTarget.agent.velocity * 0.5f;

                if (DBG_COMBAT && Time.time >= _dbgNextCombatMoveLog)
                {
                    _dbgNextCombatMoveLog = Time.time + 0.7f;
                    DbgCombat($"[COMBAT-MOVE] aprox | dist={dist:F1} engageDist={engageDist:F1} alvo={MobDbg(_combatTarget)} {AgentDbg(me)}");
                }

                // Diagnóstico de "fica atacando de longe" / "travado": dist não diminui mesmo tentando mover.
                if (_dbgCombatLastDist < 0f)
                {
                    _dbgCombatLastDist = dist;
                    _dbgCombatLastDistTime = Time.time;
                }
                else
                {
                    // progresso real
                    if (dist < _dbgCombatLastDist - 0.25f)
                    {
                        _dbgCombatLastDist = dist;
                        _dbgCombatLastDistTime = Time.time;
                    }
                    else if (Time.time - _dbgCombatLastDistTime > 2.5f)
                    {
                        float vel = 0f;
                        try { if (me.agent != null) vel = me.agent.velocity.magnitude; } catch { }
                        DbgCombat($"[COMBAT-STUCK?] dist não diminui há {(Time.time - _dbgCombatLastDistTime):F1}s | dist={dist:F1} last={_dbgCombatLastDist:F1} vel={vel:F2} alvo={MobDbg(_combatTarget)} {AgentDbg(me)}");
                        _dbgCombatLastDistTime = Time.time;
                    }
                }

                MoveToXZ(me, targetPos.x, targetPos.z);
                return true;
            }

            // Chegamos ao alcance: zera tracking de "dist não diminui"
            _dbgCombatLastDist = -1f;
            _dbgCombatLastDistTime = 0f;

            SafeStopAgent(me);
            // Se estiver montado, desmonta para atacar.
            if (CheckIsMounted(me))
            {
                if (DBG_COMBAT && Time.time >= _dbgNextCombatAttackLog)
                {
                    _dbgNextCombatAttackLog = Time.time + 1.2f;
                    DbgCombat($"[COMBAT] desmontando para atacar | dist={dist:F1} alvo={MobDbg(_combatTarget)}");
                }

                if (Time.time >= _nextAttackPulse)
                {
                    ToggleMount(me);
                    _nextAttackPulse = Time.time + 0.5f;
                }
                return true;
            }
            // Seta alvo (target) no servidor.
            try
            {
                var nid = _combatTarget.netIdentity ?? _combatTarget.GetComponent<NetworkIdentity>();
                if (me.target != _combatTarget && nid != null) CmdSetTarget(me, nid);
            }
            catch { }

            // Ataca no pulso.
            if (Time.time >= _nextAttackPulse)
            {
                me.transform.LookAt(_combatTarget.transform);
                me.transform.eulerAngles = new Vector3(0, me.transform.eulerAngles.y, 0);

                Vector3 miraFinal;
                var col = _combatTarget.GetComponent<Collider>();
                if (col != null) miraFinal = col.bounds.center;
                else miraFinal = _combatTarget.transform.position + (Vector3.up * 1.2f);


                if (DBG_COMBAT && Time.time >= _dbgNextCombatAttackLog)
                {
                    _dbgNextCombatAttackLog = Time.time + 0.9f;
                    DbgCombat($"[COMBAT-ATK] skill | dist={dist:F1} engageDist={engageDist:F1} mira=({miraFinal.x:F1},{miraFinal.z:F1}) alvo={MobDbg(_combatTarget)}");
                }

                CmdSkillToPoint(me, miraFinal);
                _nextAttackPulse = Time.time + ATTACK_PULSE;
            }
            else if (DBG_COMBAT && Time.time >= _dbgNextPulseWaitLog)
            {
                _dbgNextPulseWaitLog = Time.time + 1.0f;
                DbgCombat($"[COMBAT-PULSE] aguardando pulse | wait={(_nextAttackPulse - Time.time):F2}s distT={dist:F2} distC={distCollider:F2} alvo={MobDbg(_combatTarget)}");
            }



            return true;
        }
        private WTMob PickAggressorSmart(WTPlayer me, float radius) { Vector3 myPos = me.transform.position; float bestSqr = radius * radius; WTMob best = null; int bestAggroVal = -1; int myId = me.worldId; if (me.target is WTMob tMob && IsValidTarget(tMob)) { float sqr = (tMob.transform.position - myPos).sqrMagnitude; if (sqr <= bestSqr && !ShouldIgnoreMobForTamingDefense(tMob)) return tMob; } var mobs = FindObjectsOfType<WTMob>(); foreach (var mob in mobs) { if (!IsValidTarget(mob)) continue; if (ShouldIgnoreMobForTamingDefense(mob)) continue; float sqr = (mob.transform.position - myPos).sqrMagnitude; if (sqr > bestSqr) continue; bool isTargetingMe = (mob.target == me); int currentAggro = -1; if (_aggroByIdField != null) { try { var dict = _aggroByIdField.GetValue(mob) as IDictionary; if (dict != null && dict.Contains(myId)) { object aggroObj = dict[myId]; FieldInfo valField = aggroObj.GetType().GetField("value"); if (valField != null) currentAggro = (int)valField.GetValue(aggroObj); } } catch { } } if (currentAggro > bestAggroVal) { bestAggroVal = currentAggro; best = mob; } else if (currentAggro == bestAggroVal) { if (isTargetingMe || (sqr < 9.0f && mob.entityType?.mobBehaviour?.type == WTMobBehaviour.Mode.Aggressive)) { if (best == null || sqr < (best.transform.position - myPos).sqrMagnitude) { best = mob; } } } } return best; }

        private bool ShouldIgnoreMobForTamingDefense(WTMob mob)
        {
            if (!_tamingDefenseActive || _tamingController == null || mob == null) return false;
            try
            {
                return mob == _tamingController.CurrentTarget;
            }
            catch
            {
                return false;
            }
        }

        private WTMob PickTamingDefenseTarget(WTPlayer me, WTMob excluded, float radius)
        {
            if (me == null) return null;

            Vector3 myPos = me.transform.position;
            float bestSqr = radius * radius;
            WTMob best = null;
            int bestAggroVal = -1;
            int myId = me.worldId;

            var mobs = FindObjectsOfType<WTMob>();
            foreach (var mob in mobs)
            {
                if (!IsValidTarget(mob)) continue;
                if (mob == excluded) continue;

                float sqr = (mob.transform.position - myPos).sqrMagnitude;
                if (sqr > bestSqr) continue;

                bool isTargetingMe = false;
                try { isTargetingMe = (mob.target == me); } catch { }

                int currentAggro = -1;
                if (_aggroByIdField != null)
                {
                    try
                    {
                        var dict = _aggroByIdField.GetValue(mob) as IDictionary;
                        if (dict != null && dict.Contains(myId))
                        {
                            object aggroObj = dict[myId];
                            FieldInfo valField = aggroObj.GetType().GetField("value");
                            if (valField != null) currentAggro = (int)valField.GetValue(aggroObj);
                        }
                    }
                    catch { }
                }

                bool qualifies =
                    isTargetingMe ||
                    currentAggro > 0 ||
                    (sqr < 9.0f && mob.entityType?.mobBehaviour?.type == WTMobBehaviour.Mode.Aggressive);

                if (!qualifies) continue;

                if (currentAggro > bestAggroVal)
                {
                    bestAggroVal = currentAggro;
                    best = mob;
                }
                else if (currentAggro == bestAggroVal)
                {
                    if (best == null || sqr < (best.transform.position - myPos).sqrMagnitude)
                        best = mob;
                }
            }

            return best;
        }


        // Seleciona o atacante MAIS PRÓXIMO que está com target==me (excelente para trocar quando aparece um segundo mob batendo).
        private WTMob PickNearestTargetingMe(WTPlayer me, float radius)
        {
            if (me == null) return null;
            Vector3 myPos = me.transform.position;
            float bestSqr = radius * radius;
            WTMob best = null;
            float bestDist = float.MaxValue;

            var mobs = FindObjectsOfType<WTMob>();
            foreach (var mob in mobs)
            {
                if (!IsValidTarget(mob)) continue;
                if (ShouldIgnoreMobForTamingDefense(mob)) continue;
                bool targetingMe = false;
                try { targetingMe = (mob.target == me); } catch { }
                if (!targetingMe) continue;

                float sqr = (mob.transform.position - myPos).sqrMagnitude;
                if (sqr > bestSqr) continue;
                if (sqr < bestDist) { bestDist = sqr; best = mob; }
            }
            return best;
        }






        void EnviarCombateStatus(bool emCombate)
        {
            // Evita spam: só envia se mudou.
            if (_reportouCombate == emCombate) return;
            _reportouCombate = emCombate;

            try
            {
                string msg = $"COMBAT_FLAG;{(emCombate ? "ON" : "OFF")}";
                byte[] dados = Encoding.ASCII.GetBytes(msg);
                udpSender.Send(dados, dados.Length, "127.0.0.1", _portaEnvioTelemetria);

            }
            catch { }
        }

        IEnumerator MountSequence(WTPlayer p)
        {
            _isMountingRoutineActive = true;
            if (DBG_MOUNT)
                DbgMount($"[MOUNT SEQ] start | coleta={_modoColeta} hunter={_modoHunter} mountedBefore={CheckIsMounted(p)}");

            SafeStopAgent(p);
            yield return new WaitForSeconds(0.25f);

            // Se entramos em combate enquanto a sequência aguardava, aborta para não entrar em loop de PAUSE.
            try
            {
                bool underThreat = CheckInCombat(p) || ((Time.time - _lastDamageTime) < 5.0f) || (_combatTarget != null && IsValidTarget(_combatTarget));
                if (underThreat)
                {
                    if (DBG_MOUNT)
                        DbgMount($"[MOUNT SEQ] abort | em combate/dano recente -> não toggle | dmgAgo={(Time.time - _lastDamageTime):0.0}s combat={CheckInCombat(p)} alvo={(_combatTarget != null ? MobDbg(_combatTarget) : "null")}");
                    _pauseMovementUntil = Time.time + 0.25f;
                    _isMountingRoutineActive = false;
                    yield break;
                }
            }
            catch { }


            ToggleMount(p);
            _pauseMovementUntil = Time.time + MOUNT_ANIMATION_TIME;

            if (DBG_MOUNT)
                DbgMount($"[MOUNT SEQ] toggle | mountedAfter={CheckIsMounted(p)} pause={MOUNT_ANIMATION_TIME:0.00}s");

            _isMountingRoutineActive = false;
        }
        void SafeStopAgent(WTPlayer p) { if (p.agent != null && p.agent.enabled) { if (p.agent.isOnNavMesh) p.agent.isStopped = true; p.agent.velocity = Vector3.zero; p.agent.ResetPath(); } }
        void SafeResumeAgent(WTPlayer p) { if (p.agent != null && p.agent.enabled) { if (p.agent.isOnNavMesh) p.agent.isStopped = false; } }
        bool CheckInCombat(WTPlayer p) { if (_inCombatMethod == null) return false; try { return (bool)_inCombatMethod.Invoke(p, null); } catch { return false; } }
        bool CheckIsMounted(WTPlayer p) { if (_isMountedMethod == null) return false; try { return (bool)_isMountedMethod.Invoke(p, null); } catch { return false; } }
        void ToggleMount(WTPlayer p) { if (_toggleMountMethod == null) return; try { _toggleMountMethod.Invoke(p, null); } catch { } }
        void CmdSetTarget(WTPlayer p, NetworkIdentity target)
        {
            if (_setTargetMethod == null) return;
            try { _setTargetMethod.Invoke(p, new object[] { target }); }
            catch (Exception ex)
            {
                if (DBG_COMBAT && Time.time >= _dbgNextCmdErrLog)
                {
                    _dbgNextCmdErrLog = Time.time + 1.0f;
                    DbgCombat($"[COMBAT-ERR] CmdSetTarget exception: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        void MoveToXZ(WTPlayer p, float x, float z) { _lastMoveTarget = new Vector3(x, p.transform.position.y, z); var agent = p.agent; if (agent != null && agent.enabled) { if (agent.isOnNavMesh) { agent.isStopped = false; Vector3 dest = agent.NearestValidDestination(agent.BestPlaceY(_lastMoveTarget.Value)); if (Vector3.Distance(agent.destination, dest) > 0.5f) { agent.stoppingDistance = 0f; agent.destination = dest; } } else { NavMeshHit hit; if (NavMesh.SamplePosition(p.transform.position, out hit, 10.0f, NavMesh.AllAreas)) { agent.Warp(hit.position); agent.isStopped = false; agent.destination = _lastMoveTarget.Value; } } } }

        int GetUseSkillWhenCloser(Player p)
        {
            if (_useSkillWhenCloserField == null || p == null) return -1;
            try { return (int)_useSkillWhenCloserField.GetValue(p); } catch { return -1; }
        }

        bool HarvestAutoMoveActive(WTPlayer p)
        {
            // Se o jogo está em "useSkillWhenCloser" (pipeline do clique), NÃO podemos sobrescrever agent.destination com rota.
            int use = GetUseSkillWhenCloser(p);
            if (use != -1) return true;

            // Se estamos em coleta e o player está CASTING, também não sobrescrever.
            try
            {
                if (_modoColeta && p != null && p.state == "CASTING") return true;
            }
            catch { }

            // Se temos um alvo de harvest recente, consideramos ativo até timeout.
            if (_harvestTarget != null && Time.time < _harvestTimeout) return true;
            if (_harvestFlanking) return true;
            return false;

        }



        private int ParseFoodFromTooltip(WTPlayer p, string localeKey) { try { string tt = p.TopBarToolTip(localeKey); if (string.IsNullOrEmpty(tt)) return -1; var m = _bTagNumber.Match(tt); if (!m.Success) return -1; return int.Parse(m.Groups[1].Value); } catch { return -1; } }
        private int[] GetFoodLevelsNative(WTPlayer p) { int meat = ParseFoodFromTooltip(p, "FoodToolTip.Meat"); int bread = ParseFoodFromTooltip(p, "FoodToolTip.Bread"); int mixed = ParseFoodFromTooltip(p, "FoodToolTip.Mixed"); int veg = ParseFoodFromTooltip(p, "FoodToolTip.Vegetable"); int fruit = ParseFoodFromTooltip(p, "FoodToolTip.Fruit"); if (meat < 0 || bread < 0 || mixed < 0 || veg < 0 || fruit < 0) return null; return new[] { meat, bread, mixed, veg, fruit }; }
        private float ScoreFoodCandidate(WTUsableItem usable, int[] cur) { float score = 0f; foreach (var fv in usable.foods) { int t = (int)fv.type; int val = fv.value; if (t < 0 || t >= cur.Length || val <= 0) continue; int deficit = 100 - cur[t]; int gain = Math.Min(deficit, val); int overflow = Math.Max(0, val - deficit); score += gain; if (cur[t] == 0 && val > 0) score += 15f; score -= overflow * 1.5f; } if (usable.rawFoodType) score -= 2f; return score; }
        public void TryAutoEat(WTPlayer wtPlayer)
        {
            if (Time.time < nextEatCheck) return;
            nextEatCheck = Time.time + AUTO_EAT_COOLDOWN;

            if (wtPlayer == null) return;

            if (_autoStatusFood.TryUseStatusFood(wtPlayer, _useMethod))
                return;

            if (itensComer.Count == 0 || _useMethod == null || _foodArrayField == null) return;

            int[] currentFoodLevels = _foodArrayField.GetValue(wtPlayer) as int[];
            if (currentFoodLevels == null) return;
            if (wtPlayer.inventory == null) return;

            for (int i = 0; i < wtPlayer.inventory.Count; i++)
            {
                var slot = wtPlayer.inventory[i];
                if (slot.amount <= 0 || slot.item.data == null) continue;

                bool permitido = false;
                foreach (var permitidoNome in itensComer)
                {
                    if (slot.item.data.name.IndexOf(permitidoNome, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        permitido = true;
                        break;
                    }
                }
                if (!permitido) continue;

                if (!(slot.item.data is WTUsableItem usable)) continue;
                if (usable.foods == null || usable.foods.Length == 0) continue;

                bool precisaComer = false;
                foreach (var foodVal in usable.foods)
                {
                    int typeIndex = (int)foodVal.type;
                    if (typeIndex >= 0 && typeIndex < currentFoodLevels.Length && foodVal.value > 0)
                    {
                        int nivelAtual = currentFoodLevels[typeIndex];
                        if (nivelAtual < _eatThreshold)
                        {
                            precisaComer = true;
                            break;
                        }
                    }
                }
                if (!precisaComer) continue;
                if (wtPlayer.IsFull(usable.foods, usable.rawFoodType)) continue;

                try
                {
                    _useMethod.Invoke(wtPlayer, new object[] { i });
                    return;
                }
                catch { }
            }
        }

        void ProcessarDrops(WTPlayer wtPlayer)
        {
            if (itensDropar.Count == 0 || wtPlayer.inventory == null || _dropMethod == null)
                return;

            for (int i = 0; i < wtPlayer.inventory.Count; i++)
            {
                var slot = wtPlayer.inventory[i];

                if (slot.amount > 0 && slot.item.data != null)
                {
                    string nome = (slot.item.data.name ?? "").Trim();

                    foreach (var lixo in itensDropar)
                    {
                        string nomeLixo = (lixo ?? "").Trim();

                        // comparação EXATA do nome
                        if (string.Equals(nome, nomeLixo, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                _dropMethod.Invoke(wtPlayer, new object[] { i });
                            }
                            catch { }

                            return;
                        }
                    }
                }
            }
        }
        void CheckBagFull()
        {
            try
            {
                if (Time.time < _nextBagFullAlert) return;
                var method = typeof(Entity).GetMethod("InventorySlotsFree", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                if (method != null)
                {
                    int slotsFree = (int)method.Invoke(MeuPersonagem, null);
                    if (slotsFree == 0)
                    {
                        EnviarMensagem("BAG_FULL");
                        _nextBagFullAlert = Time.time + 5.0f;
                    }
                }
            }
            catch { }
        }

        bool ShouldKeepItem(Item item) { if (item.data == null) return true; foreach (var s in itensSeguros) if (item.data.name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true; return false; }
        string MotivoParaManter(Item item) { if (item.data == null) return "Nulo"; foreach (var s in itensSeguros) if (item.data.name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return "Safe"; return null; }
        void EnviarMensagem(string msg) { try { udpSender.Send(Encoding.ASCII.GetBytes(msg), msg.Length, "127.0.0.1", _portaEnvioTelemetria); } catch { } }

        void EnviarErroHarvest(string n, string b) { if (!string.IsNullOrEmpty(b)) bonusPendentes.Add(b); EnviarMensagem($"ERRO;HARVEST;{n};{b}"); }
        void EnviarRequisitoOk(string b) { EnviarMensagem($"REQ_OK;{b}"); }
        private void TamingLog(string msg)
        {
            try { WTSocketBot.PublicLogger.LogInfo(msg); } catch { }
            try { EnviarMensagem("TAMING_LOG;" + ((msg ?? "").Replace(";", ","))); } catch { }
        }


        private void EnviarInspectReport(string requestedPlayerName, string resolvedPlayerName, string report)
        {
            try
            {
                string requested = SanitizeUdpToken(requestedPlayerName);
                string resolved = SanitizeUdpToken(resolvedPlayerName);

                EnviarMensagem($"INSPECT_BEGIN;{requested};{resolved}");

                string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(report ?? ""));
                const int chunkSize = 700;

                for (int i = 0; i < base64.Length; i += chunkSize)
                {
                    int len = Math.Min(chunkSize, base64.Length - i);
                    EnviarMensagem("INSPECT_CHUNK;" + base64.Substring(i, len));
                }

                EnviarMensagem($"INSPECT_END;{requested};{resolved}");
            }
            catch (Exception ex)
            {
                EnviarMensagem("INSPECT_ERROR;" + SanitizeUdpToken(ex.Message));
            }
        }

        private static string SanitizeUdpToken(string value)
        {
            return (value ?? "")
                .Replace(";", ",")
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
        }


        void EnviarStats() { try { int hp = MeuPersonagem.health; int sp = MeuPersonagem.stamina; Vector3 pos = MeuPersonagem.transform.position; string x = pos.x.ToString("F0", CultureInfo.InvariantCulture); string y = pos.y.ToString("F0", CultureInfo.InvariantCulture); string z = pos.z.ToString("F0", CultureInfo.InvariantCulture); byte[] dados = Encoding.ASCII.GetBytes($"STATS;{hp};{sp};{x};{y};{z}"); udpSender.Send(dados, dados.Length, "127.0.0.1", _portaEnvioTelemetria); } catch { } }
        void EnviarBag() { try { if (MeuPersonagem.inventory == null) return; List<string> b = new List<string>(); foreach (var s in MeuPersonagem.inventory) if (s.amount > 0 && !string.IsNullOrEmpty(s.item.name)) b.Add($"{s.item.name.Replace(";", "")}:{s.amount}"); if (b.Count > 0) EnviarMensagem("BAG;" + string.Join("~", b.ToArray())); } catch { } }
        void EnviarRadar()
        {
            try
            {
                Vector3 minhaPos = MeuPersonagem.transform.position;
                List<string> encontrados = new List<string>();

                foreach (var mob in FindObjectsOfType<WTMob>())
                {
                    if (mob != null && mob.health > 0)
                    {
                        float dist = Vector3.Distance(minhaPos, mob.transform.position);
                        if (dist <= 60)
                        {
                            float dx = mob.transform.position.x - minhaPos.x;
                            float dz = mob.transform.position.z - minhaPos.z;
                            string nome = mob.name.Replace("(Clone)", "").Replace(";", "").Trim();
                            string isAggro = mob.entityType?.mobBehaviour?.type == WTMobBehaviour.Mode.Aggressive ? "1" : "0";
                            encontrados.Add($"M:{nome}:{dist:F1}:{dx:F1}:{dz:F1}:{isAggro}:{mob.level}:{mob.worldId}");
                        }
                    }
                }

                foreach (var player in FindObjectsOfType<Player>())
                {
                    if (player.isLocalPlayer) continue;
                    if (player.health <= 0) continue;
                    float dist = Vector3.Distance(minhaPos, player.transform.position);
                    if (dist <= 100)
                    {
                        float dx = player.transform.position.x - minhaPos.x;
                        float dz = player.transform.position.z - minhaPos.z;
                        string nome = player.name.Replace(";", "").Trim();
                        encontrados.Add($"P:{nome}:{dist:F1}:{dx:F1}:{dz:F1}:0:{player.level}:{player.worldId}");
                    }
                }

                foreach (var drop in FindObjectsOfType<WTDroppedItem>())
                {
                    if (drop != null)
                    {
                        float dist = Vector3.Distance(minhaPos, drop.transform.position);
                        if (dist <= 40)
                        {
                            float dx = drop.transform.position.x - minhaPos.x;
                            float dz = drop.transform.position.z - minhaPos.z;
                            string nome = drop.itemSlot.item.name.Replace("(Clone)", "").Trim();
                            encontrados.Add($"D:{nome}:{dist:F1}:{dx:F1}:{dz:F1}:0:0:{SafeWorldObjectId(drop)}");
                        }
                    }
                }

                foreach (var obj in FindObjectsOfType<WTObject>())
                {
                    if (obj == null) continue;
                    if (obj.GetComponent<WTDroppedItem>() != null) continue;
                    var compMob = obj.GetComponent<WTMob>();
                    if (compMob != null && compMob.health > 0) continue;
                    if (obj.GetComponent<Player>() != null) continue;

                    float dist = Vector3.Distance(minhaPos, obj.transform.position);
                    if (dist <= 40)
                    {
                        float dx = obj.transform.position.x - minhaPos.x;
                        float dz = obj.transform.position.z - minhaPos.z;
                        string nome = obj.name.Replace("(Clone)", "").Replace(";", "").Trim();
                        nome = nome.Replace("Harvestable", "");
                        string tipo = "R";
                        bool isContainer = (obj.worldType != null && obj.worldType.containerSlots > 0);
                        bool isBuilding = (obj is WTStructure);
                        if (isContainer && !isBuilding) tipo = "C";
                        encontrados.Add($"{tipo}:{nome}:{dist:F1}:{dx:F1}:{dz:F1}:0:0:{SafeWorldObjectId(obj)}");
                    }
                }

                if (encontrados.Count > 0)
                {
                    string pacote = "RADAR;" + string.Join("~", encontrados.ToArray());
                    byte[] dados = Encoding.ASCII.GetBytes(pacote);
                    udpSender.Send(dados, dados.Length, "127.0.0.1", _portaEnvioTelemetria);
                }
            }
            catch { }
        }

        void VerificarRequisitosPendentes(WTPlayer wtPlayer) { if (bonusPendentes.Count == 0 || wtPlayer.inventory == null) return; List<string> atendidos = new List<string>(); foreach (var bonus in bonusPendentes) { bool tem = false; var mao = wtPlayer.GetEquippedRightHand(); if (mao.HasValue && ItemTemBonus(mao.Value, bonus)) tem = true; if (!tem) { foreach (var slot in wtPlayer.inventory) { if (slot.amount > 0 && ItemTemBonus(slot.item, bonus)) { tem = true; break; } } } if (tem) { atendidos.Add(bonus); EnviarRequisitoOk(bonus); } } foreach (var ok in atendidos) bonusPendentes.Remove(ok); }

        private bool TryTriggerHarvestAction(WTPlayer wtPlayer, WTObject target, string stateForTelemetry = null, string debugReason = null)
        {
            if (wtPlayer == null || target == null) return false;

            var gather = target.worldType?.gatherSettings;
            if (gather == null || gather.skill == null) return false;
            if (Time.time < _nextHarvestTry) return false;

            if (!string.IsNullOrWhiteSpace(debugReason))
                WTSocketBot.PublicLogger.LogInfo($"[HARVEST-ACTION] {debugReason} alvo={SafeName(target)} wid={SafeWorldObjectId(target)}");

            if (!string.IsNullOrWhiteSpace(stateForTelemetry))
                NotifyHarvestState(stateForTelemetry, target);

            try { wtPlayer.WorldObjectTryAction(target, gather.skill); }
            catch { return false; }

            _harvestLastActionTime = Time.time;
            _nextHarvestTry = Time.time + 0.60f;
            _harvestNextWatchTick = Time.time + 0.25f;
            return true;
        }

        void TryHarvestObjectNative(WTPlayer wtPlayer, string partialName, int preferredWorldId = 0)
        {
            if (wtPlayer == null) return;

            _harvestPartialName = partialName;
            CleanupHarvestBlacklist();

            if (IsHarvestActive() && _harvestTarget != null)
            {
                if (_activeHarvestWorldId != 0 && preferredWorldId != 0 && _activeHarvestWorldId == preferredWorldId) return;
                return;
            }

            WTObject alvoExato = null;
            WTObject alvoFallback = null;
            float menorDistFallback = 999f;

            foreach (var obj in FindObjectsOfType<WTObject>())
            {
                if (obj == null || !obj.isActiveAndEnabled) continue;
                if (obj.worldType == null || obj.worldType.gatherSettings == null) continue;

                int objectId = SafeWorldObjectId(obj);
                if (IsHarvestBlacklisted(objectId)) continue;

                bool nameMatches = obj.name.IndexOf(partialName, StringComparison.OrdinalIgnoreCase) >= 0;
                bool idMatches = preferredWorldId > 0 && objectId == preferredWorldId;
                if (!nameMatches && !idMatches) continue;

                float dist = Vector3.Distance(wtPlayer.transform.position, obj.transform.position);
                try
                {
                    if (wtPlayer.collider != null && obj.collider != null)
                        dist = Utils.ClosestDistance(wtPlayer.collider, obj.collider);
                }
                catch { }

                if (dist >= 50f) continue;

                if (idMatches)
                {
                    alvoExato = obj;
                    break;
                }

                if (nameMatches && dist < menorDistFallback)
                {
                    menorDistFallback = dist;
                    alvoFallback = obj;
                }
            }

            WTObject alvo = alvoExato ?? alvoFallback;
            if (alvo == null)
            {
                try { EnviarMensagem($"HARVEST_DONE;Failed;TargetUnavailable;{GetHarvestTelemetryName(null)};{preferredWorldId}"); } catch { }
                ClearHarvestState();
                return;
            }

            var gather = alvo.worldType?.gatherSettings;
            if (gather == null || gather.skill == null) return;
            if (gather.bonusRequired != null)
            {
                bool tem = false;
                var mao = wtPlayer.GetEquippedRightHand();
                if (mao.HasValue && ItemTemBonus(mao.Value, gather.bonusRequired.name)) tem = true;
                if (!tem && wtPlayer.inventory != null)
                    foreach (var slot in wtPlayer.inventory)
                        if (slot.amount > 0 && ItemTemBonus(slot.item, gather.bonusRequired.name)) { tem = true; break; }
                if (!tem) { EnviarErroHarvest(partialName, gather.bonusRequired.name); return; }
            }

            _harvestTarget = alvo;
            _activeHarvestWorldId = SafeWorldObjectId(alvo);
            _harvestPartialName = partialName;
            _harvestTimeout = Time.time + HARVEST_TOTAL_TIMEOUT;
            _harvestStartTime = Time.time;
            _harvestStartedMounted = CheckIsMounted(wtPlayer);
            _harvestRetriedAfterDismount = false;
            _harvestNearTargetRetryCount = 0;
            _harvestLostInteractionSince = 0f;
            _harvestLastActionTime = 0f;
            _harvestArmedStallSince = 0f;
            _harvestArmedStallRetryCount = 0;
            _harvestMicroAdjusting = false;

            _harvestFlanking = false;
            _harvestFlankAttempts = 0;
            _harvestBestDistC = 9999f;
            _harvestLastProgressTime = Time.time;
            _harvestNextWatchTick = Time.time + 0.25f;
            _lastMoveTarget = null;
            _nextHarvestTry = 0f;

            TryTriggerHarvestAction(wtPlayer, alvo, "Approaching", _harvestStartedMounted ? "start-mounted" : "start");
        }

        void HarvestWatchdog(WTPlayer wtPlayer)
        {
            if (wtPlayer == null) return;
            if (_harvestTarget == null)
            {
                CleanupStaleHarvestStateIfNeeded("watchdog");
                return;
            }
            if (Time.time > _harvestTimeout)
            {
                WTSocketBot.PublicLogger.LogInfo($"[HARVEST-ABORT] timeout >{HARVEST_TOTAL_TIMEOUT:0.0}s alvo={SafeName(_harvestTarget)}");
                NotifyHarvestDone(false, "GlobalTimeout", _harvestTarget);
                BlacklistAndClearHarvest(_harvestTarget);
                return;
            }

            if (Time.time < _harvestNextWatchTick) return;
            _harvestNextWatchTick = Time.time + HARVEST_WATCH_TICK;

            // Calcula distC real
            float distC = 9999f;
            try
            {
                if (wtPlayer.collider != null && _harvestTarget.collider != null)
                    distC = Utils.ClosestDistance(wtPlayer.collider, _harvestTarget.collider);
                else
                    distC = Vector3.Distance(wtPlayer.transform.position, _harvestTarget.transform.position);
            }
            catch { }

            // Atualiza progresso
            if (distC < (_harvestBestDistC - HARVEST_PROGRESS_MIN_DELTA_C))
            {
                _harvestBestDistC = distC;
                _harvestLastProgressTime = Time.time;
            }
            else if (_harvestBestDistC > 9000f)
            {
                _harvestBestDistC = distC;
                _harvestLastProgressTime = Time.time;
            }

            // Se estamos flanqueando, checa chegada e re-dispara a ação
            if (_harvestFlanking)
            {
                float d = Vector3.Distance(wtPlayer.transform.position, _harvestFlankPoint);
                float rem = 999f;
                try { if (wtPlayer.agent != null) rem = wtPlayer.agent.remainingDistance; } catch { }
                if (d < HARVEST_ARRIVE_DIST || rem < 0.60f)
                {
                    bool wasMicroAdjust = _harvestMicroAdjusting;
                    _harvestFlanking = false;
                    _harvestMicroAdjusting = false;
                    _nextHarvestTry = 0f;

                    if (TryTriggerHarvestAction(wtPlayer, _harvestTarget, "Interacting", wasMicroAdjust ? $"micro-adjust-arrive d={d:0.00}" : $"flank-arrive d={d:0.00}"))
                    {
                        _harvestNearTargetRetryCount = 0;
                        _harvestLostInteractionSince = 0f;
                        _harvestArmedStallSince = 0f;
                    }
                    return;
                }
            }

            float remDist = 999f;
            bool pathBad = false;
            bool slow = false;
            bool reachedCorner = false;
            try
            {
                if (wtPlayer.agent != null)
                {
                    remDist = wtPlayer.agent.remainingDistance;
                    pathBad = (wtPlayer.agent.pathStatus != NavMeshPathStatus.PathComplete);
                    slow = (wtPlayer.agent.velocity.magnitude < 0.08f);
                    reachedCorner = (wtPlayer.agent.remainingDistance < 0.35f);
                }
            }
            catch { }

            int use = GetUseSkillWhenCloser(wtPlayer);
            bool mounted = CheckIsMounted(wtPlayer);
            bool isCasting = false;
            try { isCasting = (wtPlayer.state == "CASTING"); } catch { }

            // Debug leve (1 linha por tick do watchdog)
            WTSocketBot.PublicLogger.LogInfo($"[HARVEST-WATCH] alvo={SafeName(_harvestTarget)} use={use} distC={distC:0.00} best={_harvestBestDistC:0.00} noProg={(Time.time - _harvestLastProgressTime):0.0}s pathBad={pathBad} slow={slow} rem={remDist:0.00}");

            bool armedNearTargetStall = use != -1 && !isCasting && !_harvestFlanking && distC <= HARVEST_MIN_DISTC_FOR_STUCK && remDist <= HARVEST_ARMED_STALL_REMDIST_MAX && slow && (Time.time - _harvestLastActionTime) >= 0.20f;

            if (armedNearTargetStall)
            {
                if (_harvestArmedStallSince <= 0f) _harvestArmedStallSince = Time.time;
                float armedFor = Time.time - _harvestArmedStallSince;

                if (armedFor >= HARVEST_ARMED_STALL_RETRY_TIME && _harvestArmedStallRetryCount < HARVEST_MAX_NEAR_TARGET_RETRIES)
                {
                    _harvestArmedStallRetryCount++;
                    if (TryStartHarvestMicroAdjust(wtPlayer, _harvestTarget, _harvestArmedStallRetryCount, use, distC, remDist, armedFor))
                    {
                        _harvestLastProgressTime = Time.time;
                        _harvestArmedStallSince = Time.time;
                        _harvestLostInteractionSince = 0f;
                        return;
                    }
                }

                if (armedFor >= HARVEST_ARMED_STALL_ABORT_TIME)
                {
                    WTSocketBot.PublicLogger.LogInfo($"[HARVEST-ABORT] armed near target stalled use={use} distC={distC:0.00} rem={remDist:0.00} stall={armedFor:0.0}s alvo={SafeName(_harvestTarget)}");
                    NotifyHarvestDone(false, "ArmedNearTargetStall", _harvestTarget);
                    BlacklistAndClearHarvest(_harvestTarget);
                    return;
                }
            }
            else
            {
                _harvestArmedStallSince = 0f;
            }

            bool lostInteractionNearTarget = use == -1 && !isCasting && distC <= HARVEST_MIN_DISTC_FOR_STUCK && remDist <= (HARVEST_ARRIVE_DIST + 0.40f) && (Time.time - _harvestLastActionTime) >= 0.20f;

            if (lostInteractionNearTarget)
            {
                if (_harvestLostInteractionSince <= 0f) _harvestLostInteractionSince = Time.time;
                float lostFor = Time.time - _harvestLostInteractionSince;

                if (_harvestStartedMounted && !_harvestRetriedAfterDismount && !mounted && lostFor >= HARVEST_POST_DISMOUNT_RETRY_DELAY)
                {
                    if (TryTriggerHarvestAction(wtPlayer, _harvestTarget, "Interacting", $"retry-after-dismount lost={lostFor:0.00}s"))
                    {
                        _harvestRetriedAfterDismount = true;
                        _harvestLastProgressTime = Time.time;
                        return;
                    }
                }

                if (lostFor >= HARVEST_NEAR_TARGET_STALL_RETRY_TIME && _harvestNearTargetRetryCount < HARVEST_MAX_NEAR_TARGET_RETRIES)
                {
                    _harvestNearTargetRetryCount++;

                    try { wtPlayer.ClearUseWhenCloserSkill(); } catch { }
                    try
                    {
                        if (wtPlayer.agent != null && wtPlayer.agent.enabled)
                        {
                            wtPlayer.agent.ResetPath();
                            wtPlayer.agent.isStopped = false;
                        }
                    }
                    catch { }

                    if (TryTriggerHarvestAction(wtPlayer, _harvestTarget, "Recovering", $"near-target-retry {_harvestNearTargetRetryCount}/{HARVEST_MAX_NEAR_TARGET_RETRIES} lost={lostFor:0.0}s"))
                    {
                        _harvestLastProgressTime = Time.time;
                        _harvestLostInteractionSince = Time.time;
                        return;
                    }
                }

                if (lostFor >= HARVEST_NEAR_TARGET_ABORT_TIME)
                {
                    WTSocketBot.PublicLogger.LogInfo($"[HARVEST-ABORT] lost interaction near target lost={lostFor:0.0}s alvo={SafeName(_harvestTarget)}");
                    NotifyHarvestDone(false, "LostInteractionNearTarget", _harvestTarget);
                    BlacklistAndClearHarvest(_harvestTarget);
                    return;
                }
            }
            else
            {
                _harvestLostInteractionSince = 0f;
            }

            // Heurística de stuck (progresso + estado do agent)
            float noProgFor = Time.time - _harvestLastProgressTime;

            if (distC <= HARVEST_MIN_DISTC_FOR_STUCK) return;
            if (noProgFor <= HARVEST_NO_PROGRESS_TIME) return;
            if (!(pathBad || slow || (reachedCorner && distC > HARVEST_MIN_DISTC_FOR_STUCK))) return;

            // Dispara flank
            if (_harvestFlankAttempts >= HARVEST_MAX_FLANK_ATTEMPTS)
            {
                WTSocketBot.PublicLogger.LogInfo($"[HARVEST-ABORT] max flank attempts ({HARVEST_MAX_FLANK_ATTEMPTS}) alvo={SafeName(_harvestTarget)}");
                NotifyHarvestDone(false, "MaxFlankAttempts", _harvestTarget);
                BlacklistAndClearHarvest(_harvestTarget);
                return;
            }

            _harvestFlankAttempts++;
            WTSocketBot.PublicLogger.LogInfo($"[HARVEST-STUCK] noProg={noProgFor:0.0}s distC={distC:0.00} pathBad={pathBad} slow={slow} -> START_FLANK attempt={_harvestFlankAttempts}/{HARVEST_MAX_FLANK_ATTEMPTS} alvo={SafeName(_harvestTarget)}");
            NotifyHarvestState("Recovering", _harvestTarget);
            StartHarvestFlank(wtPlayer, _harvestTarget);
        }

        void StartHarvestFlank(WTPlayer wtPlayer, WTObject target)
        {
            if (wtPlayer == null || target == null) return;

            // IMPORTANTÍSSIMO: limpa useSkillWhenCloser antes de mover para outro lado
            try { wtPlayer.ClearUseWhenCloserSkill(); } catch { }

            Collider col = null;
            try { col = target.collider != null ? target.collider : target.GetComponent<Collider>(); } catch { }
            if (col == null)
            {
                WTSocketBot.PublicLogger.LogInfo($"[HARVEST-FLANK] sem collider no alvo -> não dá pra flanquear alvo={SafeName(target)}");
                return;
            }

            if (!TryPickFlankPoint(wtPlayer, col, out Vector3 flankPoint, out string pickLog))
            {
                WTSocketBot.PublicLogger.LogInfo($"[HARVEST-FLANK] nenhum ponto bom (PathComplete) encontrado. {pickLog}");
                return;
            }

            _harvestFlankPoint = flankPoint;
            _harvestFlanking = true;
            _harvestMicroAdjusting = false;
            _harvestLastProgressTime = Time.time; // reseta janela de stuck
            _harvestBestDistC = 9999f;
            _harvestLostInteractionSince = 0f;
            _harvestNearTargetRetryCount = 0;

            WTSocketBot.PublicLogger.LogInfo($"[HARVEST-FLANK-CHOSEN] {pickLog}");
            MoveAgentToPoint(wtPlayer, flankPoint, 0.25f);
        }

        private bool TryStartHarvestMicroAdjust(WTPlayer wtPlayer, WTObject target, int retryNo, int useSkill, float distC, float remDist, float armedFor)
        {
            if (wtPlayer == null || target == null) return false;

            try { wtPlayer.ClearUseWhenCloserSkill(); } catch { }
            try
            {
                if (wtPlayer.agent != null && wtPlayer.agent.enabled)
                {
                    wtPlayer.agent.ResetPath();
                    wtPlayer.agent.isStopped = false;
                }
            }
            catch { }

            Vector3 adjustPoint;
            string adjustLog;
            if (TryPickHarvestMicroAdjustPoint(wtPlayer, target, retryNo, out adjustPoint, out adjustLog))
            {
                _harvestFlanking = true;
                _harvestMicroAdjusting = true;
                _harvestFlankPoint = adjustPoint;
                _nextHarvestTry = 0f;
                NotifyHarvestState("Recovering", target);
                WTSocketBot.PublicLogger.LogInfo($"[HARVEST-STALL] armed-near-target use={useSkill} distC={distC:0.00} rem={remDist:0.00} stall={armedFor:0.0}s retry={retryNo}/{HARVEST_MAX_NEAR_TARGET_RETRIES} -> MICRO-ADJUST {adjustLog}");
                MoveAgentToPoint(wtPlayer, adjustPoint, HARVEST_MICROADJUST_STOPPING_DISTANCE);
                return true;
            }

            if (TryTriggerHarvestAction(wtPlayer, target, "Recovering", $"armed-near-target-direct-retry {retryNo}/{HARVEST_MAX_NEAR_TARGET_RETRIES} use={useSkill} stall={armedFor:0.0}s"))
            {
                WTSocketBot.PublicLogger.LogInfo($"[HARVEST-STALL] armed-near-target fallback-direct-retry use={useSkill} distC={distC:0.00} rem={remDist:0.00} stall={armedFor:0.0}s retry={retryNo}/{HARVEST_MAX_NEAR_TARGET_RETRIES}");
                return true;
            }

            return false;
        }

        private bool TryPickHarvestMicroAdjustPoint(WTPlayer wtPlayer, WTObject target, int retryNo, out Vector3 bestPoint, out string log)
        {
            bestPoint = Vector3.zero;
            log = "no candidates";
            if (wtPlayer == null || target == null || target.collider == null) return false;

            Vector3 from = wtPlayer.transform.position;
            Vector3 center = target.collider.bounds.center;
            Vector3 toCenter = center - from;
            toCenter.y = 0f;
            if (toCenter.sqrMagnitude < 0.0001f) toCenter = (target.transform.position - from);
            toCenter.y = 0f;
            if (toCenter.sqrMagnitude < 0.0001f) toCenter = Vector3.forward;
            toCenter.Normalize();

            Vector3 perp = new Vector3(-toCenter.z, 0f, toCenter.x);
            int primarySide = (retryNo % 2 == 1) ? 1 : -1;
            float forwardStep = 0.20f;
            float sideStep = 0.38f;
            float[] sideVariants = new float[] { primarySide * sideStep, -primarySide * sideStep, 0f };
            NavMeshPath path = new NavMeshPath();

            foreach (float side in sideVariants)
            {
                Vector3 raw = target.collider.ClosestPoint(from) + (perp * side) + (toCenter * forwardStep);
                raw.y = from.y;
                if (!NavMesh.SamplePosition(raw, out NavMeshHit hit, 1.25f, NavMesh.AllAreas))
                    continue;
                if (!NavMesh.CalculatePath(from, hit.position, NavMesh.AllAreas, path))
                    continue;
                if (path == null || path.status != NavMeshPathStatus.PathComplete || path.corners == null || path.corners.Length < 2)
                    continue;

                bestPoint = hit.position;
                log = $"p=({bestPoint.x:0.00},{bestPoint.z:0.00}) side={side:0.00}";
                return true;
            }

            return false;
        }

        bool TryPickFlankPoint(WTPlayer wtPlayer, Collider col, out Vector3 bestPoint, out string log)
        {
            bestPoint = Vector3.zero;
            log = "";
            if (wtPlayer == null || col == null) return false;

            Vector3 center = col.bounds.center;
            Vector3 ext = col.bounds.extents;
            float baseR = Mathf.Clamp(Mathf.Max(ext.x, ext.z) + 1.25f, 1.8f, 4.0f);
            float[] radii = new float[] { baseR, baseR + 1.0f };
            int angles = 12;

            float bestScore = 999999f;
            int bestAngle = -1;
            float bestR = 0f;
            NavMeshPath path = new NavMeshPath();

            int logged = 0;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            Vector3 from = wtPlayer.transform.position;

            for (int ring = 0; ring < radii.Length; ring++)
            {
                float r = radii[ring];
                for (int i = 0; i < angles; i++)
                {
                    float ang = (360f / angles) * i;
                    float rad = ang * Mathf.Deg2Rad;
                    Vector3 raw = new Vector3(center.x + Mathf.Cos(rad) * r, from.y, center.z + Mathf.Sin(rad) * r);

                    if (!NavMesh.SamplePosition(raw, out NavMeshHit hit, 1.2f, NavMesh.AllAreas)) continue;

                    if (!NavMesh.CalculatePath(from, hit.position, NavMesh.AllAreas, path)) continue;
                    if (path == null || path.corners == null || path.corners.Length < 2) continue;

                    float len = 0f;
                    for (int c = 1; c < path.corners.Length; c++) len += Vector3.Distance(path.corners[c - 1], path.corners[c]);

                    float score = len;
                    if (path.status == NavMeshPathStatus.PathPartial) score += 50f;
                    if (path.status == NavMeshPathStatus.PathInvalid) continue;

                    if (logged < 6)
                    {
                        sb.Append($"cand a={ang:0} r={r:0.0} st={path.status} len={len:0.0}; ");
                        logged++;
                    }

                    // Preferir PathComplete fortemente
                    if (path.status != NavMeshPathStatus.PathComplete) continue;

                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestPoint = hit.position;
                        bestAngle = (int)ang;
                        bestR = r;
                    }
                }
            }

            if (bestAngle < 0)
            {
                log = sb.Length > 0 ? sb.ToString() : "no candidates";
                return false;
            }

            log = $"best a={bestAngle} r={bestR:0.0} score={bestScore:0.0} p=({bestPoint.x:0.0},{bestPoint.z:0.0}) | {sb}";
            return true;
        }

        void MoveAgentToPoint(WTPlayer p, Vector3 point, float stoppingDistance)
        {
            try
            {
                var agent = p.agent;
                if (agent == null || !agent.enabled) return;

                agent.isStopped = false;
                agent.stoppingDistance = stoppingDistance;

                // Usa o mesmo ajuste do jogo para Y  navmesh mais próximo
                Vector3 dest = point;
                try { dest = agent.NearestValidDestination(agent.BestPlaceY(point)); } catch { }

                if (!agent.isOnNavMesh)
                {
                    if (NavMesh.SamplePosition(p.transform.position, out NavMeshHit hit, 10.0f, NavMesh.AllAreas))
                        agent.Warp(hit.position);
                }

                agent.destination = dest;
            }
            catch { }
        }
        void BlacklistAndClearHarvest(WTObject target)
        {
            try
            {
                if (target != null)
                {
                    int id = SafeWorldObjectId(target);
                    if (id != 0)
                        _harvestBlacklistUntil[id] = Time.time + HARVEST_BLACKLIST_TIME;
                }
            }
            catch { }

            ClearHarvestState();
        }

        string SafeName(UnityEngine.Object o)
        {
            try { return o != null ? o.name : "null"; } catch { return "null"; }
        }

        private void CleanupStaleHarvestStateIfNeeded(string origin = null)
        {
            if (_activeHarvestWorldId == 0) return;

            bool queued = !actionQueue.IsEmpty;
            bool hasLiveContext = _harvestTarget != null || _harvestFlanking || queued;
            bool timedOut = _harvestTimeout > 0f && Time.time > (_harvestTimeout + 0.50f);
            bool staleLock = !hasLiveContext && (_harvestLastActionTime <= 0f || (Time.time - _harvestLastActionTime) >= 0.35f);

            if (!timedOut && !staleLock) return;

            try
            {
                string why = timedOut ? "timeout" : "target-null";
                WTSocketBot.PublicLogger.LogInfo($"[HARVEST-CLEANUP] limpando trava órfã origem={origin ?? "?"} wid={_activeHarvestWorldId} motivo={why} alvo={SafeName(_harvestTarget)} flanking={_harvestFlanking} queued={queued}");
            }
            catch { }

            ClearHarvestState();
        }

        private bool HasHarvestInFlightUnsafe()
        {
            CleanupStaleHarvestStateIfNeeded("command");
            if (_harvestTarget != null || _harvestFlanking || !actionQueue.IsEmpty) return true;
            return _activeHarvestWorldId != 0 && _harvestTimeout > 0f && Time.time < _harvestTimeout && (Time.time - _harvestLastActionTime) < 1.25f;
        }

        private bool IsHarvestActive()
        {
            CleanupStaleHarvestStateIfNeeded("active-check");
            return _harvestTarget != null || _harvestFlanking || (_activeHarvestWorldId != 0 && Time.time < _harvestTimeout);
        }

        private int SafeWorldObjectId(WTObject target)
        {
            try
            {
                if (target != null && target.worldId != 0) return target.worldId;
            }
            catch { }

            try { return target != null ? target.GetInstanceID() : 0; } catch { return 0; }
        }

        private bool IsHarvestBlacklisted(int objectId)
        {
            if (objectId == 0) return false;
            if (_harvestBlacklistUntil.TryGetValue(objectId, out float until) && Time.time < until) return true;
            return false;
        }

        private void CleanupHarvestBlacklist()
        {
            if (_harvestBlacklistUntil.Count == 0) return;
            var expired = _harvestBlacklistUntil.Where(kv => kv.Value <= Time.time).Select(kv => kv.Key).ToList();
            foreach (var key in expired) _harvestBlacklistUntil.Remove(key);
        }

        private void ClearHarvestState()
        {
            _harvestTarget = null;
            _activeHarvestWorldId = 0;
            _harvestTimeout = 0f;
            _nextHarvestTry = 0f;
            _harvestPartialName = "";
            _harvestFlanking = false;
            _harvestStartTime = 0f;
            _harvestBestDistC = 9999f;
            _harvestLastProgressTime = 0f;
            _harvestFlankAttempts = 0;
            _harvestStartedMounted = false;
            _harvestRetriedAfterDismount = false;
            _harvestNearTargetRetryCount = 0;
            _harvestLostInteractionSince = 0f;
            _harvestLastActionTime = 0f;
            _harvestArmedStallSince = 0f;
            _harvestArmedStallRetryCount = 0;
            _harvestMicroAdjusting = false;
            _modoColeta = false;
        }

        private string GetHarvestTelemetryName(WTObject target)
        {
            string name = !string.IsNullOrWhiteSpace(_harvestPartialName) ? _harvestPartialName : SafeName(target);
            return (name ?? "").Replace(";", "").Trim();
        }

        private void NotifyHarvestState(string state, WTObject target)
        {
            try
            {
                EnviarMensagem($"HARVEST_STATE;{state};{GetHarvestTelemetryName(target)};{SafeWorldObjectId(target)}");
            }
            catch { }
        }

        private void NotifyHarvestDone(bool success, string reason, WTObject target)
        {
            try
            {
                string status = success ? "Success" : "Failed";
                string safeReason = string.IsNullOrWhiteSpace(reason) ? "Unknown" : reason.Replace(";", "").Trim();
                EnviarMensagem($"HARVEST_DONE;{status};{safeReason};{GetHarvestTelemetryName(target)};{SafeWorldObjectId(target)}");
            }
            catch { }
        }

        // Chamado via HarvestHooks ao receber TargetGatherResult (sucesso ou falha)
        public void OnGatherResult(WTPlayer p, JobResult res)
        {
            WTObject target = _harvestTarget;
            try
            {
                if (target == null) return;
                string alvoName = "?";
                try { alvoName = target != null ? target.name : "?"; } catch { }
                WTSocketBot.PublicLogger.LogInfo($"[HARVEST-RESULT] alvo={alvoName} success={res.success} toolFail={res.tool} dropped={res.dropped}");
            }
            catch { }

            try
            {
                if (res.success && target != null)
                {
                    int id = SafeWorldObjectId(target);
                    if (id != 0)
                        _harvestBlacklistUntil[id] = Time.time + HARVEST_SUCCESS_COOLDOWN_TIME;
                }
            }
            catch { }

            NotifyHarvestDone(res.success, res.success ? "GatherResult" : (res.tool ? "ToolFail" : "ResultFail"), target);
            ClearHarvestState();
        }



        bool ItemTemBonus(Item item, string bonusName) { if (item.data is WTEquipmentItem eq && eq.bonuses != null) { if (eq.maxDurability > 0 && item.GetDurability() <= 0) return false; foreach (var b in eq.bonuses) if (b.bonusType?.name == bonusName) return true; } return false; }
        private bool CheckStamina(WTPlayer wtPlayer) { if (wtPlayer.stamina < 20) { if (!_resting) { _resting = true; try { wtPlayer.CmdCancelAction(); } catch { } SafeStopAgent(wtPlayer); } return false; } if (_resting) { if (wtPlayer.stamina < 90) { SafeStopAgent(wtPlayer); return false; } _resting = false; SafeResumeAgent(wtPlayer); } return true; }
    }

    internal sealed class AutoStatusFoodController
    {
        private readonly List<string> _configuredItems = new List<string>();
        private const float STATUS_REFRESH_SECONDS = 2.0f;

        public void ReplaceConfiguredItems(IEnumerable<string> itemNames)
        {
            _configuredItems.Clear();
            if (itemNames == null) return;

            foreach (string raw in itemNames)
            {
                string name = (raw ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (_configuredItems.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) continue;
                _configuredItems.Add(name);
            }

            try { WTSocketBot.PublicLogger.LogInfo(string.Format(WildTerraBot.Properties.Resources.UdpRunnerAutoEatStatusConfiguredFormat, string.Join(", ", _configuredItems))); } catch { }
        }

        public bool TryUseStatusFood(WTPlayer me, MethodInfo useMethod)
        {
            if (me == null || useMethod == null) return false;
            if (_configuredItems.Count == 0) return false;
            if (me.inventory == null) return false;

            foreach (string itemName in _configuredItems)
            {
                int inventoryIndex;
                WTUsableItem usable;
                if (!TryFindUsableInventoryItem(me, itemName, out inventoryIndex, out usable))
                {
                    try { WTSocketBot.PublicLogger.LogInfo(string.Format(WildTerraBot.Properties.Resources.UdpRunnerAutoEatStatusItemNotFoundFormat, itemName)); } catch { }
                    continue;
                }

                if (usable == null || usable.useEffects == null || usable.useEffects.Length == 0)
                {
                    try { WTSocketBot.PublicLogger.LogInfo(string.Format(WildTerraBot.Properties.Resources.UdpRunnerAutoEatStatusNoMonitorableUseEffectsFormat, itemName)); } catch { }
                    continue;
                }

                string reason;
                if (!NeedsRefresh(me, usable, STATUS_REFRESH_SECONDS, out reason))
                {
                    try { WTSocketBot.PublicLogger.LogInfo(string.Format(WildTerraBot.Properties.Resources.UdpRunnerAutoEatStatusKeepingFormat, usable.name, reason)); } catch { }
                    continue;
                }

                try
                {
                    useMethod.Invoke(me, new object[] { inventoryIndex });
                    try { WTSocketBot.PublicLogger.LogInfo(string.Format(WildTerraBot.Properties.Resources.UdpRunnerAutoEatStatusUsingFormat, usable.name, inventoryIndex, reason)); } catch { }
                    return true;
                }
                catch (Exception ex)
                {
                    try { WTSocketBot.PublicLogger.LogInfo(string.Format(WildTerraBot.Properties.Resources.UdpRunnerAutoEatStatusUseFailedFormat, usable.name, inventoryIndex, ex.Message)); } catch { }
                }
            }

            return false;
        }

        private static bool NeedsRefresh(WTPlayer me, WTUsableItem usable, float refreshSeconds, out string reason)
        {
            reason = WildTerraBot.Properties.Resources.UdpRunnerAutoEatStatusNoMonitorableEffects;
            if (me == null || usable == null || usable.useEffects == null || usable.useEffects.Length == 0)
                return false;

            bool hasAnyMonitoredEffect = false;
            bool foundActiveEffect = false;
            float bestRemaining = float.MaxValue;
            string bestEffectName = null;

            for (int i = 0; i < usable.useEffects.Length; i++)
            {
                TimedEffect timed = usable.useEffects[i];
                if (timed.effect == null) continue;
                hasAnyMonitoredEffect = true;

                float remaining = GetBestEffectRemainingByName(me, timed.effect);
                string effectName = timed.effect.name ?? "<sem-nome>";
                if (remaining > 0f)
                {
                    foundActiveEffect = true;
                    if (remaining < bestRemaining)
                    {
                        bestRemaining = remaining;
                        bestEffectName = effectName;
                    }
                }
                else
                {
                    reason = string.Format(WildTerraBot.Properties.Resources.UdpRunnerAutoEatStatusEffectMissingFormat, effectName);
                    return true;
                }

                if (remaining <= refreshSeconds)
                {
                    reason = string.Format(WildTerraBot.Properties.Resources.UdpRunnerAutoEatStatusEffectEndingFormat, effectName, remaining, refreshSeconds);
                    return true;
                }
            }

            if (!hasAnyMonitoredEffect)
            {
                reason = WildTerraBot.Properties.Resources.UdpRunnerAutoEatStatusItemNoMonitorableEffects;
                return false;
            }

            if (!foundActiveEffect)
            {
                reason = WildTerraBot.Properties.Resources.UdpRunnerAutoEatStatusNoActiveEffectByName;
                return true;
            }

            reason = string.Format(WildTerraBot.Properties.Resources.UdpRunnerAutoEatStatusActiveBuffRemainingFormat, bestEffectName ?? "?", bestRemaining);
            return false;
        }

        private static float GetBestEffectRemainingByName(WTPlayer me, WTScriptableEffect effectType)
        {
            float best = 0f;
            if (me == null || effectType == null || me.effects == null) return best;

            try
            {
                for (int i = 0; i < me.effects.Count; i++)
                {
                    EntityEffect active = me.effects[i];
                    if (!string.Equals(active.name, effectType.name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    float remaining = active.TimeRemaining();
                    if (remaining > best) best = remaining;
                }
            }
            catch { }

            return best;
        }

        private static bool TryFindUsableInventoryItem(WTPlayer me, string wantedName, out int inventoryIndex, out WTUsableItem usable)
        {
            inventoryIndex = -1;
            usable = null;
            if (me == null || me.inventory == null || string.IsNullOrWhiteSpace(wantedName)) return false;

            string wanted = wantedName.Trim();
            int exact = -1;
            int contains = -1;
            WTUsableItem exactUsable = null;
            WTUsableItem containsUsable = null;

            for (int i = 0; i < me.inventory.Count; i++)
            {
                ItemSlot slot = me.inventory[i];
                if (slot.amount <= 0 || slot.item.data == null) continue;

                WTUsableItem candidate = slot.item.data as WTUsableItem;
                if (candidate == null) continue;

                string invName = candidate.name ?? slot.item.data.name ?? "";
                if (string.Equals(invName.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    exact = i;
                    exactUsable = candidate;
                    break;
                }

                if (contains < 0 && invName.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    contains = i;
                    containsUsable = candidate;
                }
            }

            if (exact >= 0)
            {
                inventoryIndex = exact;
                usable = exactUsable;
                return true;
            }

            if (contains >= 0)
            {
                inventoryIndex = contains;
                usable = containsUsable;
                return true;
            }

            return false;
        }
    }
}
