using HarmonyLib;
using System.Collections;
using UnityEngine;

namespace WildTerraBot
{
    internal static class PlayerHooks
    {
        [HarmonyPatch(typeof(Player), "OnStartLocalPlayer")]
        [HarmonyPostfix]
        static void OnStart(Player __instance)
        {
            if (GameObject.Find("WTRunner_UDP") == null)
            {
                GameObject go = new GameObject("WTRunner_UDP");
                UDPRunner runner = go.AddComponent<UDPRunner>();
                runner.MeuPersonagem = __instance;
                Object.DontDestroyOnLoad(go);

                WTSocketBot.PublicLogger.LogInfo("WTRunner Criado.");
            }
        }
    }
}