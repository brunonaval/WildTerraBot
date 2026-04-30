using System;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEngine;
using UnityEngine.AI;

namespace WildTerraBot
{
    /// <summary>
    /// Treino de Cura (modo independente). Usa APENAS funções do próprio jogo:
    /// - WTPlayer.CmdSetTarget
    /// - WTPlayer.TryUseSkill (respeita CheckSelf/Target/Distance/Cooldown do jogo)
    ///
    /// A escolha de skill é por prioridade fixa (ordem da lista). Alvo pode ser PET/SELF/PLAYER_BY_NAME.
    /// Quando FollowTopTargetEnabled  PLAYER_BY_NAME estiver ativo, o controller entra no modo
    /// "follow heal": segue apenas o primeiro nome da lista, ignora SkillNames, monitora o HP do alvo,
    /// usa a skill configurada por threshold e preserva o healer com itens de autocura.
    /// </summary>
    public class HealTrainingController
    {
        public class Config
        {
            public string WeaponName = "";
            public string TargetMode = "PET"; // PET | SELF | PLAYER_BY_NAME
            public int Radius = 18;
            public List<string> SkillNames = new List<string>();
            public List<string> TargetNames = new List<string>(); // apenas PLAYER_BY_NAME

            public bool FollowTopTargetEnabled = false;
            public string FollowSkillName = "";
            public int FollowTargetHpPct = 75;
            public float FollowDistance = 4.5f;
            public List<string> SelfRecoveryItemNames = new List<string>();
            public int SelfRecoveryHpPct = 40;
            public int SelfRecoveryResumeHpPct = 55;
        }

        private const float TICK_INTERVAL = 0.20f;
        private const float DEFAULT_ACTION_COOLDOWN = 0.20f;
        private const float ITEM_ACTION_COOLDOWN = 0.90f;
        private const float SKILL_ACTION_COOLDOWN = 0.25f;
        private const float FOLLOW_DISTANCE_BAND = 0.75f;
        private const float FOLLOW_REPATH_MIN_DELTA = 0.60f;
        private const float MOUNT_SYNC_COOLDOWN = 1.10f;
        private const float ANTI_AFK_IDLE_SECONDS = 28f;
        private const float ANTI_AFK_MOVE_DURATION = 1.75f;
        private const float ANTI_AFK_TARGET_MOVE_RESET_DISTANCE = 0.35f;
        private const float ANTI_AFK_REACH_EPSILON = 0.30f;
        private const float ANTI_AFK_ANGLE_DEGREES = 18f;

        private readonly Func<WTPlayer, string, int, bool> _equipFunc;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logWarn;

        private bool _enabled = false;
        private Config _cfg = null;
        private float _nextTick = 0f;
        private float _nextActionAt = 0f;
        private int _targetNameIndex = 0;
        private bool _selfRecoveryMode = false;
        private float _nextFollowMoveLogAt = 0f;
        private float _nextRecoveryWarnAt = 0f;
        private float _nextMountSyncAt = 0f;
        private Entity _lastFollowTarget = null;
        private Vector3 _lastObservedTargetPos = Vector3.zero;
        private float _lastTargetMovementAt = 0f;
        private float _nextAntiAfkAt = 0f;
        private bool _antiAfkFlip = false;
        private bool _antiAfkMoveActive = false;
        private Vector3 _antiAfkDestination = Vector3.zero;
        private float _antiAfkMoveUntil = 0f;

        public bool IsEnabled => _enabled;

        public HealTrainingController(Func<WTPlayer, string, int, bool> equipFunc, Action<string> logInfo, Action<string> logWarn)
        {
            _equipFunc = equipFunc;
            _logInfo = logInfo ?? (_ => { });
            _logWarn = logWarn ?? (_ => { });
        }

        public void Enable(Config cfg)
        {
            _cfg = cfg;
            _enabled = cfg != null;
            _targetNameIndex = 0;
            _nextTick = 0f;
            _nextActionAt = 0f;
            _selfRecoveryMode = false;
            _nextFollowMoveLogAt = 0f;
            _nextRecoveryWarnAt = 0f;
            _nextMountSyncAt = 0f;
            _lastFollowTarget = null;
            _lastObservedTargetPos = Vector3.zero;
            _lastTargetMovementAt = 0f;
            _nextAntiAfkAt = 0f;
            _antiAfkFlip = false;
            _antiAfkMoveActive = false;
            _antiAfkDestination = Vector3.zero;
            _antiAfkMoveUntil = 0f;
            if (_enabled)
            {
                string mode = IsFollowModeActive() ? "FOLLOW_HEAL" : "DEFAULT";
                _logInfo($"[CURA] HealTraining ENABLED mode={mode}");
            }
        }

        public void Disable()
        {
            _enabled = false;
            _cfg = null;
            _nextTick = 0f;
            _nextActionAt = 0f;
            _selfRecoveryMode = false;
            _nextFollowMoveLogAt = 0f;
            _nextRecoveryWarnAt = 0f;
            _nextMountSyncAt = 0f;
            _lastFollowTarget = null;
            _lastObservedTargetPos = Vector3.zero;
            _lastTargetMovementAt = 0f;
            _nextAntiAfkAt = 0f;
            _antiAfkFlip = false;
            _antiAfkMoveActive = false;
            _antiAfkDestination = Vector3.zero;
            _antiAfkMoveUntil = 0f;
            _logInfo("[CURA] HealTraining DISABLED");
        }

        public void Tick(WTPlayer me)
        {
            if (!_enabled || _cfg == null || me == null) return;
            if (Time.time < _nextTick) return;
            _nextTick = Time.time + TICK_INTERVAL;

            // Equipar arma de cura (slot 0) e continuar reaplicando se quebrar / trocar.
            if (!string.IsNullOrWhiteSpace(_cfg.WeaponName))
            {
                bool equipped = false;
                try { equipped = _equipFunc(me, _cfg.WeaponName, 0); } catch { }
                if (!equipped) return;
            }

            if (Time.time < _nextActionAt) return;

            if (IsFollowModeActive())
            {
                TickFollowMode(me);
                return;
            }

            TickDefaultMode(me);
        }

        private void TickDefaultMode(WTPlayer me)
        {
            Entity target = ResolveTarget(me);
            if (target == null) return;

            TrySetTarget(me, target);

            int skillIndex = ResolveFirstReadySkillIndex(me, _cfg.SkillNames);
            if (skillIndex < 0) return;

            try
            {
                me.TryUseSkill(skillIndex, ignoreState: false, actionBarMouseClick: false);
                _nextActionAt = Time.time + SKILL_ACTION_COOLDOWN;
            }
            catch { }
        }

        private void TickFollowMode(WTPlayer me)
        {
            Entity target = ResolveTarget(me);

            if (TryHandleSelfRecovery(me))
                return;

            if (target == null)
            {
                ResetFollowTracking(clearAntiAfk: true);
                return;
            }

            if (target.health <= 0)
            {
                ResetFollowTracking(clearAntiAfk: true);
                return;
            }

            UpdateFollowTracking(target);

            if (TrySyncMountState(me, target))
                return;

            TrySetTarget(me, target);

            if (TryMaintainFollowDistance(me, target, _cfg.FollowDistance))
                return;

            float targetHpPct = SafeHealthPercent(target);
            if (targetHpPct > 0f && targetHpPct <= _cfg.FollowTargetHpPct)
            {
                int skillIndex = ResolveReadySkillIndex(me, _cfg.FollowSkillName);
                if (skillIndex >= 0)
                {
                    try
                    {
                        me.TryUseSkill(skillIndex, ignoreState: false, actionBarMouseClick: false);
                        _nextActionAt = Time.time + SKILL_ACTION_COOLDOWN;
                        return;
                    }
                    catch { }
                }
            }

            TryDoAntiAfkStep(me, target, _cfg.FollowDistance);
        }

        private bool TryHandleSelfRecovery(WTPlayer me)
        {
            float myHpPct = SafeHealthPercent(me);
            if (myHpPct <= 0f) return false;

            if (_selfRecoveryMode)
            {
                if (myHpPct >= _cfg.SelfRecoveryResumeHpPct)
                {
                    _selfRecoveryMode = false;
                }
            }
            else if (myHpPct <= _cfg.SelfRecoveryHpPct)
            {
                _selfRecoveryMode = true;
            }

            if (!_selfRecoveryMode) return false;

            if (_cfg.SelfRecoveryItemNames == null || _cfg.SelfRecoveryItemNames.Count == 0)
            {
                if (Time.time >= _nextRecoveryWarnAt)
                {
                    _nextRecoveryWarnAt = Time.time + 5f;
                    _logWarn("[CURA] Follow Heal: HP baixo, mas a lista de itens de autocura está vazia");
                }
                return true;
            }

            if (me.target != me)
                TrySetTarget(me, me);

            foreach (string itemName in _cfg.SelfRecoveryItemNames)
            {
                int inventoryIndex;
                WTUsableItem usable;
                if (!TryFindUsableInventoryItem(me, itemName, out inventoryIndex, out usable))
                    continue;

                if (!IsHealthRecoveryItem(usable))
                    continue;

                try
                {
                    me.CmdUseInventoryItem(inventoryIndex);
                    _nextActionAt = Time.time + ITEM_ACTION_COOLDOWN;
                    _logInfo($"[CURA] self recovery use {usable.name} idx={inventoryIndex} hp={myHpPct:F0}%");
                    return true;
                }
                catch (Exception ex)
                {
                    _nextActionAt = Time.time + DEFAULT_ACTION_COOLDOWN;
                    _logWarn($"[CURA] self recovery error item='{itemName}' idx={inventoryIndex} ex={ex.GetType().Name}:{ex.Message}");
                    return true;
                }
            }

            if (Time.time >= _nextRecoveryWarnAt)
            {
                _nextRecoveryWarnAt = Time.time + 5f;
                _logWarn($"[CURA] Follow Heal: HP={myHpPct:F0}% abaixo do seguro, mas nenhum item configurado foi encontrado no inventário");
            }

            _nextActionAt = Time.time + DEFAULT_ACTION_COOLDOWN;
            return true;
        }

        private bool TryMaintainFollowDistance(WTPlayer me, Entity target, float desiredDistance)
        {
            if (me == null || target == null) return false;
            if (desiredDistance < 0.5f) desiredDistance = 0.5f;

            Vector3 myPos = me.transform.position;
            Vector3 targetPos = target.transform.position;
            Vector3 offset = myPos - targetPos;
            offset.y = 0f;
            float distance = offset.magnitude;

            float lower = Mathf.Max(0.10f, desiredDistance - FOLLOW_DISTANCE_BAND);
            float upper = desiredDistance + FOLLOW_DISTANCE_BAND;

            if (_antiAfkMoveActive)
            {
                float antiAfkRemain = FlatDistance(myPos, _antiAfkDestination);
                if (Time.time >= _antiAfkMoveUntil || antiAfkRemain <= ANTI_AFK_REACH_EPSILON)
                {
                    _antiAfkMoveActive = false;
                    StopAgent(me);
                }
                else if (distance >= lower && distance <= upper)
                {
                    return true;
                }
                else
                {
                    _antiAfkMoveActive = false;
                }
            }

            if (distance >= lower && distance <= upper)
            {
                StopAgent(me);
                return false;
            }

            Vector3 dir;
            if (distance > 0.01f) dir = offset / distance;
            else
            {
                dir = me.transform.forward;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.01f) dir = Vector3.back;
                dir.Normalize();
            }

            Vector3 desiredPos = targetPos + dir * desiredDistance;
            desiredPos.y = myPos.y;

            MoveAgentToPoint(me, desiredPos, 0.15f);

            if (Time.time >= _nextFollowMoveLogAt)
            {
                _nextFollowMoveLogAt = Time.time + 1.5f;
                _logInfo($"[CURA] follow move target='{SafeEntityName(target)}' dist={distance:F1} desired={desiredDistance:F1}");
            }

            _nextActionAt = Time.time + DEFAULT_ACTION_COOLDOWN;
            return true;
        }

        private Entity ResolveTarget(WTPlayer me)
        {
            string mode = (_cfg.TargetMode ?? "PET").Trim().ToUpperInvariant();
            if (mode == "SELF") return me;

            if (mode == "PLAYER_BY_NAME")
            {
                if (_cfg.TargetNames == null || _cfg.TargetNames.Count == 0) return null;

                if (IsFollowModeActive())
                {
                    string wanted = ResolvePrimaryTargetName(_cfg.TargetNames);
                    if (string.IsNullOrWhiteSpace(wanted)) return null;
                    return FindPlayerByNameNear(me, wanted, _cfg.Radius);
                }

                for (int attempt = 0; attempt < _cfg.TargetNames.Count; attempt++)
                {
                    int idx = (_targetNameIndex + attempt) % _cfg.TargetNames.Count;
                    string wanted = _cfg.TargetNames[idx];
                    if (string.IsNullOrWhiteSpace(wanted)) continue;
                    var p = FindPlayerByNameNear(me, wanted, _cfg.Radius);
                    if (p != null)
                    {
                        _targetNameIndex = idx;
                        return p;
                    }
                }
                return null;
            }

            return FindOwnedPet(me, _cfg.Radius);
        }

        private bool IsFollowModeActive()
        {
            return _cfg != null
                && _cfg.FollowTopTargetEnabled
                && string.Equals((_cfg.TargetMode ?? "").Trim(), "PLAYER_BY_NAME", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolvePrimaryTargetName(List<string> targetNames)
        {
            if (targetNames == null) return "";
            for (int i = 0; i < targetNames.Count; i++)
            {
                string value = (targetNames[i] ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return "";
        }

        private static float SafeHealthPercent(Entity entity)
        {
            if (entity == null) return 0f;
            try { return Mathf.Clamp(entity.HealthPercent() * 100f, 0f, 100f); }
            catch { return 0f; }
        }

        private void ResetFollowTracking(bool clearAntiAfk)
        {
            _lastFollowTarget = null;
            _lastObservedTargetPos = Vector3.zero;
            _lastTargetMovementAt = 0f;
            _nextAntiAfkAt = 0f;
            if (clearAntiAfk)
            {
                _antiAfkMoveActive = false;
                _antiAfkDestination = Vector3.zero;
                _antiAfkMoveUntil = 0f;
            }
        }

        private void UpdateFollowTracking(Entity target)
        {
            if (target == null)
            {
                ResetFollowTracking(clearAntiAfk: true);
                return;
            }

            Vector3 targetPos = target.transform.position;
            targetPos.y = 0f;

            if (!ReferenceEquals(_lastFollowTarget, target))
            {
                _lastFollowTarget = target;
                _lastObservedTargetPos = targetPos;
                _lastTargetMovementAt = Time.time;
                _nextAntiAfkAt = Time.time + ANTI_AFK_IDLE_SECONDS;
                _antiAfkMoveActive = false;
                _antiAfkDestination = Vector3.zero;
                _antiAfkMoveUntil = 0f;
                return;
            }

            if ((_lastObservedTargetPos - targetPos).sqrMagnitude >= (ANTI_AFK_TARGET_MOVE_RESET_DISTANCE * ANTI_AFK_TARGET_MOVE_RESET_DISTANCE))
            {
                _lastObservedTargetPos = targetPos;
                _lastTargetMovementAt = Time.time;
                _nextAntiAfkAt = Time.time + ANTI_AFK_IDLE_SECONDS;
                _antiAfkMoveActive = false;
                _antiAfkDestination = Vector3.zero;
                _antiAfkMoveUntil = 0f;
            }
        }

        private bool TrySyncMountState(WTPlayer me, Entity target)
        {
            if (me == null || target == null) return false;
            if (Time.time < _nextMountSyncAt) return false;

            Player targetPlayer = target as Player;
            if (targetPlayer == null) return false;

            bool targetMounted;
            bool meMounted;
            try
            {
                targetMounted = targetPlayer.IsMounted();
                meMounted = me.IsMounted();
            }
            catch
            {
                return false;
            }

            if (targetMounted == meMounted) return false;

            try
            {
                me.CmdToggleMount();
                _nextMountSyncAt = Time.time + MOUNT_SYNC_COOLDOWN;
                _nextActionAt = Time.time + MOUNT_SYNC_COOLDOWN;
                _antiAfkMoveActive = false;
                _antiAfkDestination = Vector3.zero;
                _antiAfkMoveUntil = 0f;
                _logInfo($"[CURA] follow mount sync target='{SafeEntityName(target)}' action={(targetMounted ? "MONTAR" : "DESMONTAR")}");
                return true;
            }
            catch (Exception ex)
            {
                _nextMountSyncAt = Time.time + DEFAULT_ACTION_COOLDOWN;
                _logWarn($"[CURA] follow mount sync error target='{SafeEntityName(target)}' ex={ex.GetType().Name}:{ex.Message}");
                return false;
            }
        }

        private bool TryDoAntiAfkStep(WTPlayer me, Entity target, float desiredDistance)
        {
            if (me == null || target == null) return false;
            if (desiredDistance < 1.0f) desiredDistance = 1.0f;
            if (_lastTargetMovementAt <= 0f) return false;
            if (Time.time < _nextAntiAfkAt) return false;
            if ((Time.time - _lastTargetMovementAt) < ANTI_AFK_IDLE_SECONDS) return false;
            if (_selfRecoveryMode) return false;
            if (_antiAfkMoveActive) return false;

            Vector3 myPos = me.transform.position;
            Vector3 targetPos = target.transform.position;
            float distance = FlatDistance(myPos, targetPos);

            float lower = Mathf.Max(0.10f, desiredDistance - FOLLOW_DISTANCE_BAND);
            float upper = desiredDistance + FOLLOW_DISTANCE_BAND;
            if (distance < lower || distance > upper) return false;

            Vector3 offset = myPos - targetPos;
            offset.y = 0f;
            if (offset.sqrMagnitude < 0.01f)
            {
                offset = me.transform.right;
                offset.y = 0f;
                if (offset.sqrMagnitude < 0.01f)
                    offset = Vector3.right;
            }
            offset.Normalize();

            float signedAngle = _antiAfkFlip ? ANTI_AFK_ANGLE_DEGREES : -ANTI_AFK_ANGLE_DEGREES;
            _antiAfkFlip = !_antiAfkFlip;

            Vector3 rotatedOffset = Quaternion.Euler(0f, signedAngle, 0f) * offset;
            Vector3 desiredPos = targetPos + rotatedOffset.normalized * desiredDistance;
            desiredPos.y = myPos.y;

            MoveAgentToPoint(me, desiredPos, 0.05f);
            _antiAfkMoveActive = true;
            _antiAfkDestination = desiredPos;
            _antiAfkMoveUntil = Time.time + ANTI_AFK_MOVE_DURATION;
            _nextAntiAfkAt = Time.time + ANTI_AFK_IDLE_SECONDS;
            _nextActionAt = Time.time + DEFAULT_ACTION_COOLDOWN;

            _logInfo($"[CURA] anti-afk step target='{SafeEntityName(target)}' angle={signedAngle:F0} dist={desiredDistance:F1}");
            return true;
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private void TrySetTarget(WTPlayer me, Entity target)
        {
            if (me == null || target == null) return;
            try
            {
                if (me.target == target) return;
                var ni = target.netIdentity ?? target.GetComponent<NetworkIdentity>();
                if (ni != null) me.CmdSetTarget(ni);
            }
            catch { }
        }

        private static string SafeEntityName(Entity entity)
        {
            try { return entity != null ? (entity.name ?? "") : ""; }
            catch { return ""; }
        }

        private static Pet FindOwnedPet(WTPlayer me, float radius)
        {
            try
            {
                Vector3 pos = me.transform.position;
                float bestSqr = radius * radius;
                Pet best = null;
                foreach (var pet in UnityEngine.Object.FindObjectsOfType<Pet>())
                {
                    if (pet == null || !pet.isActiveAndEnabled) continue;
                    if (pet.health <= 0) continue;
                    if (pet.owner != me) continue;
                    float sqr = (pet.transform.position - pos).sqrMagnitude;
                    if (sqr <= bestSqr) { bestSqr = sqr; best = pet; }
                }
                return best;
            }
            catch { return null; }
        }

        private static Player FindPlayerByNameNear(WTPlayer me, string name, float radius)
        {
            try
            {
                Vector3 pos = me.transform.position;
                float bestSqr = radius * radius;
                Player best = null;
                foreach (var p in UnityEngine.Object.FindObjectsOfType<Player>())
                {
                    if (p == null || !p.isActiveAndEnabled) continue;
                    if (p.health <= 0) continue;
                    if (!string.Equals((p.name ?? "").Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                    float sqr = (p.transform.position - pos).sqrMagnitude;
                    if (sqr <= bestSqr) { bestSqr = sqr; best = p; }
                }
                return best;
            }
            catch { return null; }
        }

        private static int ResolveReadySkillIndex(WTPlayer me, string skillName)
        {
            if (me == null || string.IsNullOrWhiteSpace(skillName)) return -1;
            try
            {
                int idx = me.skills.FindIndex(s => string.Equals((s.name ?? "").Trim(), skillName.Trim(), StringComparison.OrdinalIgnoreCase));
                if (idx < 0) return -1;
                Skill skill = me.skills[idx];
                if (!skill.IsReady()) return -1;
                return idx;
            }
            catch { return -1; }
        }

        private static int ResolveFirstReadySkillIndex(WTPlayer me, List<string> skillNames)
        {
            if (skillNames == null || skillNames.Count == 0) return -1;
            try
            {
                for (int i = 0; i < skillNames.Count; i++)
                {
                    string sn = (skillNames[i] ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(sn)) continue;
                    int idx = me.skills.FindIndex(s => string.Equals((s.name ?? "").Trim(), sn, StringComparison.OrdinalIgnoreCase));
                    if (idx < 0) continue;
                    Skill skill = me.skills[idx];
                    if (skill.IsReady()) return idx;
                }
            }
            catch { }
            return -1;
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

        private static bool IsHealthRecoveryItem(WTUsableItem usable)
        {
            if (usable == null || usable.foods == null || usable.foods.Length == 0)
                return false;

            try
            {
                return usable.foods.Any(f => f.type == FoodType.Health && f.value > 0);
            }
            catch
            {
                return false;
            }
        }

        private static void StopAgent(WTPlayer me)
        {
            try
            {
                if (me == null || me.agent == null || !me.agent.enabled) return;
                if (me.agent.isOnNavMesh) me.agent.isStopped = true;
                me.agent.velocity = Vector3.zero;
                me.agent.ResetPath();
            }
            catch { }
        }

        private static void MoveAgentToPoint(WTPlayer me, Vector3 point, float stoppingDistance)
        {
            try
            {
                if (me == null) return;
                var agent = me.agent;
                if (agent == null || !agent.enabled) return;

                Vector3 dest = point;
                NavMeshHit hit;
                if (NavMesh.SamplePosition(point, out hit, 4.0f, NavMesh.AllAreas))
                    dest = hit.position;

                if (!agent.isOnNavMesh)
                {
                    if (NavMesh.SamplePosition(me.transform.position, out hit, 10.0f, NavMesh.AllAreas))
                    {
                        agent.Warp(hit.position);
                    }
                    else
                    {
                        return;
                    }
                }

                if ((agent.destination - dest).sqrMagnitude < (FOLLOW_REPATH_MIN_DELTA * FOLLOW_REPATH_MIN_DELTA) && !agent.isStopped)
                    return;

                agent.isStopped = false;
                agent.stoppingDistance = Mathf.Max(0f, stoppingDistance);
                agent.destination = dest;
            }
            catch { }
        }
    }
}