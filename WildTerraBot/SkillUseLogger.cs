using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace WildTerraBot
{
    /// <summary>
    /// Log automático de skills usadas pelo jogador local.
    /// Objetivo: descobrir o nome real (me.skills[i].name) quando você usa manualmente uma skill no jogo.
    ///
    /// Importante: NÃO altera comportamento do jogo, apenas loga.
    /// </summary>
    [HarmonyPatch]
    public static class SkillUseLogger
    {
        // Alguns builds têm TryUseSkill em WTPlayer, outros em Player.
        // Patchamos por reflexão (Harmony TargetMethod).
        static MethodBase TargetMethod()
        {
            // Preferência: WTPlayer.TryUseSkill(int, bool, bool)
            var wt = AccessTools.TypeByName("WTPlayer");
            if (wt != null)
            {
                var m = AccessTools.Method(wt, "TryUseSkill", new Type[] { typeof(int), typeof(bool), typeof(bool) });
                if (m != null) return m;
                // fallback: WTPlayer.TryUseSkill(int)
                m = AccessTools.Method(wt, "TryUseSkill", new Type[] { typeof(int)});
                if (m != null) return m;
            }

            // Fallback: Player.TryUseSkill(...)
            var pl = AccessTools.TypeByName("Player");
            if (pl != null)
            {
                    var m = AccessTools.Method(pl, "TryUseSkill", new Type[] { typeof(int), typeof(bool), typeof(bool) });
                    if (m != null) return m;
                    m = AccessTools.Method(pl, "TryUseSkill", new Type[] { typeof(int) });
                    if (m != null) return m;
            }

            return null;
        }

        // Prefix para capturar antes do método (já sabemos o índice pedido).
        static void Prefix(object __instance, int skillIndex)
        {
                try
                {
                        var player = __instance as Player;
                        if (player == null || !player.isLocalPlayer) return;
        
                        string skillName = "?";
                        bool ready = false;
                        double cdLeft = -1.0;
        
                        try
                        {
                                if (player.skills != null && skillIndex >= 0 && skillIndex < player.skills.Count)
                                {
                                        var s = player.skills[skillIndex];
                                        skillName = s.name ?? "";
                                        try { ready = s.IsReady(); } catch { }
                
                                        // cooldownEnd pode ser float ou double dependendo do build/versão.
                                        try
                                        {
                                                object ceObj = s.cooldownEnd;
                                                double ce;
                                                if (ceObj is float f) ce = f;
                                                else if (ceObj is double d) ce = d;
                                                else ce = Convert.ToDouble(ceObj);
                    
                                                cdLeft = Math.Max(0.0, ce - Time.time);
                                        }
                                        catch { }
                                }
                        }
                        catch { }
        
                        string cdTxt = (cdLeft >= 0.0) ? cdLeft.ToString("F1") : "?";
                        string line = $"[SKILL-USE] idx={skillIndex} name='{skillName}' ready={ready} cdLeft={cdTxt}s";
        
                        try
                        {
                                if (WTSocketBot.PublicLogger != null) WTSocketBot.PublicLogger.LogInfo(line);
                                else Debug.Log(line);
                        }
                        catch
                        {
                            Debug.Log(line);
                        }
                }
                catch { }
        }
    }
}
