using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace WildTerraBot
{
    internal sealed class PlayerInspectService
    {
        private const float NearbyRangeMeters = 100f;

        private sealed class PlayerCandidate
        {
            public Player Player;
            public float Distance;
        }

        public bool TryBuildReport(Player localPlayer, string requestedPlayerName, out string resolvedPlayerName, out string report, out string error)
        {
            resolvedPlayerName = "";
            report = "";
            error = "";

            if (localPlayer == null)
            {
                error = "Player local não disponível.";
                return false;
            }

            requestedPlayerName = (requestedPlayerName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(requestedPlayerName))
            {
                error = "Nome do player não informado.";
                return false;
            }

            Player target = FindNearbyPlayer(localPlayer, requestedPlayerName, out resolvedPlayerName, out error);
            if (target == null)
                return false;

            report = BuildReport(localPlayer, target, requestedPlayerName);
            return true;
        }

        private Player FindNearbyPlayer(Player localPlayer, string requestedPlayerName, out string resolvedPlayerName, out string error)
        {
            resolvedPlayerName = "";
            error = "";

            Vector3 myPos = localPlayer.transform.position;

            List<PlayerCandidate> nearbyPlayers = UnityEngine.Object.FindObjectsOfType<Player>()
                .Where(p => p != null && !p.isLocalPlayer && p.health > 0)
                .Select(p => new PlayerCandidate
                {
                    Player = p,
                    Distance = Vector3.Distance(myPos, p.transform.position)
                })
                .Where(x => x.Distance <= NearbyRangeMeters)
                .OrderBy(x => x.Distance)
                .ToList();

            if (nearbyPlayers.Count == 0)
            {
                error = "Nenhum player próximo/visível foi encontrado para inspeção.";
                return null;
            }

            PlayerCandidate exact = nearbyPlayers.FirstOrDefault(x =>
                string.Equals(x.Player.name, requestedPlayerName, StringComparison.OrdinalIgnoreCase));

            if (exact != null)
            {
                resolvedPlayerName = exact.Player.name;
                return exact.Player;
            }

            List<PlayerCandidate> partial = nearbyPlayers
                .Where(x => x.Player.name.IndexOf(requestedPlayerName, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(x => x.Distance)
                .ToList();

            if (partial.Count == 1)
            {
                resolvedPlayerName = partial[0].Player.name;
                return partial[0].Player;
            }

            if (partial.Count > 1)
            {
                error = "Mais de um player corresponde ao nome informado: " +
                        string.Join(", ", partial.Take(8).Select(x => string.Format("{0} ({1:F1}m)", x.Player.name, x.Distance)).ToArray());
                return null;
            }

            error = string.Format("Player '{0}' não encontrado entre os players próximos/visíveis.", requestedPlayerName);
            return null;
        }

        private string BuildReport(Player localPlayer, Player target, string requestedPlayerName)
        {
            StringBuilder sb = new StringBuilder(4096);
            Vector3 myPos = localPlayer.transform.position;
            Vector3 targetPos = target.transform.position;
            float distance = Vector3.Distance(myPos, targetPos);
            WTPlayer wtTarget = target as WTPlayer;

            sb.AppendLine("========================================");
            sb.AppendLine(WildTerraBot.Properties.Resources.InspectReportTitle);
            sb.AppendLine("========================================");
            sb.AppendLine("Consulta: " + Safe(requestedPlayerName));
            sb.AppendLine("Player encontrado: " + Safe(target.name));
            sb.AppendLine("Gerado em: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            sb.AppendLine(WildTerraBot.Properties.Resources.InspectSectionGeneral);
            sb.AppendLine(WildTerraBot.Properties.Resources.InspectLabelName + ": " + Safe(target.name));
            sb.AppendLine(WildTerraBot.Properties.Resources.InspectLabelLevel + ": " + target.level);
            sb.AppendLine("HP: " + target.health);
            sb.AppendLine("SP: " + target.stamina);
            sb.AppendLine("Distância: " + distance.ToString("F1") + "m");
            sb.AppendLine(string.Format("Posição: X={0:F1} | Y={1:F1} | Z={2:F1}", targetPos.x, targetPos.y, targetPos.z));
            sb.AppendLine("Estado: " + Safe(target.state));
            sb.AppendLine("Skill atual: " + GetCurrentSkillName(target));
            sb.AppendLine(WildTerraBot.Properties.Resources.InspectLabelGuild + ": " + GetGuildName(target));
            AppendCosmeticsSection(sb, target);

            try
            {
                if (target.activePet != null)
                {
                    string petName = "-";
                    try
                    {
                        petName = GetLocalizedItemName(target.activePet.petItem);
                    }
                    catch
                    {
                        petName = Safe(target.activePet.name);
                    }

                    sb.AppendLine("Pet ativo: " + Safe(petName) + " | Level=" + target.activePet.level);
                }
            }
            catch { }

            try
            {
                if (target.activeMount != null)
                    sb.AppendLine("Montaria ativa: " + GetLocalizedMountName(target.activeMount) + " | Level=" + target.activeMount.level);
            }
            catch { }

            sb.AppendLine();
            sb.AppendLine("[ECONOMIA]");
            sb.AppendLine("Money: " + target.money);
            sb.AppendLine("Gold: " + target.gold);
            sb.AppendLine();

            sb.AppendLine("[PROGRESSAO]");
            sb.AppendLine("Experience: " + target.experience);
            sb.AppendLine("Experience Max: " + target.experienceMax);
            sb.AppendLine("SkillExperience: " + target.skillExperience);
            sb.AppendLine("Boost Atual: " + target.boost);
            sb.AppendLine("Boost Max: " + target.boostMax);
            sb.AppendLine("Boost Ativo: " + FormatBool(target.isBoostOn));
            sb.AppendLine("XP Progress: " + FormatPercent(target.experience, target.experienceMax));
            sb.AppendLine("Boost Progress: " + FormatPercent(target.boost, target.boostMax));
            sb.AppendLine();

            sb.AppendLine("[ATRIBUTOS]");
            sb.AppendLine("Strength: " + target.strength);
            sb.AppendLine("Intelligence: " + target.intelligence);
            sb.AppendLine();

            sb.AppendLine(WildTerraBot.Properties.Resources.InspectSectionEquipment);
            AppendEquipmentSection(sb, target);
            sb.AppendLine();

            sb.AppendLine("[PROF_SKILLS]");
            AppendProfSkillsSection(sb, wtTarget);
            sb.AppendLine();

            sb.AppendLine("[SKILLS]");
            AppendSkillsSection(sb, target);
            sb.AppendLine();

            sb.AppendLine("[BUFFS]");
            AppendBuffsSection(sb, target);
            sb.AppendLine();

            sb.AppendLine("[OBSERVACOES]");
            sb.AppendLine("- Os dados dependem do que o cliente recebeu do servidor.");
            sb.AppendLine("- A inspeção é limitada a players próximos/visíveis no cliente.");
            sb.AppendLine("- Os nomes localizados dependem da tradução ativa no cliente do jogo.");
            sb.AppendLine("- Skills ativas não expõem um level numérico próprio no struct Skill.");

            return sb.ToString();
        }

        private static void AppendEquipmentSection(StringBuilder sb, Player target)
        {
            if (target == null || target.equipment == null || target.equipment.Count == 0)
            {
                sb.AppendLine("Sem dados de equipamento.");
                return;
            }

            int count = target.equipment.Count;
            for (int i = 0; i < count; i++)
            {
                string slotName = GetEquipmentSlotName(target, i);
                ItemSlot slot = target.equipment[i];

                if (slot.amount <= 0 || slot.item.hash == 0)
                {
                    sb.AppendLine(slotName + ": [vazio]");
                    continue;
                }

                Item item = slot.item;
                string itemName = Safe(GetLocalizedItemName(item));

                sb.AppendLine(slotName + ": " + itemName);
                sb.AppendLine("  Qualidade: " + FormatQuality(item.quality));
                sb.AppendLine("  Raridade: " + item.GetRarity());
                sb.AppendLine("  Durabilidade: " + FormatDurability(item));

                string enchantName = Safe(item.GetEnchantmentName());
                string enchantEffect = Safe(item.GetEnchantmentEffect());

                if (!string.IsNullOrWhiteSpace(enchantName) && enchantName != "-")
                    sb.AppendLine("  Encantamento: " + enchantName);

                if (!string.IsNullOrWhiteSpace(enchantEffect) && enchantEffect != "-")
                    sb.AppendLine("  Efeito Encant.: " + enchantEffect);

                if (item.itemValues != null && item.itemValues.Length > 0)
                {
                    sb.AppendLine("  Valores extras:");
                    foreach (ItemValue iv in item.itemValues)
                    {
                        if (string.IsNullOrWhiteSpace(iv.key) && string.IsNullOrWhiteSpace(iv.value))
                            continue;

                        sb.AppendLine("    - " + Safe(iv.key) + " = " + Safe(iv.value));
                    }
                }
            }
        }

        private static void AppendProfSkillsSection(StringBuilder sb, WTPlayer target)
        {
            if (target == null || target.profSkills == null || target.profSkills.Count == 0)
            {
                sb.AppendLine("Sem dados de profSkills.");
                return;
            }

            foreach (WTProfSkill prof in target.profSkills.OrderBy(x => x.name))
            {
                sb.AppendLine("- " + Safe(GetLocalizedProfSkillName(prof)) + " | Level=" + prof.level + " | XP=" + prof.xp);
            }
        }

        private static void AppendSkillsSection(StringBuilder sb, Player target)
        {
            if (target == null || target.skills == null || target.skills.Count == 0)
            {
                sb.AppendLine("Sem dados de skills.");
                return;
            }

            foreach (Skill skill in target.skills)
            {
                string line = "- " + Safe(GetLocalizedSkillName(skill));

                try
                {
                    WTWeaponSkill weaponSkill = skill.data as WTWeaponSkill;
                    if (weaponSkill != null && !string.IsNullOrWhiteSpace(weaponSkill.profSkillName))
                        line += " | ProfSkill=" + Safe(GetLocalizedProfSkillName(new WTProfSkill(weaponSkill.profSkillName)));
                }
                catch { }

                try
                {
                    WTAbilitySkill abilitySkill = skill.data as WTAbilitySkill;
                    if (abilitySkill != null && abilitySkill.profSkillLevel > 0)
                        line += " | ReqProfLevel=" + abilitySkill.profSkillLevel;
                }
                catch { }

                try
                {
                    float cooldown = skill.CooldownRemaining();
                    if (cooldown > 0.01f)
                        line += " | CD=" + cooldown.ToString("F1") + "s";
                }
                catch { }

                try
                {
                    float cast = skill.CastTimeRemaining();
                    if (cast > 0.01f)
                        line += " | Cast=" + cast.ToString("F1") + "s";
                }
                catch { }

                sb.AppendLine(line);
            }
        }

        private static void AppendBuffsSection(StringBuilder sb, Player target)
        {
            if (target == null || target.buffs == null || target.buffs.Count == 0)
            {
                sb.AppendLine("Sem buffs ativos.");
                return;
            }

            foreach (Buff buff in target.buffs)
            {
                float remaining = 0f;
                try { remaining = buff.BuffTimeRemaining(); } catch { }

                sb.AppendLine("- " + Safe(GetLocalizedBuffName(buff)) + " | Level=" + buff.level + " | Restante=" + remaining.ToString("F1") + "s");
            }
        }

        private static void AppendCosmeticsSection(StringBuilder sb, Player target)
        {
            try
            {
                sb.AppendLine();
                sb.AppendLine(WildTerraBot.Properties.Resources.InspectSectionCosmetics);

                string titleRaw = Safe(target.view.title);
                string titleLocalized = Safe(GetLocalizedTitle(target));

                sb.AppendLine("Titulo (raw): " + titleRaw);
                sb.AppendLine("Titulo (localizado): " + titleLocalized);
                sb.AppendLine("Avatar: " + Safe(target.view.avatar));
                sb.AppendLine("Border: " + Safe(target.view.border));
                sb.AppendLine(WildTerraBot.Properties.Resources.InspectLabelSex + ": " + (target.view.isMale ? WildTerraBot.Properties.Resources.InspectValueMale : WildTerraBot.Properties.Resources.InspectValueFemale));
            }
            catch
            {
                sb.AppendLine();
                sb.AppendLine(WildTerraBot.Properties.Resources.InspectSectionCosmetics);
                sb.AppendLine("Sem dados de cosmeticos.");
            }
        }

        private static string GetEquipmentSlotName(Player target, int index)
        {
            try
            {
                if (target.equipmentInfo != null && index >= 0 && index < target.equipmentInfo.Length)
                {
                    string cat = target.equipmentInfo[index].requiredCategory;
                    if (!string.IsNullOrWhiteSpace(cat))
                        return cat;
                }
            }
            catch { }

            return "Slot" + index;
        }

        private static string GetCurrentSkillName(Player target)
        {
            try
            {
                if (target.currentSkill >= 0 && target.currentSkill < target.skills.Count)
                    return Safe(GetLocalizedSkillName(target.skills[target.currentSkill]));
            }
            catch { }

            return "-";
        }

        private static string GetGuildName(Player target)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(target.guild.name))
                    return Safe(target.guild.name);
            }
            catch { }

            return "-";
        }

        private static string GetLocalizedItemName(Item item)
        {
            try
            {
                if (item.hash != 0)
                {
                    string localized = TranslateOrDefault("Item." + item.name, null);
                    if (!string.IsNullOrWhiteSpace(localized))
                        return localized;

                    return item.name;
                }
            }
            catch { }

            return "-";
        }

        private static string GetLocalizedProfSkillName(WTProfSkill prof)
        {
            try
            {
                string localized = TranslateOrDefault("Skill." + prof.name, null);
                if (!string.IsNullOrWhiteSpace(localized))
                    return localized;
            }
            catch { }

            return !string.IsNullOrWhiteSpace(prof.name) ? prof.name : "-";
        }

        private static string GetLocalizedSkillName(Skill skill)
        {
            try
            {
                if (skill.data != null)
                {
                    string localized = TranslateOrDefault("Action." + skill.name, null);
                    if (!string.IsNullOrWhiteSpace(localized))
                        return localized;
                }
            }
            catch { }

            return !string.IsNullOrWhiteSpace(skill.name) ? skill.name : "-";
        }

        private static string GetLocalizedBuffName(Buff buff)
        {
            try
            {
                if (buff.data != null)
                {
                    string localized = TranslateOrDefault("Effect." + buff.name, null);
                    if (!string.IsNullOrWhiteSpace(localized))
                        return localized;
                }
            }
            catch { }

            return !string.IsNullOrWhiteSpace(buff.name) ? buff.name : "-";
        }

        private static string GetLocalizedMountName(Mount mount)
        {
            try
            {
                if (mount != null)
                {
                    string localized = TranslateOrDefault("Item." + mount.name, null);
                    if (!string.IsNullOrWhiteSpace(localized))
                        return localized;
                }
            }
            catch { }

            return mount != null ? Safe(mount.name) : "-";
        }

        private static string GetLocalizedTitle(Player target)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(target.view.title))
                    return Player.GetLocalizedTitle(target.view.title);
            }
            catch { }

            return "-";
        }

        private static string TranslateOrDefault(string key, string fallback)
        {
            try
            {
                if (GameManager.instance != null && GameManager.instance.i18n != null && !string.IsNullOrWhiteSpace(key))
                {
                    string localized = GameManager.instance.i18n.GetIfExist(key);
                    if (!string.IsNullOrWhiteSpace(localized) && localized != key)
                        return localized;

                    localized = GameManager.instance.i18n.__(key);
                    if (!string.IsNullOrWhiteSpace(localized) && localized != key)
                        return localized;
                }
            }
            catch { }

            return fallback ?? key ?? "-";
        }

        private static string FormatQuality(int quality)
        {
            return quality >= 0 ? quality + "%" : "-";
        }

        private static string FormatDurability(Item item)
        {
            try
            {
                int max = item.HasMaxDurability();
                if (max <= 0)
                    return "-";

                int cur = item.GetDurability();
                float pct = item.GetDurabilityPercent() * 100f;
                return string.Format("{0}/{1} ({2:F1}%)", cur, max, pct);
            }
            catch
            {
                return "-";
            }
        }

        private static string FormatBool(bool value)
        {
            return value ? WildTerraBot.Properties.Resources.InspectValueYes : WildTerraBot.Properties.Resources.InspectValueNo;
        }

        private static string FormatPercent(long current, long max)
        {
            try
            {
                if (max <= 0)
                    return "-";

                double pct = (double)current / max * 100.0;
                return string.Format("{0:F1}%", pct);
            }
            catch
            {
                return "-";
            }
        }

        private static string FormatPercent(int current, int max)
        {
            try
            {
                if (max <= 0)
                    return "-";

                double pct = (double)current / max * 100.0;
                return string.Format("{0:F1}%", pct);
            }
            catch
            {
                return "-";
            }
        }

        private static string Safe(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "-";

            return value
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
        }
    }
}
