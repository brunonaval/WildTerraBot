using System;
using System.Collections.Generic;
using System.Linq;

namespace WildTerraBot
{
    /// <summary>
    /// Contrato do modo Treinamento recebido via UDP.
    ///
    /// Formato esperado:
    /// TRAINING;ON;skillsEnabled;buffEnabled;recoveryEnabled;buffRefreshSeconds;hpThreshold;spThreshold;skills(~);buffItems(~);recovery(~);autoAttackEnabled;autoAttackTarget
    /// TRAINING;OFF
    ///
    /// Exemplos:
    /// TRAINING;ON;1;1;1;1;50;40;SkillA~SkillB;Tea~Pie;HP:Healing Potion~SP:Energy Drink;1;Cavecrawler
    /// TRAINING;OFF
    /// </summary>
    public class TrainingModeConfig
    {
        public bool EnableSkills = false;
        public bool EnableBuffItems = false;
        public bool EnableRecovery = false;
        public bool EnableAutoAttack = false;

        public int BuffRefreshSeconds = 1;
        public int HpThreshold = 50;
        public int SpThreshold = 50;

        public List<string> SkillNames = new List<string>();
        public List<string> BuffItemNames = new List<string>();
        public List<TrainingRecoveryItem> RecoveryItems = new List<TrainingRecoveryItem>();
        public string AutoAttackTargetName = "";

        public TrainingModeConfig Normalize()
        {
            return new TrainingModeConfig
            {
                EnableSkills = EnableSkills,
                EnableBuffItems = EnableBuffItems,
                EnableRecovery = EnableRecovery,
                EnableAutoAttack = EnableAutoAttack,
                BuffRefreshSeconds = Clamp(BuffRefreshSeconds, 0, 30),
                HpThreshold = Clamp(HpThreshold, 1, 100),
                SpThreshold = Clamp(SpThreshold, 1, 100),
                SkillNames = NormalizeList(SkillNames),
                BuffItemNames = NormalizeList(BuffItemNames),
                RecoveryItems = NormalizeRecoveryItems(RecoveryItems),
                AutoAttackTargetName = NormalizeSingleValue(AutoAttackTargetName)
            };
        }

        public bool Validate(out string error)
        {
            error = null;

            if (!EnableSkills && !EnableBuffItems && !EnableRecovery && !EnableAutoAttack)
            {
                error = WildTerraBot.Properties.Resources.TrainingModeConfigNoBlockEnabled;
                return false;
            }

            if (EnableSkills && (SkillNames == null || SkillNames.Count == 0))
            {
                error = WildTerraBot.Properties.Resources.TrainingModeConfigSkillsEnabledEmpty;
                return false;
            }

            if (EnableBuffItems && (BuffItemNames == null || BuffItemNames.Count == 0))
            {
                error = WildTerraBot.Properties.Resources.TrainingModeConfigBuffItemsEnabledEmpty;
                return false;
            }

            if (EnableRecovery && (RecoveryItems == null || RecoveryItems.Count == 0))
            {
                error = WildTerraBot.Properties.Resources.TrainingModeConfigRecoveryEnabledEmptyOrInvalid;
                return false;
            }

            if (EnableAutoAttack && string.IsNullOrWhiteSpace(AutoAttackTargetName))
            {
                error = WildTerraBot.Properties.Resources.TrainingModeConfigAutoAttackEnabledEmptyTarget;
                return false;
            }

            return true;
        }

        public string BuildSummary()
        {
            string autoAtk = EnableAutoAttack
                ? string.Format(WildTerraBot.Properties.Resources.TrainingModeConfigSummaryAutoAttackOnFormat, AutoAttackTargetName)
                : WildTerraBot.Properties.Resources.TrainingModeConfigSummaryAutoAttackOff;

            return string.Format(
                WildTerraBot.Properties.Resources.TrainingModeConfigSummaryFormat,
                SkillNames.Count,
                BuffItemNames.Count,
                RecoveryItems.Count,
                autoAtk,
                HpThreshold,
                SpThreshold,
                BuffRefreshSeconds);
        }

        public static TrainingModeConfig FromUdpParts(string[] p)
        {
            var cfg = new TrainingModeConfig();
            cfg.EnableSkills = ParseBool(GetPart(p, 2));
            cfg.EnableBuffItems = ParseBool(GetPart(p, 3));
            cfg.EnableRecovery = ParseBool(GetPart(p, 4));
            cfg.BuffRefreshSeconds = ParseInt(GetPart(p, 5), 1);
            cfg.HpThreshold = ParseInt(GetPart(p, 6), 50);
            cfg.SpThreshold = ParseInt(GetPart(p, 7), 50);
            cfg.SkillNames = ParseTildeList(GetPart(p, 8));
            cfg.BuffItemNames = ParseTildeList(GetPart(p, 9));
            cfg.RecoveryItems = ParseRecoveryItems(GetPart(p, 10));
            cfg.EnableAutoAttack = ParseBool(GetPart(p, 11));
            cfg.AutoAttackTargetName = NormalizeSingleValue(GetPart(p, 12));
            return cfg.Normalize();
        }

        public static List<string> ParseTildeList(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return new List<string>();
            return payload
                .Split(new[] { '~' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => (x ?? "").Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static List<TrainingRecoveryItem> ParseRecoveryItems(string payload)
        {
            var list = new List<TrainingRecoveryItem>();
            foreach (var raw in ParseTildeList(payload))
            {
                int idx = raw.IndexOf(':');
                if (idx <= 0 || idx >= raw.Length - 1) continue;

                string kind = (raw.Substring(0, idx) ?? "").Trim().ToUpperInvariant();
                string itemName = (raw.Substring(idx + 1) ?? "").Trim();
                if (string.IsNullOrWhiteSpace(itemName)) continue;
                if (kind != "HP" && kind != "SP") continue;

                list.Add(new TrainingRecoveryItem
                {
                    ResourceType = kind,
                    ItemName = itemName
                });
            }

            return NormalizeRecoveryItems(list);
        }

        private static bool ParseBool(string raw)
        {
            string s = (raw ?? "").Trim();
            if (string.Equals(s, "1", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "TRUE", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "ON", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "YES", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static int ParseInt(string raw, int fallback)
        {
            int value;
            return int.TryParse((raw ?? "").Trim(), out value) ? value : fallback;
        }

        private static string GetPart(string[] p, int index)
        {
            if (p == null || index < 0 || index >= p.Length) return "";
            return p[index] ?? "";
        }

        private static List<string> NormalizeList(List<string> src)
        {
            if (src == null) return new List<string>();
            return src
                .Select(x => (x ?? "").Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string NormalizeSingleValue(string raw)
        {
            return (raw ?? "").Trim().Replace(";", "").Replace("~", " ");
        }

        private static List<TrainingRecoveryItem> NormalizeRecoveryItems(List<TrainingRecoveryItem> src)
        {
            if (src == null) return new List<TrainingRecoveryItem>();

            return src
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.ResourceType) && !string.IsNullOrWhiteSpace(x.ItemName))
                .Select(x => new TrainingRecoveryItem
                {
                    ResourceType = (x.ResourceType ?? "").Trim().ToUpperInvariant(),
                    ItemName = (x.ItemName ?? "").Trim()
                })
                .Where(x => (x.ResourceType == "HP" || x.ResourceType == "SP") && !string.IsNullOrWhiteSpace(x.ItemName))
                .GroupBy(x => x.ResourceType + "|" + x.ItemName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }

    public class TrainingRecoveryItem
    {
        public string ResourceType = ""; // HP | SP
        public string ItemName = "";

        public bool IsHp => string.Equals(ResourceType, "HP", StringComparison.OrdinalIgnoreCase);
        public bool IsSp => string.Equals(ResourceType, "SP", StringComparison.OrdinalIgnoreCase);

        public override string ToString()
        {
            return $"{ResourceType}:{ItemName}";
        }
    }
}
