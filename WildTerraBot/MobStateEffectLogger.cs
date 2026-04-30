using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace WildTerraBot
{
    /// <summary>
    /// Logger de diagnóstico: monitora o TARGET atual (se for WTMob) e imprime mudanças em:
    /// - mob.state
    /// - mob.effects (Entity.effects SyncList)
    ///
    /// NÃO altera comportamento do jogo.
    /// </summary>
    public class MobStateEffectLogger
    {
        private readonly Action<string> _logInfo;
        private readonly float _tickInterval;

        private float _nextTick = 0f;
        private int _lastTargetId = 0;
        private string _lastState = null;
        private string _lastEffectsSig = null;

        public MobStateEffectLogger(Action<string> logInfo, float tickIntervalSeconds = 0.25f)
        {
            _logInfo = logInfo ?? (_ => { });
            _tickInterval = Mathf.Max(0.05f, tickIntervalSeconds);
        }

        public void Tick(WTPlayer me)
        {
            if (me == null) return;
            if (Time.time < _nextTick) return;
            _nextTick = Time.time + _tickInterval;

            var mob = me.target as WTMob;
            if (mob == null || !mob.isActiveAndEnabled) { ResetCache(); return; }

            int id = 0;
            try { id = mob.worldId; } catch { id = mob.GetInstanceID(); }

            string st = "?";
            try { st = mob.state ?? "?"; } catch { }

            string sig = BuildEffectsSignature(mob);

            bool targetChanged = (id != _lastTargetId);
            bool stateChanged = !string.Equals(st, _lastState, StringComparison.Ordinal);
            bool effectsChanged = !string.Equals(sig, _lastEffectsSig, StringComparison.Ordinal);

            if (targetChanged)
            {
                _lastTargetId = id;
                _lastState = st;
                _lastEffectsSig = sig;
                _logInfo($"[TAME-DBG] target='{SafeName(mob)}' id={id} state={st} effects=[{sig}]");
                return;
            }

            if (stateChanged || effectsChanged)
            {
                _lastState = st;
                _lastEffectsSig = sig;
                _logInfo($"[TAME-DBG] target='{SafeName(mob)}' id={id} state={st} effects=[{sig}]");
            }
        }

        private void ResetCache()
        {
            _lastTargetId = 0;
            _lastState = null;
            _lastEffectsSig = null;
        }

        private static string SafeName(WTMob mob)
        {
            try { return (mob.name ?? "mob").Replace("(Clone)", "").Trim(); } catch { return "mob"; }
        }

        private static string BuildEffectsSignature(WTMob mob)
        {
            try
            {
                // Entity.effects fica na base Entity (WTMob herda Entity)
                var ent = mob as Entity;
                if (ent == null) return "";

                var effects = ent.effects;
                if (effects == null) return "";

                // SyncList é IEnumerable; coletamos nomes de cada efeito.
                List<string> names = new List<string>();
                foreach (var e in (IEnumerable)effects)
                {
                    string n = TryGetEffectName(e);
                    if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
                }

                // Ordena para assinatura estável (independente de ordem interna)
                names.Sort(StringComparer.OrdinalIgnoreCase);
                return string.Join(",", names);
            }
            catch { return ""; }
        }

        private static string TryGetEffectName(object effectObj)
        {
            if (effectObj == null) return "";
            try
            {
                // Tentativas comuns: field/property "name", "type", "effect", "id"
                Type t = effectObj.GetType();

                // property 'name'
                var pName = t.GetProperty("name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pName != null)
                {
                    object v = pName.GetValue(effectObj, null);
                    if (v is string s1) return s1;
                }

                // field 'name'
                var fName = t.GetField("name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fName != null)
                {
                    object v = fName.GetValue(effectObj);
                    if (v is string s2) return s2;
                }

                // property 'type'
                var pType = t.GetProperty("type", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pType != null)
                {
                    object v = pType.GetValue(effectObj, null);
                    if (v is string s3) return s3;
                    if (v != null) return v.ToString();
                }

                // field 'type'
                var fType = t.GetField("type", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fType != null)
                {
                    object v = fType.GetValue(effectObj);
                    if (v is string s4) return s4;
                    if (v != null) return v.ToString();
                }

                // fallback
                return effectObj.ToString();
            }
            catch { return ""; }
        }
    }
}
