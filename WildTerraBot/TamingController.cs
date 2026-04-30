using Mirror;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace WildTerraBot
{
    public class TamingController
    {

        public class Config
        {
            public string Mode = "PACIFICO";
            public string TrapName = "";
            public string CombatWeaponName = "";
            public List<string> TargetNames = new List<string>();
            public float SearchRadius = 22f;
            public int MaxTrapAttempts = 3;
        }

        private enum TamingState
        {
            Patrol,
            EquipCombatWeapon,
            ApproachCombatRange,
            AttackUntilFlee,
            WatchFlee,
            ApproachTrapRange,
            Dismount,
            EquipTrap,
            ThrowTrap,
            ConfirmTrap,
            ApproachCatch,
            UseCatching,
            ConfirmCatch
        }

        private readonly Action<string> _log;
        private readonly Func<WTPlayer, bool> _isMounted;
        private readonly Action<WTPlayer> _toggleMount;
        private readonly Action<WTPlayer, float, float> _moveToXZ;
        private readonly Func<WTPlayer, string, int, bool> _checkAndEquipItem;
        private readonly Action<WTPlayer, NetworkIdentity> _cmdSetTarget;
        private readonly Action<WTPlayer, Vector3> _cmdSkillToPoint;
        private readonly Action<WTPlayer, int> _tryUseSkill;

        private Config _config = new Config();
        private TamingState _state = TamingState.Patrol;
        private WTMob _target = null;
        private float _stateUntil = 0f;
        private float _nextActionAt = 0f;
        private float _nextScanAt = 0f;
        private float _nextMoveLogAt = 0f;
        private float _nextStatusLogAt = 0f;
        private float _lastTrapThrowAt = 0f;
        private float _trapRange = 7.5f;
        private float _catchRange = 1.5f;
        private float _combatRange = 1.5f;
        private bool _combatIsRanged = false;
        private int _catchSkillIndex = -1;
        private int _trapAttempts = 0;
        private int _catchAttempts = 0;
        private const int MAX_CATCH_ATTEMPTS = 3;
        private const float CATCH_DISTANCE_BUFFER = 0.25f;
        private const float MIN_CATCH_DISTANCE = 1.05f;

        private string _expectedCatchItemName = "";
        private int _expectedCatchItemCount = 0;
        private bool _trapEquipLatched = false;
        private bool _trapThrowPrepared = false;
        private float _reacquireBlockedUntil = 0f;
        private float _lastAggroAttackAt = 0f;
        private bool _watchingForFlee = false;
        private float _fleeHpThreshold = -1f;
        private const float FLEE_MARGIN = 0.05f;
        private int _extraHitsAfterThreshold = 0;
        private const int MAX_EXTRA_HITS_AFTER_THRESHOLD = 1;
        private WTPlayer _lastActor = null;
        private bool _pausedByDefense = false;
        private string _pauseReason = "";
        private readonly Dictionary<int, float> _blacklistUntil = new Dictionary<int, float>();

        public bool IsEnabled { get; private set; }
        public bool IsBusy => IsEnabled && _state != TamingState.Patrol && !_pausedByDefense;
        public bool IsPausedByDefense => IsEnabled && _pausedByDefense;
        public bool IsInAggressiveCombatPhase => IsEnabled && (_state == TamingState.EquipCombatWeapon || _state == TamingState.ApproachCombatRange || _state == TamingState.AttackUntilFlee || _state == TamingState.WatchFlee);
        public WTMob CurrentTarget => _target;
        public string CurrentStateName => _state.ToString();
        public string CombatWeaponName => _config != null ? _config.CombatWeaponName : "";

        public TamingController(
            Action<string> log,
            Func<WTPlayer, bool> isMounted,
            Action<WTPlayer> toggleMount,
            Action<WTPlayer, float, float> moveToXZ,
            Func<WTPlayer, string, int, bool> checkAndEquipItem,
            Action<WTPlayer, NetworkIdentity> cmdSetTarget,
            Action<WTPlayer, Vector3> cmdSkillToPoint,
            Action<WTPlayer, int> tryUseSkill)
        {
            _log = log ?? (_ => { });
            _isMounted = isMounted ?? (_ => false);
            _toggleMount = toggleMount ?? (_ => { });
            _moveToXZ = moveToXZ ?? ((_, __, ___) => { });
            _checkAndEquipItem = checkAndEquipItem ?? ((_, __, ___) => false);
            _cmdSetTarget = cmdSetTarget ?? ((_, __) => { });
            _cmdSkillToPoint = cmdSkillToPoint ?? ((_, __) => { });
            _tryUseSkill = tryUseSkill ?? ((_, __) => { });
        }

        public void Enable(Config config)
        {
            _config = NormalizeConfig(config);
            CleanupExpiredBlacklist();
            ResetRun(clearBlacklist: false);
            IsEnabled = true;
            _log($"[TAMING] ON mode={_config.Mode} trap='{_config.TrapName}' weapon='{_config.CombatWeaponName}' targets={string.Join(", ", _config.TargetNames)}");
        }

        public void Disable()
        {
            if (IsEnabled || _target != null)
            {
                _log("[TAMING] OFF");
            }
            IsEnabled = false;
            ResetRun(clearBlacklist: false);
        }

        public void AbortCurrent(string reason)
        {
            if (!IsEnabled || (_state == TamingState.Patrol && _target == null)) return;
            _log($"[TAMING] abort: {reason}");
            BlacklistCurrentTarget(12f);
            CleanupActorContext(success: false);
            ResetRun(clearBlacklist: false);
        }

        public void PauseForDefense(string reason)
        {
            if (!IsEnabled || _pausedByDefense) return;
            
            _pausedByDefense = true;
            _pauseReason = NormalizeToken(reason);
            _nextActionAt = 0f;
            _nextMoveLogAt = 0f;
            _trapThrowPrepared = false;

            try
            {
                if (_lastActor != null)
                {
                    SafeStopActor(_lastActor);
                    _lastActor.target = null;
                }
            }
            catch { }

            _log($"[TAMING-DEF] pause state='{_state}' tameTarget='{SafeName(_target)}' reason='{_pauseReason}'");
        }

        public void ResumeAfterDefense(WTPlayer me)
        {
            if (!IsEnabled || !_pausedByDefense) return;

            _pausedByDefense = false;
            _pauseReason = "";
            _nextActionAt = 0f;
            _nextMoveLogAt = 0f;
            _trapThrowPrepared = false;
            _trapEquipLatched = false;
            _stateUntil = 0f;

            if (me != null)
            {
                try
                {
                    SafeStopActor(me);
                    me.target = null;
                }
                catch { }
                _lastActor = me;
            }

            if (!IsValidTarget(_target))
            {
                _log("[TAMING-DEF] resume falhou: alvo da doma não é mais válido. voltando para patrulha.");
                ResetRun(clearBlacklist: false);
                return;
            }

            if (TargetHasTrapEffect(_target, out string effects))
            {
                _state = TamingState.ApproachCatch;
                _stateUntil = Time.time + 8.0f;
                _log($"[TAMING-DEF] resume -> ApproachCatch target='{SafeName(_target)}' effects=[{effects}]");
                return;
            }

            bool agressivo = string.Equals(_config.Mode, "AGRESSIVO", StringComparison.OrdinalIgnoreCase);
            if (agressivo)
            {
                if (HasConfirmedFlee(_target))
                {
                    _state = TamingState.ApproachTrapRange;
                    _stateUntil = Time.time + 10.0f;
                    _log($"[TAMING-DEF] resume -> ApproachTrapRange target='{SafeName(_target)}' (fuga confirmada)");
                    return;
                }

                _state = TamingState.EquipCombatWeapon;
                _stateUntil = Time.time + 8.0f;
                _log($"[TAMING-DEF] resume -> EquipCombatWeapon target='{SafeName(_target)}'");
                return;
            }

            _state = TamingState.ApproachTrapRange;
            _stateUntil = Time.time + 10.0f;
            _log($"[TAMING-DEF] resume -> ApproachTrapRange target='{SafeName(_target)}'");
        }

        public bool Tick(WTPlayer me)
        {
            if (!IsEnabled || me == null) return false;

            _lastActor = me;
            CleanupExpiredBlacklist();

            if (_pausedByDefense)
            {
                return false;
            }

            if (_target != null && !IsValidTarget(_target) && _state != TamingState.ConfirmCatch)
            {
                Fail("alvo inválido antes da confirmação");
                return false;
            }

            switch (_state)
            {
                case TamingState.Patrol:
                    return TickPatrol(me);
                case TamingState.EquipCombatWeapon:
                    return TickEquipCombatWeapon(me);
                case TamingState.ApproachCombatRange:
                    return TickApproachCombatRange(me);
                case TamingState.AttackUntilFlee:
                    return TickAttackUntilFlee(me);
                case TamingState.WatchFlee:
                    return TickWatchFlee(me);
                case TamingState.ApproachTrapRange:
                    return TickApproachTrapRange(me);
                case TamingState.Dismount:
                    return TickDismount(me);
                case TamingState.EquipTrap:
                    return TickEquipTrap(me);
                case TamingState.ThrowTrap:
                    return TickThrowTrap(me);
                case TamingState.ConfirmTrap:
                    return TickConfirmTrap(me);
                case TamingState.ApproachCatch:
                    return TickApproachCatch(me);
                case TamingState.UseCatching:
                    return TickUseCatching(me);
                case TamingState.ConfirmCatch:
                    return TickConfirmCatch(me);
                default:
                    return false;
            }
        }

        private Config NormalizeConfig(Config cfg)
        {
            cfg = cfg ?? new Config();
            return new Config
            {
                Mode = NormalizeToken(cfg.Mode).ToUpperInvariant(),
                TrapName = NormalizeToken(cfg.TrapName),
                CombatWeaponName = NormalizeToken(cfg.CombatWeaponName),
                TargetNames = (cfg.TargetNames ?? new List<string>())
                    .Select(NormalizeToken)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                SearchRadius = Mathf.Clamp(cfg.SearchRadius, 8f, 40f),
                MaxTrapAttempts = Mathf.Clamp(cfg.MaxTrapAttempts, 1, 6)
            };
        }

        private bool TickPatrol(WTPlayer me)
        {
            if (Time.time < _reacquireBlockedUntil) return false;
            if (Time.time < _nextScanAt) return false;
            _nextScanAt = Time.time + 0.35f;

            bool pacifico = string.Equals(_config.Mode, "PACIFICO", StringComparison.OrdinalIgnoreCase);
            bool agressivo = string.Equals(_config.Mode, "AGRESSIVO", StringComparison.OrdinalIgnoreCase);
            if (!pacifico && !agressivo)
            {
                if (Time.time >= _nextStatusLogAt)
                {
                    _nextStatusLogAt = Time.time + 4.0f;
                    _log($"[TAMING] modo '{_config.Mode}' ainda não implementado nesta etapa.");
                }
                return false;
            }

            var candidate = FindBestTarget(me);
            if (candidate == null) return false;

            _target = candidate;
            _state = agressivo ? TamingState.EquipCombatWeapon : TamingState.ApproachTrapRange;
            _stateUntil = Time.time + (agressivo ? 8f : 18f);
            _nextMoveLogAt = 0f;
            _nextActionAt = 0f;
            _trapAttempts = 0;
            _catchAttempts = 0;
            _trapEquipLatched = false;
            _trapThrowPrepared = false;
            _lastAggroAttackAt = 0f;
            _watchingForFlee = false;
            _extraHitsAfterThreshold = 0;
            _catchSkillIndex = FindSkillIndexByName(me, "Catching");
            _catchRange = GetSkillCastRange(me, _catchSkillIndex, 1.5f);
            _trapRange = GetTrapRange(me, _config.TrapName, 7.5f);
            _expectedCatchItemName = GetExpectedCatchItemName(_target);
            _expectedCatchItemCount = CountInventoryItemByName(me, _expectedCatchItemName);
            _fleeHpThreshold = agressivo ? 0.10f : GetFleeHealthThreshold(_target);
            _combatRange = GetCombatWeaponRange(me, _config.CombatWeaponName, 2.0f, out _combatIsRanged);

            TrySetTarget(me, _target);
            _log($"[TAMING] alvo travado '{SafeName(_target)}' dist={DistanceToTarget(me, _target):F1} trapRange={_trapRange:F1} catchRange={_catchRange:F1} expectedPet='{_expectedCatchItemName}' countBefore={_expectedCatchItemCount}");
            if (agressivo)
            {
                _log($"[TAMING-AGGRO] triggerHp={_fleeHpThreshold:P0} combatWeapon='{_config.CombatWeaponName}' combatRange={_combatRange:F1}");
            }
            return true;
        }

        private bool TickEquipCombatWeapon(WTPlayer me)
        {
            if (!EnsureTargetStillValid()) return false;
            if (string.IsNullOrWhiteSpace(_config.CombatWeaponName)) { Fail("arma de combate não configurada"); return false; }
            if (Time.time > _stateUntil) { Fail("timeout equipando arma de combate"); return false; }

            if (IsCombatWeaponEquipped(me, _config.CombatWeaponName, out string handInfo, out float range, out bool isRanged))
            {
                _combatRange = Mathf.Max(1.0f, range);
                _combatIsRanged = isRanged;
                _state = TamingState.ApproachCombatRange;
                _stateUntil = Time.time + 18f;
                _nextMoveLogAt = 0f;
                _log($"[TAMING-AGGRO] combat weapon equipada '{_config.CombatWeaponName}'. hand={handInfo} range={_combatRange:F1} ranged={_combatIsRanged}");
                return true;
            }

            if (Time.time >= _nextActionAt)
            {
                _nextActionAt = Time.time + 0.20f;
                bool alreadyEquipped = _checkAndEquipItem(me, _config.CombatWeaponName, 0);
                _log($"[TAMING-AGGRO] equip combat weapon='{_config.CombatWeaponName}' alreadyEquipped={alreadyEquipped} hand={handInfo}");
            }
            return true;
        }

        private bool TickApproachCombatRange(WTPlayer me)
        {
            if (!EnsureTargetStillValid()) return false;
            if (Time.time > _stateUntil) { Fail("timeout aproximando para combate"); return false; }

            float dist = DistanceToTarget(me, _target);
            float desired = GetAggressiveEngageDistance();
            if (dist > desired)
            {
                if (Time.time >= _nextMoveLogAt)
                {
                    _nextMoveLogAt = Time.time + 0.8f;
                    _log($"[TAMING-AGGRO] aproximando combate alvo='{SafeName(_target)}' dist={dist:F1} desired<={desired:F1}");
                }
                _moveToXZ(me, _target.transform.position.x, _target.transform.position.z);
                return true;
            }

            _state = TamingState.AttackUntilFlee;
            _stateUntil = Time.time + 24f;
            _nextActionAt = 0f;
            _extraHitsAfterThreshold = 0;
            _watchingForFlee = false;
            _log($"[TAMING-AGGRO] entrou no range de combate. dist={dist:F1}. iniciando ataques controlados.");
            return true;
        }

        private bool TickAttackUntilFlee(WTPlayer me)
        {
            if (!EnsureTargetStillValid()) return false;
            if (HasConfirmedFlee(_target))
            {
                SwitchToTrapPipeline("fuga confirmada durante ataque");
                return true;
            }
            if (Time.time > _stateUntil) { Fail("timeout forçando fuga"); return false; }

            float dist = DistanceToTarget(me, _target);
            float desired = GetAggressiveEngageDistance();
            float hp = GetTargetHealthPercent(_target);
            if (_fleeHpThreshold > 0f && hp <= Mathf.Clamp01(_fleeHpThreshold + FLEE_MARGIN))
            {
                _watchingForFlee = true;
                _state = TamingState.WatchFlee;
                _stateUntil = Time.time + 2.2f;
                _nextActionAt = Mathf.Max(_nextActionAt, Time.time + 0.35f);
                _log($"[TAMING-AGGRO] entrando em watch flee hp={hp:P0} threshold={_fleeHpThreshold:P0} state={_target.state}");
                return true;
            }

            if (dist > desired + 0.5f)
            {
                _state = TamingState.ApproachCombatRange;
                _stateUntil = Time.time + 12f;
                return true;
            }

            if (_isMounted(me))
            {
                if (Time.time >= _nextActionAt)
                {
                    _nextActionAt = Time.time + 0.9f;
                    _log("[TAMING-AGGRO] desmontando para atacar...");
                    _toggleMount(me);
                }
                return true;
            }

            if (Time.time >= _nextActionAt)
            {
                Vector3 aim = GetCombatAimPoint(me, _target, out string aimInfo);
                SafeStopActor(me);
                FacePoint(me, aim);
                TrySetTarget(me, _target);
                _cmdSkillToPoint(me, aim);
                _lastAggroAttackAt = Time.time;
                _nextActionAt = Time.time + 0.90f;
                _log($"[TAMING-AGGRO] attack pulse hp={hp:P0} threshold={_fleeHpThreshold:P0} state={_target.state} dist={dist:F1} aim={aimInfo}");
            }
            return true;
        }

        private bool TickWatchFlee(WTPlayer me)
        {
            if (!EnsureTargetStillValid()) return false;
            if (HasConfirmedFlee(_target))
            {
                SwitchToTrapPipeline("fuga confirmada");
                return true;
            }

            float dist = DistanceToTarget(me, _target);
            float desired = GetAggressiveEngageDistance();
            float hp = GetTargetHealthPercent(_target);

            if (dist > desired + 0.7f)
            {
                _state = TamingState.ApproachCombatRange;
                _stateUntil = Time.time + 10f;
                return true;
            }

            if (_isMounted(me))
            {
                if (Time.time >= _nextActionAt)
                {
                    _nextActionAt = Time.time + 0.9f;
                    _log("[TAMING-AGGRO] desmontando em watch flee...");
                    _toggleMount(me);
                }
                return true;
            }

            if (Time.time > _stateUntil)
            {
                if (_extraHitsAfterThreshold < MAX_EXTRA_HITS_AFTER_THRESHOLD)
                {
                    _extraHitsAfterThreshold++;
                    _state = TamingState.AttackUntilFlee;
                    _stateUntil = Time.time + 5.0f;
                    _nextActionAt = Time.time;
                    _log($"[TAMING-AGGRO] watch flee expirou sem Slowdown/HP. extraHit={_extraHitsAfterThreshold}/{MAX_EXTRA_HITS_AFTER_THRESHOLD}");
                    return true;
                }

                Fail($"alvo não entrou em fuga hp={hp:P0} state={_target.state}");
                return false;
            }

            if (Time.time >= _nextMoveLogAt)
            {
                _nextMoveLogAt = Time.time + 0.7f;
                _log($"[TAMING-AGGRO] watch flee hp={hp:P0} threshold={_fleeHpThreshold:P0} state={_target.state} dist={dist:F1}");
            }
            SafeStopActor(me);
            TrySetTarget(me, _target);
            return true;
        }

        private void SwitchToTrapPipeline(string reason)
        {
            _watchingForFlee = false;
            _trapAttempts = 0;
            _catchAttempts = 0;
            _trapEquipLatched = false;
            _trapThrowPrepared = false;
            _state = TamingState.ApproachTrapRange;
            _stateUntil = Time.time + 12f;
            _nextMoveLogAt = 0f;
            _nextActionAt = 0f;
            _log($"[TAMING-AGGRO] flee confirmed ({reason}) -> mudando para pipeline da trap");
        }

        private bool TickApproachTrapRange(WTPlayer me)
        {
            if (!EnsureTargetStillValid()) return false;
            if (Time.time > _stateUntil) { Fail("timeout aproximando para trap"); return false; }

            float dist = DistanceToTarget(me, _target);
            float desired = Mathf.Max(2.2f, _trapRange - 0.8f);

            if (dist > desired)
            {
                if (Time.time >= _nextMoveLogAt)
                {
                    _nextMoveLogAt = Time.time + 1.0f;
                    _log($"[TAMING] aproximando trap alvo='{SafeName(_target)}' dist={dist:F1} desired<={desired:F1}");
                }
                _moveToXZ(me, _target.transform.position.x, _target.transform.position.z);
                return true;
            }

            _state = TamingState.Dismount;
            _stateUntil = Time.time + 4.5f;
            _nextActionAt = 0f;
            _log($"[TAMING] entrou no range da trap. dist={dist:F1}. preparando desmontar.");
            return true;
        }

        private bool TickDismount(WTPlayer me)
        {
            if (!EnsureTargetStillValid()) return false;
            if (!_isMounted(me))
            {
                _state = TamingState.EquipTrap;
                _stateUntil = Time.time + 6.0f;
                _nextActionAt = 0f;
                _log("[TAMING] desmontado. equipando armadilha...");
                return true;
            }

            if (Time.time > _stateUntil) { Fail("timeout desmontando"); return false; }
            if (Time.time >= _nextActionAt)
            {
                _nextActionAt = Time.time + 1.2f;
                _log("[TAMING] desmontando...");
                _toggleMount(me);
            }
            return true;
        }

        private bool TickEquipTrap(WTPlayer me)
        {
            if (!EnsureTargetStillValid()) return false;
            if (Time.time > _stateUntil) { Fail("timeout equipando trap"); return false; }

            bool equippedNow = IsTrapEquipped(me, _config.TrapName, out string handInfo);
            if (equippedNow) _trapEquipLatched = true;

            if (_trapEquipLatched)
            {
                _state = TamingState.ThrowTrap;
                _stateUntil = Time.time + 4.0f;
                _nextActionAt = 0f;
                _trapThrowPrepared = false;
                _log($"[TAMING] trap equipada '{_config.TrapName}'. hand={handInfo}");
                return true;
            }

            if (Time.time >= _nextActionAt)
            {
                _nextActionAt = Time.time + 1.0f;
                bool alreadyEquipped = _checkAndEquipItem(me, _config.TrapName, 0);
                bool equippedAfterEquip = IsTrapEquipped(me, _config.TrapName, out string handAfterEquip);
                if (equippedAfterEquip) _trapEquipLatched = true;
                _log($"[TAMING] equip trap='{_config.TrapName}' alreadyEquipped={alreadyEquipped} handBefore={handInfo} handAfter={handAfterEquip}");
                if (_trapEquipLatched)
                {
                    _state = TamingState.ThrowTrap;
                    _stateUntil = Time.time + 4.0f;
                    _nextActionAt = 0f;
                    _trapThrowPrepared = false;
                    _log($"[TAMING] trap equipada '{_config.TrapName}' logo após equip. hand={handAfterEquip}");
                    return true;
                }

            }
            return true;
        }

        private bool TickThrowTrap(WTPlayer me)
        {
            if (!EnsureTargetStillValid()) return false;
            if (Time.time > _stateUntil) { Fail("timeout preparando lançamento da trap"); return false; }

            float dist = DistanceToTarget(me, _target);
            if (dist > (_trapRange + 0.9f))
            {
                _state = TamingState.ApproachTrapRange;
                _stateUntil = Time.time + 10.0f;
                return true;
            }

            if (!IsTrapEquipped(me, _config.TrapName, out _))
            {
                _state = TamingState.EquipTrap;
                _stateUntil = Time.time + 5.0f;
                return true;
            }

            Vector3 point = GetTrapAimPoint(me, _target, out string aimInfo);
            SafeStopActor(me);
            FacePoint(me, point);
            TrySetTarget(me, _target);

            if (!_trapThrowPrepared)
            {
                _trapThrowPrepared = true;
                _nextActionAt = Time.time + 0.05f;
                _log($"[TAMING] preparando lançamento da trap alvo='{SafeName(_target)}' aim={aimInfo} point=({point.x:F1},{point.z:F1}) dist={dist:F1}");
                return true;
            }

            if (Time.time >= _nextActionAt)
            {
                _nextActionAt = Time.time + 0.75f;
                _trapAttempts++;
                _cmdSkillToPoint(me, point);
                _lastTrapThrowAt = Time.time;
                _state = TamingState.ConfirmTrap;
                _stateUntil = Time.time + 3.8f;
                _log($"[TAMING] throw trap attempt={_trapAttempts} alvo='{SafeName(_target)}' aim={aimInfo} point=({point.x:F1},{point.z:F1}) dist={dist:F1}");
            }
            return true;
        }

        private bool TickConfirmTrap(WTPlayer me)
        {
            if (!EnsureTargetStillValid()) return false;

            SafeStopActor(me);
            TrySetTarget(me, _target);

            if (TargetHasTrapEffect(_target, out string effects))
            {
                _state = TamingState.ApproachCatch;
                _stateUntil = Time.time + 8.0f;
                _nextMoveLogAt = 0f;
                _log($"[TAMING] trap confirmada no alvo='{SafeName(_target)}' effects=[{effects}] -> aproximando para Catching");
                return true;
            }

            if (Time.time > _stateUntil)
            {
                if (_trapAttempts < _config.MaxTrapAttempts)
                {
                    _log($"[TAMING] trap não confirmou. retry {_trapAttempts}/{_config.MaxTrapAttempts}.");
                    _state = TamingState.ApproachTrapRange;
                    _stateUntil = Time.time + 10.0f;
                    return true;
                }

                Fail($"trap não confirmou após {_trapAttempts} tentativa(s)");
                return false;
            }

            float dist = DistanceToTarget(me, _target);
            if (dist > (_trapRange + 1.5f))
            {
                _moveToXZ(me, _target.transform.position.x, _target.transform.position.z);
            }
            return true;
        }

        private bool TickApproachCatch(WTPlayer me)
        {
            if (!EnsureTargetStillValid()) return false;
            if (Time.time > _stateUntil) { Fail("timeout aproximando para catching"); return false; }

            float dist = DistanceToTarget(me, _target);
            float desired = Mathf.Max(MIN_CATCH_DISTANCE, _catchRange - CATCH_DISTANCE_BUFFER);
            bool snared = TargetHasTrapEffect(_target, out string effects);
            if (!snared && Time.time > (_lastTrapThrowAt + 1.25f) && dist > desired)
            {
                if (_trapAttempts < _config.MaxTrapAttempts)
                {
                    _log($"[TAMING] efeito da trap sumiu antes do Catching. effects=[{effects}] retry trap.");
                    _state = TamingState.ApproachTrapRange;
                    _stateUntil = Time.time + 10.0f;
                    return true;
                }
            }

            if (dist > desired)
            {
                if (Time.time >= _nextMoveLogAt)
                {
                    _nextMoveLogAt = Time.time + 0.8f;
                    _log($"[TAMING] aproximando para Catching alvo='{SafeName(_target)}' dist={dist:F2} desired<={desired:F2} effects=[{effects}]");
                }
                _moveToXZ(me, _target.transform.position.x, _target.transform.position.z);
                return true;
            }

            _state = TamingState.UseCatching;
            _stateUntil = Time.time + 4.0f;
            _nextActionAt = 0f;
            return true;
        }

        private bool TickUseCatching(WTPlayer me)
        {
            if (!EnsureTargetStillValid()) return false;
            if (_catchSkillIndex < 0) { Fail("skill Catching não encontrada"); return false; }
            if (Time.time > _stateUntil) { Fail("timeout antes do Catching"); return false; }

            if (Time.time >= _nextActionAt)
            {

                if (_catchAttempts >= MAX_CATCH_ATTEMPTS)
                {
                    Fail($"limite de tentativas de Catching atingido ({_catchAttempts}/{MAX_CATCH_ATTEMPTS})");
                    return false;
                }


                _nextActionAt = Time.time + 0.25f;
                _catchAttempts++;

                TrySetTarget(me, _target);
                _tryUseSkill(me, _catchSkillIndex);
                _state = TamingState.ConfirmCatch;
                _stateUntil = Time.time + 7.0f;
                _log($"[TAMING] usando Catching attempt={_catchAttempts}/{MAX_CATCH_ATTEMPTS} idx={_catchSkillIndex} alvo='{SafeName(_target)}' dist={DistanceToTarget(me, _target):F2}");
            }
            return true;
        }

        private bool TickConfirmCatch(WTPlayer me)
        {
            int afterCount = CountInventoryItemByName(me, _expectedCatchItemName);
            bool itemGained = !string.IsNullOrWhiteSpace(_expectedCatchItemName) && afterCount > _expectedCatchItemCount;
            bool targetAlive = IsValidTarget(_target);

            if (itemGained)
            {
                Success($"pet '{_expectedCatchItemName}' capturado. before={_expectedCatchItemCount} after={afterCount}");
                return false;
            }

            if (!targetAlive && Time.time > (_lastTrapThrowAt + 0.25f))
            {
                Success($"alvo removido após Catching. item esperado='{_expectedCatchItemName}' before={_expectedCatchItemCount} after={afterCount}");
                return false;
            }

            if (Time.time > _stateUntil)
            {
                if (targetAlive && _catchAttempts < MAX_CATCH_ATTEMPTS)
                {
                    _state = TamingState.ApproachCatch;
                    _stateUntil = Time.time + 6.0f;
                    _nextActionAt = 0f;
                    _nextMoveLogAt = 0f;
                    _log($"[TAMING] Catching não confirmou. retry {_catchAttempts}/{MAX_CATCH_ATTEMPTS} alvo='{SafeName(_target)}'");
                    return true;
                }

                Fail($"Catching não confirmou após {_catchAttempts}/{MAX_CATCH_ATTEMPTS} tentativa(s). targetAlive={targetAlive} item='{_expectedCatchItemName}' before={_expectedCatchItemCount} after={afterCount}");
                return false;
            }

            return true;
        }

        private void Success(string message)
        {
            _log($"[TAMING] SUCCESS {message}");
            CleanupActorContext(success: true);
            ResetRun(clearBlacklist: false);
        }

        private void Fail(string reason)
        {
            _log($"[TAMING] FAIL {reason}");
            BlacklistCurrentTarget(20f);
            CleanupActorContext(success: false);
            ResetRun(clearBlacklist: false);
        }

        private void BlacklistCurrentTarget(float seconds)
        {
            if (_target == null) return;
            int id = SafeWorldId(_target);
            if (id == 0) return;
            _blacklistUntil[id] = Time.time + Mathf.Max(1f, seconds);
        }

        private void CleanupExpiredBlacklist()
        {
            if (_blacklistUntil.Count == 0) return;
            var expired = _blacklistUntil.Where(kv => kv.Value <= Time.time).Select(kv => kv.Key).ToList();
            foreach (var key in expired) _blacklistUntil.Remove(key);
        }

        private bool EnsureTargetStillValid()
        {
            if (IsValidTarget(_target)) return true;
            if (IsInAggressiveCombatPhase)
            {
                Fail("alvo morreu ou sumiu antes da fuga");
                return false;
            }
            Fail("target perdido");
            return false;
        }

        private void ResetRun(bool clearBlacklist)
        {
            _state = TamingState.Patrol;
            _target = null;
            _stateUntil = 0f;
            _nextActionAt = 0f;
            _nextMoveLogAt = 0f;
            _trapAttempts = 0;
            _catchAttempts = 0;
            _trapRange = 7.5f;
            _catchRange = 1.5f;
            _combatRange = 1.5f;
            _combatIsRanged = false;
            _catchSkillIndex = -1;
            _expectedCatchItemName = "";
            _expectedCatchItemCount = 0;
            _trapEquipLatched = false;
            _trapThrowPrepared = false;
            _lastAggroAttackAt = 0f;
            _watchingForFlee = false;
            _fleeHpThreshold = -1f;
            _extraHitsAfterThreshold = 0;
            _pausedByDefense = false;
            _pauseReason = "";
            _nextScanAt = Mathf.Max(_nextScanAt, Time.time + 0.15f);
            if (clearBlacklist) _blacklistUntil.Clear();
        }


        private void CleanupActorContext(bool success)
        {
            try
            {
                if (_lastActor != null)
                {
                    SafeStopActor(_lastActor);
                    _lastActor.target = null;
                }
            }
            catch { }

            _reacquireBlockedUntil = Time.time + (success ? 1.25f : 0.65f);
            _nextScanAt = Mathf.Max(_nextScanAt, _reacquireBlockedUntil);
        }

        private void SafeStopActor(WTPlayer me)
        {
            try
            {
                if (me != null && me.agent != null && me.agent.enabled)
                {
                    if (me.agent.isOnNavMesh) me.agent.isStopped = true;
                    me.agent.velocity = Vector3.zero;
                    me.agent.ResetPath();
                }
            }
            catch { }
        }

        private void FacePoint(WTPlayer me, Vector3 point)
        {
            try
            {
                if (me == null) return;
                Vector3 flat = point;
                flat.y = me.transform.position.y;
                me.transform.LookAt(flat);
                me.transform.eulerAngles = new Vector3(0f, me.transform.eulerAngles.y, 0f);
            }
            catch { }
        }

        private Vector3 GetCombatAimPoint(WTPlayer me, WTMob mob, out string aimInfo)
        {
            aimInfo = "transform";
            Vector3 point = mob != null ? (mob.transform.position + Vector3.up * 1.0f) : Vector3.zero;

            try
            {
                var col = mob != null ? mob.GetComponent<Collider>() : null;
                if (col != null)
                {
                    point = col.bounds.center;
                    aimInfo = "collider.center";
                }
            }
            catch { }

            try
            {
                if (mob != null && mob.agent != null)
                {
                    Vector3 vel = mob.agent.velocity;
                    Vector3 velFlat = new Vector3(vel.x, 0f, vel.z);
                    float speed = velFlat.magnitude;
                    if (speed > 0.10f)
                    {
                        float leadFactor = Mathf.Clamp(DistanceToTarget(me, mob) / Mathf.Max(_combatRange, 1f), 0.10f, 0.35f);
                        point += velFlat * leadFactor;
                        aimInfo += $"+lead({leadFactor:F2})";
                    }
                }
            }
            catch { }

            return point;
        }

        private Vector3 GetTrapAimPoint(WTPlayer me, WTMob mob, out string aimInfo)
        {
            aimInfo = "transform";
            Vector3 point = mob != null ? (mob.transform.position + Vector3.up * 0.8f) : Vector3.zero;

            try
            {
                var col = mob != null ? mob.GetComponent<Collider>() : null;
                if (col != null)
                {
                    point = col.bounds.center;
                    aimInfo = "collider.center";
                }
            }
            catch { }

            try
            {
                if (mob != null && mob.agent != null)
                {
                    Vector3 vel = mob.agent.velocity;
                    Vector3 velFlat = new Vector3(vel.x, 0f, vel.z);
                    float speed = velFlat.magnitude;
                    if (speed > 0.15f)
                    {
                        float leadFactor = Mathf.Clamp(DistanceToTarget(me, mob) / Mathf.Max(_trapRange, 1f), 0.08f, 0.22f);
                        point += velFlat * leadFactor;
                        aimInfo += $"+lead({leadFactor:F2})";
                    }
                }
            }
            catch { }

            return point;
        }

        private WTMob FindBestTarget(WTPlayer me)
        {
            Vector3 myPos = me.transform.position;
            WTMob best = null;
            float bestDist = float.MaxValue;

            foreach (var mob in UnityEngine.Object.FindObjectsOfType<WTMob>())
            {
                if (!IsValidTarget(mob)) continue;
                if (!IsMatchingConfiguredTarget(mob)) continue;
                if (!IsCatchable(mob)) continue;
                if (!IsModeCompatible(mob)) continue;

                int worldId = SafeWorldId(mob);
                if (worldId != 0 && _blacklistUntil.TryGetValue(worldId, out float until) && until > Time.time) continue;

                float dist = Vector3.Distance(myPos, mob.transform.position);
                if (dist > _config.SearchRadius) continue;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = mob;
                }
            }

            return best;
        }

        private bool IsModeCompatible(WTMob mob)
        {
            if (mob?.entityType?.mobBehaviour == null) return true;

            bool requiresFleeBeforeTrap = RequiresFleeBeforeTrap(mob);

            if (string.Equals(_config.Mode, "PACIFICO", StringComparison.OrdinalIgnoreCase))
                return !requiresFleeBeforeTrap;

            if (string.Equals(_config.Mode, "AGRESSIVO", StringComparison.OrdinalIgnoreCase))
                return requiresFleeBeforeTrap;

            return true;
        }

        private bool RequiresFleeBeforeTrap(WTMob mob)
        {
            try
            {
                var behaviour = mob?.entityType?.mobBehaviour;
                if (behaviour == null) return false;
                // Para o Taming, "agressivo" significa: precisa apanhar antes de fugir.
                // Isso inclui os vermelhos e também os "laranjas" (Neutral/Fearful com skills),
                // desde que tenham trigger de fuga por vida e distância de fear configurados.


                return behaviour.triggerHealthPercent > 0f
                    && behaviour.fearDistance > 0f
                    && SafeHasDamageSkills(mob);
            }
            catch
            {
                return false;
            }
        }

        private bool SafeHasDamageSkills(WTMob mob)
        {
            try { return mob != null && mob.HasDamageSkills(); }
            catch { }

            try
            {
                var et = mob != null ? mob.entityType : null;
                return et != null && et.skills != null && et.skills.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private bool IsMatchingConfiguredTarget(WTMob mob)
        {
            if (mob == null || _config.TargetNames == null || _config.TargetNames.Count == 0) return false;

            string mobName = NormalizeToken(SafeName(mob));
            string typeName = NormalizeToken(mob.entityType != null ? (mob.entityType.name ?? "") : "");

            foreach (var token in _config.TargetNames)
            {
                if (string.IsNullOrWhiteSpace(token)) continue;
                if (mobName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (!string.IsNullOrWhiteSpace(typeName) && typeName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private bool IsCatchable(WTMob mob)
        {
            try { return mob != null && mob.entityType != null && mob.entityType.catchType != null && mob.entityType.catchType.item != null; }
            catch { return false; }
        }

        private string GetExpectedCatchItemName(WTMob mob)
        {
            try { return mob?.entityType?.catchType?.item?.name ?? ""; }
            catch { return ""; }
        }

        private float GetFleeHealthThreshold(WTMob mob)
        {
            try { return mob?.entityType?.mobBehaviour != null ? Mathf.Clamp01(mob.entityType.mobBehaviour.triggerHealthPercent) : -1f; }
            catch { return -1f; }
        }

        private float GetTargetHealthPercent(WTMob mob)
        {
            try { return mob != null ? Mathf.Clamp01(mob.HealthPercent()) : 0f; }
            catch { return 0f; }
        }

        private bool TargetIsRunning(WTMob mob)
        {
            try { return mob != null && string.Equals(mob.state, "RUNNING", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }


        private bool TargetInFear(WTMob mob)
        {
            try
            {
                return mob != null && mob.InFear();
            }
            catch
            {
                return false;
            }
        }

        private bool HasConfirmedFlee(WTMob mob)
        {
            if (mob == null) return false;
            // Teste: trocar para a trap assim que aparecer Slowdown OU quando o HP cair para <= 10%.
            bool hasSlowdown = false;
            try
            {

             hasSlowdown = mob.IsEffectPresent("Slowdown") || mob.IsEffectPresent("SlowDown");


            }
            catch { hasSlowdown = false; }

            if (hasSlowdown) return true;
            float hp = GetTargetHealthPercent(mob);
            return (_fleeHpThreshold > 0f && hp <= _fleeHpThreshold);

        }



        private float GetAggressiveEngageDistance()
        {
            if (_combatIsRanged) return Mathf.Max(2.5f, _combatRange - 0.4f);
            return Mathf.Max(1.0f, _combatRange);
        }

        private float GetCombatWeaponRange(WTPlayer me, string weaponName, float fallback, out bool isRanged)
        {
            isRanged = false;
            weaponName = NormalizeToken(weaponName);
            if (TryGetCombatWeaponRangeFromEquipped(me, weaponName, out float range, out isRanged)) return range;
            if (TryGetCombatWeaponRangeFromInventory(me, weaponName, out range, out isRanged)) return range;
            return fallback;
        }

        private bool TryGetCombatWeaponRangeFromEquipped(WTPlayer me, string weaponName, out float range, out bool isRanged)
        {
            range = 0f;
            isRanged = false;
            try
            {
                var right = me.GetEquippedRightHand();
                if (!right.HasValue || right.Value.data == null) return false;
                if (!string.IsNullOrWhiteSpace(weaponName) && right.Value.data.name.IndexOf(weaponName, StringComparison.OrdinalIgnoreCase) < 0) return false;
                WTWeaponItem weapon = right.Value.data as WTWeaponItem;
                if (weapon == null || weapon.IsTrap()) return false;
                isRanged = weapon.requiredAmmo != null;
                if (weapon.damageSkill != null) range = weapon.damageSkill.baseCastRange;
                if (range <= 0f) range = isRanged ? 10f : 1.0f;
                return range > 0f;
            }
            catch { return false; }
        }

        private bool TryGetCombatWeaponRangeFromInventory(WTPlayer me, string weaponName, out float range, out bool isRanged)
        {
            range = 0f;
            isRanged = false;
            if (me == null || me.inventory == null) return false;
            foreach (var slot in me.inventory)
            {
                if (slot.amount <= 0 || slot.item.data == null) continue;
                string itemName = slot.item.data.name ?? "";
                if (!string.IsNullOrWhiteSpace(weaponName) && itemName.IndexOf(weaponName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                WTWeaponItem weapon = slot.item.data as WTWeaponItem;
                if (weapon == null || weapon.IsTrap()) continue;
                isRanged = weapon.requiredAmmo != null;
                if (weapon.damageSkill != null) range = weapon.damageSkill.baseCastRange;
                if (range <= 0f) range = isRanged ? 10f : 1.0f;
                return range > 0f;
            }
            return false;
        }

        private bool IsCombatWeaponEquipped(WTPlayer me, string weaponName, out string handInfo, out float range, out bool isRanged)
        {
            handInfo = "none";
            range = 0f;
            isRanged = false;
            try
            {
                var right = me.GetEquippedRightHand();
                if (!right.HasValue || right.Value.data == null)
                {
                    handInfo = "empty";
                    return false;
                }

                string itemName = right.Value.data.name ?? "";
                WTWeaponItem weapon = right.Value.data as WTWeaponItem;
                bool isWeapon = weapon != null;
                bool isTrap = weapon != null && weapon.IsTrap();
                bool broken = false;
                try { broken = right.Value.IsBroken(); } catch { }
                if (isWeapon)
                {
                    isRanged = weapon.requiredAmmo != null;
                    if (weapon.damageSkill != null) range = weapon.damageSkill.baseCastRange;
                    if (range <= 0f) range = isRanged ? 10f : 1.0f;
                }

                handInfo = $"name='{itemName}' weapon={isWeapon} trap={isTrap} broken={broken} range={range:F1} ranged={isRanged}";

                if (!isWeapon || isTrap || broken) return false;
                if (string.IsNullOrWhiteSpace(weaponName)) return true;
                return itemName.IndexOf(weaponName, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception ex)
            {
                handInfo = "error=" + ex.Message;
                return false;
            }
        }

        private float GetTrapRange(WTPlayer me, string trapName, float fallback)
        {
            trapName = NormalizeToken(trapName);
            if (TryGetTrapRangeFromEquipped(me, trapName, out float range)) return range;
            if (TryGetTrapRangeFromInventory(me, trapName, out range)) return range;
            return fallback;
        }

        private bool TryGetTrapRangeFromEquipped(WTPlayer me, string trapName, out float range)
        {
            range = 0f;
            try
            {
                var right = me.GetEquippedRightHand();
                if (!right.HasValue || right.Value.data == null) return false;
                if (right.Value.data.name.IndexOf(trapName, StringComparison.OrdinalIgnoreCase) < 0) return false;
                WTWeaponItem weapon = right.Value.data as WTWeaponItem;
                if (weapon == null || !weapon.IsTrap() || weapon.damageSkill == null) return false;
                range = weapon.damageSkill.baseCastRange;
                return range > 0f;
            }
            catch { return false; }
        }

        private bool TryGetTrapRangeFromInventory(WTPlayer me, string trapName, out float range)
        {
            range = 0f;
            if (me == null || me.inventory == null) return false;
            foreach (var slot in me.inventory)
            {
                if (slot.amount <= 0 || slot.item.data == null) continue;
                string itemName = slot.item.data.name ?? "";
                if (itemName.IndexOf(trapName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                WTWeaponItem weapon = slot.item.data as WTWeaponItem;
                if (weapon == null || !weapon.IsTrap() || weapon.damageSkill == null) continue;
                range = weapon.damageSkill.baseCastRange;
                return range > 0f;
            }
            return false;
        }

        private bool IsTrapEquipped(WTPlayer me, string trapName, out string handInfo)
        {
            handInfo = "none";
            try
            {
                var right = me.GetEquippedRightHand();
                if (!right.HasValue || right.Value.data == null)
                {
                    handInfo = "empty";
                    return false;
                }

                string itemName = right.Value.data.name ?? "";
                WTWeaponItem weapon = right.Value.data as WTWeaponItem;
                bool isTrap = weapon != null && weapon.IsTrap();
                bool broken = false;
                bool hasMax = false;
                int dur = -1;
                try { broken = right.Value.IsBroken(); } catch { }
                try { hasMax = right.Value.HasMaxDurability() > 0; } catch { }
                try { dur = right.Value.GetDurability(); } catch { }

                handInfo = $"name='{itemName}' trap={isTrap} broken={broken} dur={dur} hasMax={hasMax}";

                if (string.IsNullOrWhiteSpace(trapName))
                    return isTrap && !broken;

                if (itemName.IndexOf(trapName, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;

                return isTrap && !broken;
            }
            catch (Exception ex)
            {
                handInfo = "error=" + ex.Message;
                return false;
            }
        }

        private int FindSkillIndexByName(WTPlayer me, string skillName)
        {
            if (me == null || me.skills == null || string.IsNullOrWhiteSpace(skillName)) return -1;
            for (int i = 0; i < me.skills.Count; i++)
            {
                var sk = me.skills[i];
                try
                {
                    if (sk.hash != 0 && string.Equals(sk.name, skillName, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
                catch { }
            }

            return -1;
        }

        private float GetSkillCastRange(WTPlayer me, int skillIndex, float fallback)
        {
            try
            {
                if (me == null || me.skills == null || skillIndex < 0 || skillIndex >= me.skills.Count) return fallback;
                var sk = me.skills[skillIndex];
                if (sk.hash == 0) return fallback;
                return Mathf.Max(0.5f, sk.castRange);


            }
            catch { return fallback; }
        }

        private bool TargetHasTrapEffect(WTMob mob, out string effects)
        {
            effects = "";
            if (mob == null) return false;

            bool snare = false;
            bool slowdown = false;
            try { snare = mob.IsEffectPresent("Snare"); } catch { }
            try { slowdown = mob.IsEffectPresent("Slowdown"); } catch { }

            try
            {
                var names = new List<string>();
                foreach (var e in mob.effects)
                {
                    string n = e.name ?? e.ToString();
                    if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
                }

                effects = string.Join(",", names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            }
            catch { }

            return snare || slowdown;
        }

        private int CountInventoryItemByName(WTPlayer me, string itemName)
        {
            if (me == null || me.inventory == null || string.IsNullOrWhiteSpace(itemName)) return 0;
            int total = 0;
            foreach (var slot in me.inventory)
            {
                if (slot.amount <= 0 || slot.item.data == null) continue;
                if (string.Equals(slot.item.data.name ?? "", itemName, StringComparison.OrdinalIgnoreCase))
                    total += slot.amount;
            }
            return total;
        }

        private float DistanceToTarget(WTPlayer me, WTMob mob)
        {
            if (me == null || mob == null) return 9999f;
            try
            {
                if (me.collider != null && mob.collider != null)
                    return Utils.ClosestDistance(me.collider, mob.collider);
            }
            catch { }
            try { return Vector3.Distance(me.transform.position, mob.transform.position); }
            catch { return 9999f; }
        }

        private bool IsValidTarget(WTMob mob)
        {
            if (mob == null) return false;
            if (!mob.isActiveAndEnabled) return false;
            if (!mob.gameObject.activeInHierarchy) return false;
            if (mob.health <= 0) return false;
            if (string.Equals(mob.state, "DEAD", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private void TrySetTarget(WTPlayer me, WTMob mob)
        {
            if (me == null) return;
            try
            {
                NetworkIdentity ni = (mob != null) ? mob.netIdentity : null;
                _cmdSetTarget(me, ni);
            }
            catch { }
        }

        private int SafeWorldId(WTMob mob)
        {
            try { return mob != null ? mob.worldId : 0; } catch { return 0; }
        }

        private static string SafeName(WTMob mob)
        {
            try { return (mob.name ?? "mob").Replace("(Clone)", "").Trim(); }
            catch { return "mob"; }
        }

        private static string NormalizeToken(string s)
        {
            if (s == null) return "";
            s = s.Trim();
            s = s.Replace("\uFEFF", "")
                 .Replace("\u200B", "")
                 .Replace("\u200E", "")
                 .Replace("\u200F", "")
                 .Replace("\u00A0", " ");
            return s.Trim();
        }
    }
}
