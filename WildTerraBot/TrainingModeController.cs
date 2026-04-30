using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace WildTerraBot
{
    /// <summary>
    /// Modo Treinamento (independente / exclusivo).
    ///
    /// Regras desta versão:
    /// - roda somente DESMONTADO
    /// - prioridade: Recovery > BuffItems > Skills > AutoAttack
    /// - skills por nome interno (ordem = prioridade fixa)
    /// - consumíveis com buff são monitorados pelos useEffects do item
    /// - recuperação HP/SP usa itens configurados no formato HP:Item / SP:Item
    /// </summary>
    public class TrainingModeController
    {
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logWarn;
        private readonly Action<WTPlayer, Vector3> _attackToPoint;

        private bool _enabled = false;
        private TrainingModeConfig _cfg = null;
        private float _nextTick = 0f;
        private float _nextActionAt = 0f;
        private float _nextMountedLogAt = 0f;
        private float _nextAutoAttackAt = 0f;
        private float _nextMissingTargetLogAt = 0f;

        private const float AUTO_ATTACK_INTERVAL = 0.30f;
        private const float AUTO_ATTACK_SEARCH_RADIUS = 30f;

        public bool IsEnabled => _enabled;

        public TrainingModeController(Action<string> logInfo, Action<string> logWarn, Action<WTPlayer, Vector3> attackToPoint)
        {
            _logInfo = logInfo ?? (_ => { });
            _logWarn = logWarn ?? (_ => { });
            _attackToPoint = attackToPoint ?? ((player, point) => { });
        }

        public void Enable(TrainingModeConfig cfg)
        {
            _cfg = (cfg ?? new TrainingModeConfig()).Normalize();
            _enabled = true;
            _nextTick = 0f;
            _nextActionAt = 0f;
            _nextMountedLogAt = 0f;
            _nextAutoAttackAt = 0f;
            _nextMissingTargetLogAt = 0f;
            _logInfo("[TRAINING] ENABLED | " + _cfg.BuildSummary());
        }

        public void Disable()
        {
            if (_enabled)
                _logInfo("[TRAINING] DISABLED");

            _enabled = false;
            _cfg = null;
            _nextTick = 0f;
            _nextActionAt = 0f;
            _nextMountedLogAt = 0f;
            _nextAutoAttackAt = 0f;
            _nextMissingTargetLogAt = 0f;
        }

        public void Tick(WTPlayer me)
        {
            if (!_enabled || _cfg == null || me == null) return;
            if (Time.time < _nextTick) return;
            _nextTick = Time.time + 0.20f;

            if (Time.time < _nextActionAt) return;
            if (me.IsStateStunnedOrDeadOrFaintOrTrading()) return;
            if (me.IsStateCasting()) return;

            if (me.IsMounted())
            {
                if (Time.time >= _nextMountedLogAt)
                {
                    _nextMountedLogAt = Time.time + 5.0f;
                    _logInfo("[TRAINING] aguardando desmontar para agir");
                }
                return;
            }

            _nextMountedLogAt = 0f;

            if (_cfg.EnableRecovery && TryUseRecoveryItem(me))
            {
                _nextActionAt = Time.time + 0.90f;
                return;
            }

            if (_cfg.EnableBuffItems && TryUseBuffItem(me))
            {
                _nextActionAt = Time.time + 0.90f;
                return;
            }

            if (_cfg.EnableSkills && TryUseReadySkill(me))
            {
                _nextActionAt = Time.time + 0.25f;
                return;
            }

            if (_cfg.EnableAutoAttack)
                TryPulseAutoAttack(me);
        }

        private bool TryUseRecoveryItem(WTPlayer me)
        {
            float hpPct = me.HealthPercent() * 100f;
            float spPct = me.StaminaPercent() * 100f;

            bool needsHp = hpPct < _cfg.HpThreshold;
            bool needsSp = spPct < _cfg.SpThreshold;
            if (!needsHp && !needsSp) return false;

            foreach (var entry in _cfg.RecoveryItems)
            {
                if (entry == null) continue;
                if (entry.IsHp && !needsHp) continue;
                if (entry.IsSp && !needsSp) continue;

                int inventoryIndex;
                WTUsableItem usable;
                if (!TryFindUsableInventoryItem(me, entry.ItemName, out inventoryIndex, out usable))
                    continue;

                if (usable == null || usable.foods == null || usable.foods.Length == 0)
                    continue;

                FoodType wanted = entry.IsHp ? FoodType.Health : FoodType.Stamina;
                if (!usable.foods.Any(f => f.type == wanted && f.value > 0))
                    continue;

                try
                {
                    me.CmdUseInventoryItem(inventoryIndex);
                    _logInfo($"[TRAINING] recovery use {entry.ResourceType}:{usable.name} idx={inventoryIndex} hp={hpPct:F0}% sp={spPct:F0}%");
                    return true;
                }
                catch (Exception ex)
                {
                    _logWarn($"[TRAINING] recovery error item='{entry.ItemName}' idx={inventoryIndex} ex={ex.GetType().Name}:{ex.Message}");
                    return false;
                }
            }

            return false;
        }

        private bool TryUseBuffItem(WTPlayer me)
        {
            foreach (string itemName in _cfg.BuffItemNames)
            {
                int inventoryIndex;
                WTUsableItem usable;
                if (!TryFindUsableInventoryItem(me, itemName, out inventoryIndex, out usable))
                    continue;

                if (usable == null || usable.useEffects == null || usable.useEffects.Length == 0)
                    continue;

                if (!NeedsRefresh(me, usable, _cfg.BuffRefreshSeconds))
                    continue;

                try
                {
                    me.CmdUseInventoryItem(inventoryIndex);
                    _logInfo($"[TRAINING] buff use {usable.name} idx={inventoryIndex}");
                    return true;
                }
                catch (Exception ex)
                {
                    _logWarn($"[TRAINING] buff error item='{itemName}' idx={inventoryIndex} ex={ex.GetType().Name}:{ex.Message}");
                    return false;
                }
            }

            return false;
        }

        private bool TryUseReadySkill(WTPlayer me)
        {
            for (int i = 0; i < _cfg.SkillNames.Count; i++)
            {
                string wanted = _cfg.SkillNames[i];
                if (string.IsNullOrWhiteSpace(wanted)) continue;

                int idx = -1;
                try
                {
                    idx = me.skills.FindIndex(s => string.Equals((s.name ?? "").Trim(), wanted.Trim(), StringComparison.OrdinalIgnoreCase));
                }
                catch { idx = -1; }

                if (idx < 0) continue;

                Skill skill = me.skills[idx];
                if (!skill.IsReady()) continue;

                try
                {
                    me.TryUseSkill(idx, ignoreState: false, actionBarMouseClick: false);
                    _logInfo($"[TRAINING] skill try {skill.name} idx={idx}");
                    return true;
                }
                catch (Exception ex)
                {
                    _logWarn($"[TRAINING] skill error name='{wanted}' idx={idx} ex={ex.GetType().Name}:{ex.Message}");
                    return false;
                }
            }

            return false;
        }


        private void TryPulseAutoAttack(WTPlayer me)
        {
            if (me == null) return;
            if (Time.time < _nextAutoAttackAt) return;
            if (string.IsNullOrWhiteSpace(_cfg.AutoAttackTargetName)) return;

            WTMob target = FindNearestMobByName(me, _cfg.AutoAttackTargetName, AUTO_ATTACK_SEARCH_RADIUS);
            if (target == null)
            {
                if (Time.time >= _nextMissingTargetLogAt)
                {
                    _nextMissingTargetLogAt = Time.time + 5.0f;
                    _logInfo($"[TRAINING] auto attack aguardando alvo '{_cfg.AutoAttackTargetName}'");
                }
                return;
            }

            _nextMissingTargetLogAt = 0f;
            _nextAutoAttackAt = Time.time + AUTO_ATTACK_INTERVAL;

            try
            {
                _attackToPoint(me, target.transform.position);
            }
            catch (Exception ex)
            {
                _logWarn($"[TRAINING] auto attack error alvo='{_cfg.AutoAttackTargetName}' ex={ex.GetType().Name}:{ex.Message}");
            }
        }

        private static WTMob FindNearestMobByName(WTPlayer me, string targetName, float radius)
        {
            if (me == null || string.IsNullOrWhiteSpace(targetName)) return null;

            string wanted = NormalizeMobName(targetName);
            float bestSqr = radius * radius;
            WTMob best = null;
            Vector3 myPos = me.transform.position;

            foreach (var mob in UnityEngine.Object.FindObjectsOfType<WTMob>())
            {
                if (!IsValidMob(mob)) continue;

                string mobName = NormalizeMobName(mob.name);
                if (!string.Equals(mobName, wanted, StringComparison.OrdinalIgnoreCase))
                    continue;

                float sqr = (mob.transform.position - myPos).sqrMagnitude;
                if (sqr > bestSqr) continue;

                bestSqr = sqr;
                best = mob;
            }

            return best;
        }

        private static bool IsValidMob(WTMob mob)
        {
            if (mob == null) return false;
            if (!mob.gameObject.activeInHierarchy) return false;
            if (!mob.isActiveAndEnabled) return false;
            if (mob.health <= 0) return false;
            if (string.Equals(mob.state, "DEAD", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static string NormalizeMobName(string value)
        {
            return (value ?? "").Replace("(Clone)", "").Trim();
        }

        private static bool NeedsRefresh(WTPlayer me, WTUsableItem usable, int refreshSeconds)
        {
            if (me == null || usable == null || usable.useEffects == null || usable.useEffects.Length == 0)
                return false;

            bool hasAnyMonitoredEffect = false;

            for (int i = 0; i < usable.useEffects.Length; i++)
            {
                TimedEffect timed = usable.useEffects[i];
                if (timed.effect == null) continue;
                hasAnyMonitoredEffect = true;

                float remaining = GetBestEffectRemaining(me, timed.effect);
                if (remaining <= refreshSeconds)
                    return true;
            }

            return false;
        }

        private static float GetBestEffectRemaining(WTPlayer me, WTScriptableEffect effectType)
        {
            float best = 0f;
            if (me == null || effectType == null || me.effects == null) return best;

            try
            {
                for (int i = 0; i < me.effects.Count; i++)
                {
                    EntityEffect active = me.effects[i];
                    bool nameMatch = string.Equals(active.name, effectType.name, StringComparison.OrdinalIgnoreCase);
                    bool groupMatch = effectType.effectGroup != EffectGroupType.None && active.effectType != null && active.effectType.effectGroup == effectType.effectGroup;
                    if (!nameMatch && !groupMatch) continue;

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
