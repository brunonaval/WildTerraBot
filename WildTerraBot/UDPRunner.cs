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

namespace WildTerraBot
{
    public class UDPRunner : MonoBehaviour
    {
        public Player MeuPersonagem;
        private UdpClient udpSender;
        private Thread listenThread;
        private volatile bool running = false;
        private bool _botAtivo = false;
        private bool _useMount = false;
        private bool _modoColeta = false;
        private bool _modoHunter = false;
        private bool _modoPesca = false;
        private bool _returningHome = false;
        private Vector3 _homeCoordsBackup = Vector3.zero;
        private string _alvoHunterTipo = "";
        private string _armaPreferida = "";
        private string _nomeVaraPesca = "";
        private string _nomeIscaPesca = "";
        private WTMob _combatTarget = null;
        private float _nextAttackPulse = 0f;
        private float _nextEquipCheck = 0f;
        private const float ATTACK_PULSE = 0.30f;
        private int _lastKnownHp = -1;
        private float _lastDamageTime = 0f;
        private bool _reportouCombate = false;
        private float _nextCastCheck = 0f;
        private float _nextDebugLog = 0f;
        private bool _isMountingRoutineActive = false;
        private float _pauseMovementUntil = 0f;
        private const float MOUNT_ANIMATION_TIME = 4.0f;
        private Vector3? _lastMoveTarget = null;
        private float nextStatsSend = 0f;
        private float nextBagSend = 0f;
        private float nextRadarScan = 0f;
        private float nextDropCheck = 0f;
        private float nextEatCheck = 0f;
        private const float AUTO_EAT_COOLDOWN = 1.0f;
        private int _eatThreshold = 30;
        private bool _resting = false;
        private bool _depositando = false;
        private HashSet<string> bonusPendentes = new HashSet<string>();
        private HashSet<string> itensSeguros = new HashSet<string>();
        private HashSet<string> itensDropar = new HashSet<string>();
        private HashSet<string> itensComer = new HashSet<string>();
        private ConcurrentQueue<Vector2> moveQueue = new ConcurrentQueue<Vector2>();
        private ConcurrentQueue<string> actionQueue = new ConcurrentQueue<string>();
        public ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>();

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
        private static readonly Regex _bTagNumber = new Regex("<b>(\\d+)</b>", RegexOptions.Compiled);

        void Start()
        {
            // === CORREÇÃO: Usa o novo nome do método (DumpFromScene) ===
            StartCoroutine(AutoDumpDelay());

            udpSender = new UdpClient();
            try
            {
                var udpListener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 8889));
                running = true;
                listenThread = new Thread(() => ListenLoop(udpListener));
                listenThread.IsBackground = true;
                listenThread.Start();
                WTSocketBot.PublicLogger.LogInfo("[SUCESSO] Porta 8889 Aberta.");
            }
            catch (Exception ex) { WTSocketBot.PublicLogger.LogError("[ERRO] Porta: " + ex.Message); }

            Type tipoWT = typeof(WTPlayer); Type tipoPlayer = typeof(Player); Type tipoEntity = typeof(Entity); Type tipoMob = typeof(WTMob);
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
            }
            catch (Exception ex) { WTSocketBot.PublicLogger.LogError($"[INIT] Reflection Error: {ex.Message}"); }
        }

        // TIMER DE 15 SEGUNDOS
        IEnumerator AutoDumpDelay()
        {
            yield return new WaitForSeconds(15.0f);
            if (WTSocketBot.Instance != null && !WTSocketBot.HasDumped)
            {
                // ATENÇÃO: Nome corrigido aqui
                WTSocketBot.Instance.DumpFromScene("AUTO-START-15s");
                WTSocketBot.HasDumped = true;
            }
        }

        void OnDestroy() { running = false; if (udpSender != null) udpSender.Close(); }

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

                    // ATENÇÃO: Nome corrigido aqui também
                    if (p[0] == "DUMP") mainThreadActions.Enqueue(() => WTSocketBot.Instance.DumpFromScene("UDP-TRIGGER"));
                    else if (p[0] == "MOVE") { moveQueue.Enqueue(new Vector2(float.Parse(p[1], CultureInfo.InvariantCulture), float.Parse(p[2], CultureInfo.InvariantCulture))); ResetModes(); }
                    else if (p[0] == "HARVEST") { while (actionQueue.TryDequeue(out _)) { } actionQueue.Enqueue(p[1]); ResetModes(); _modoColeta = true; }
                    else if (p[0] == "HUNT" && p.Length >= 2)
                    {
                        _alvoHunterTipo = p[1].Trim();
                        _armaPreferida = (p.Length >= 3) ? p[2].Trim() : "";
                        ResetModes(); _modoHunter = true;
                        WTSocketBot.PublicLogger.LogInfo($"[HUNTER] Alvo: {_alvoHunterTipo} | Arma: {_armaPreferida}");
                    }
                    else if (p[0] == "FISHING")
                    {
                        if (p.Length >= 2 && p[1] == "ON")
                        {
                            ResetModes(); _modoPesca = true;
                            _nomeVaraPesca = (p.Length >= 3) ? p[2].Trim() : "";
                            _nomeIscaPesca = (p.Length >= 4) ? p[3].Trim() : "";
                            WTSocketBot.IsFishingBotActive = true;
                            WTSocketBot.PublicLogger.LogInfo($"[PESCA] ATIVADO. Vara: {_nomeVaraPesca}, Isca: {_nomeIscaPesca}");
                        }
                        else
                        {
                            _modoPesca = false; WTSocketBot.IsFishingBotActive = false;
                            WTSocketBot.PublicLogger.LogInfo("[PESCA] DESATIVADO.");
                        }
                    }
                    else if (p[0] == "RETURN_HOME")
                    {
                        ResetModes(); _returningHome = true; _botAtivo = true; _lastMoveTarget = null;
                        if (p.Length >= 3) { try { float hx = float.Parse(p[1], CultureInfo.InvariantCulture); float hz = float.Parse(p[2], CultureInfo.InvariantCulture); _homeCoordsBackup = new Vector3(hx, 0, hz); WTSocketBot.PublicLogger.LogInfo($"[HOME] Coordenadas Recebidas: {hx}, {hz}"); } catch { } }
                        else { WTSocketBot.PublicLogger.LogInfo("[HOME] Voltando (Usando ClaimPoint)..."); }
                    }
                    else if (p[0] == "BOT_STATUS")
                    {
                        _botAtivo = (p[1] == "ON");
                        if (!_botAtivo) { ResetModes(); _lastMoveTarget = null; StopAllCoroutines(); _isMountingRoutineActive = false; _combatTarget = null; WTSocketBot.IsFishingBotActive = false; EnviarCombateStatus(false); }
                    }
                    else if (p[0] == "MOUNT_CONFIG") { _useMount = (p[1] == "ON"); }
                    else if (p[0] == "TEST_MOUNT") { mainThreadActions.Enqueue(() => { if (MeuPersonagem is WTPlayer wtPlayer) ToggleMount(wtPlayer); }); }
                    else if (p[0] == "SAFE_LIST")
                    {
                        string[] itens = p.Length >= 2 ? p[1].Split('~') : Array.Empty<string>();
                        mainThreadActions.Enqueue(() =>
                        {
                            itensSeguros.Clear();
                            foreach (var raw in itens)
                            {
                                var s = raw?.Trim();
                                if (string.IsNullOrWhiteSpace(s)) continue;   // <-- FIX: ignora vazio
                                itensSeguros.Add(s);
                            }
                        });
                    }

                    else if (p[0] == "DROP_LIST") { string[] itens = p[1].Split('~'); mainThreadActions.Enqueue(() => { itensDropar.Clear(); foreach (var i in itens) if (!string.IsNullOrWhiteSpace(i)) itensDropar.Add(i.Trim()); }); }
                    else if (p[0] == "EAT_LIST") { string[] itens = p[1].Split('~'); mainThreadActions.Enqueue(() => { itensComer.Clear(); foreach (var i in itens) if (!string.IsNullOrWhiteSpace(i)) itensComer.Add(i.Trim()); }); }
                    else if (p[0] == "EAT_THRESHOLD") { if (int.TryParse(p[1], out int val)) _eatThreshold = val; }
                    else if (p[0] == "DEPOSIT_ALL") { string alvo = p.Length >= 2 ? p[1] : ""; mainThreadActions.Enqueue(() => IniciarDepositoProximo(alvo)); }
                }
                catch { }
            }
        }

        private void ResetModes() { _modoColeta = false; _modoHunter = false; _returningHome = false; _modoPesca = false; }

        void Update()
        {
            if (MeuPersonagem == null) return;
            WTPlayer wtPlayer = MeuPersonagem as WTPlayer;
            if (wtPlayer == null) return;

            while (mainThreadActions.TryDequeue(out var action)) action.Invoke();

            // === PRIORIDADE: SOBREVIVÊNCIA ===
            bool emCombateReal = CheckInCombat(wtPlayer);
            int currentHp = wtPlayer.health;
            bool tomandoDano = (_lastKnownHp != -1 && currentHp < _lastKnownHp);
            _lastKnownHp = currentHp;

            try
            {
                if (Time.time >= nextStatsSend) { nextStatsSend = Time.time + 0.1f; EnviarStats(); }
                if (Time.time >= nextRadarScan) { nextRadarScan = Time.time + 0.5f; EnviarRadar(); }
                if (Time.time >= nextBagSend) { nextBagSend = Time.time + 1.0f; EnviarBag(); }
            }
            catch { }

            if (tomandoDano || emCombateReal)
            {
                if (_combatTarget == null) _combatTarget = PickAggressorSmart(wtPlayer, 8f);

                if (_combatTarget != null || emCombateReal)
                {
                    if (!tomandoDano && _modoHunter && !string.IsNullOrEmpty(_armaPreferida))
                    {
                        if (!CheckAndEquipItem(wtPlayer, _armaPreferida, 0)) { }
                    }

                    bool lutando = RunCombatLogic(wtPlayer, _modoHunter);
                    if (lutando)
                    {
                        if (!_reportouCombate) { EnviarCombateStatus(true); }
                        return; // SAI DO UPDATE PARA NÃO PESCAR
                    }
                }
            }

            if (_reportouCombate) EnviarCombateStatus(false);

            // [LÓGICA DE SOBREVIVÊNCIA ADICIONAL (SKINNING)]
            if (_combatTarget != null)
            {
                if (!IsValidTarget(_combatTarget))
                {
                    if (_fiWorldObject != null)
                    {
                        WTObject corpse = _fiWorldObject.GetValue(_combatTarget) as WTObject;
                        if (corpse != null && corpse.isActiveAndEnabled) TrySkinCorpse(wtPlayer, corpse);
                    }
                    _combatTarget = null;
                }
            }
            else if (_modoHunter)
            {
                if (!CheckAmmo(wtPlayer))
                {
                    WTSocketBot.PublicLogger.LogWarning("[HUNTER] SEM MUNIÇÃO! Voltando para casa...");
                    _modoHunter = false; _returningHome = true;
                    if (_homeCoordsBackup == Vector3.zero && wtPlayer.claimPoint == Vector3.zero) { _returningHome = false; _botAtivo = false; }
                    return;
                }

                bool alvoValido = _combatTarget != null && IsValidTarget(_combatTarget);
                if (!alvoValido || !IsMobType(_combatTarget, _alvoHunterTipo))
                {
                    _combatTarget = BuscarMobPorTipo(wtPlayer, _alvoHunterTipo);
                }
            }
            else if (_combatTarget == null)
            {
                float range = _modoPesca ? 5f : 12f;
                _combatTarget = PickAggressorSmart(wtPlayer, range);
            }

            if (_modoPesca) { RunFishingLogic(wtPlayer); return; }

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
                        WTSocketBot.PublicLogger.LogInfo("[HOME] Cheguei em casa. Bot Pausado.");
                        _returningHome = false; _botAtivo = false;
                        EnviarMensagem("BOT_STATUS;OFF");
                    }
                }
                return;
            }

            if (_isMountingRoutineActive) return;
            if (Time.time < _pauseMovementUntil) return;

            if (_useMount && !_modoColeta && !_modoHunter && _lastMoveTarget.HasValue && !CheckIsMounted(wtPlayer))
            {
                float simpleDist = Vector3.Distance(wtPlayer.transform.position, _lastMoveTarget.Value);
                if (simpleDist > 5.0f) { StartCoroutine(MountSequence(wtPlayer)); return; }
            }

            if (Time.time >= nextDropCheck) { nextDropCheck = Time.time + 0.5f; ProcessarDrops(wtPlayer); }
            if (Time.time >= nextEatCheck) { TryAutoEat(wtPlayer); }
            if (Time.time >= nextBagSend - 0.5f) { VerificarRequisitosPendentes(wtPlayer); CheckBagFull(); }

            if (!CheckStamina(wtPlayer)) return;

            if (actionQueue.TryDequeue(out var targetName))
            {
                _modoColeta = true;
                TryHarvestObjectNative(wtPlayer, targetName);
            }

            if (_lastMoveTarget.HasValue)
            {
                MoveToXZ(wtPlayer, _lastMoveTarget.Value.x, _lastMoveTarget.Value.z);
            }
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
            if (!gameDizTemVara) { WTSocketBot.PublicLogger.LogWarning("[PESCA] Aguardando Vara..."); return; }

            if (!IsBaitEquippedAnywhere(p, _nomeIscaPesca)) { WTSocketBot.PublicLogger.LogWarning($"[PESCA] Aguardando Isca ({_nomeIscaPesca})..."); return; }

            bool pescando = false;
            try { pescando = (bool)_isFishingMethod.Invoke(p, null); } catch { }
            if (pescando) return;

            // 5. Arremessa
            Vector3 alvoAgua = p.transform.position + (p.transform.forward * 3.5f);
            WTSocketBot.PublicLogger.LogInfo("[PESCA] Arremessando...");
            CmdSkillToPoint(p, alvoAgua);
        }

        // === HELPER DEBUG: DUMP EQUIPMENT ===
        private void DumpEquipment(WTPlayer p)
        {
            if (p.equipment == null) return;
            string log = "[DEBUG EQUIP] ";
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
                        WTSocketBot.PublicLogger.LogInfo($"[EQUIP] Equipando Isca '{slot.item.data.name}' no Slot {targetSlot} (Auto-Detectado)...");
                        if (_swapEquipMethod != null) try { _swapEquipMethod.Invoke(p, new object[] { i, targetSlot }); } catch { }
                        return false;
                    }
                }
            }
            WTSocketBot.PublicLogger.LogWarning($"[PESCA] Isca '{itemName}' não encontrada!");
            return false;
        }

        private bool CheckAndEquipItem(WTPlayer p, string itemName, int targetSlot)
        {
            if (Time.time < _nextEquipCheck) return true;
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
                    if (name.IndexOf(itemName, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            catch { }
            if (p.inventory == null) return false;
            _nextEquipCheck = Time.time + 1.5f;
            for (int i = 0; i < p.inventory.Count; i++)
            {
                var slot = p.inventory[i];
                if (slot.amount > 0 && slot.item.data != null)
                {
                    if (slot.item.data.name.IndexOf(itemName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (slot.item.GetDurability() > 0)
                        {
                            WTSocketBot.PublicLogger.LogInfo($"[EQUIP] Equipando Vara {slot.item.data.name} no Slot {targetSlot}...");
                            if (_swapEquipMethod != null) try { _swapEquipMethod.Invoke(p, new object[] { i, targetSlot }); } catch { }
                            return false;
                        }
                    }
                }
            }
            return false;
        }

        // MÉTODOS DE SUPORTE
        private void CmdSkillToPoint(WTPlayer p, Vector3 point) { if (_cmdSkillToPoint != null) try { _cmdSkillToPoint.Invoke(p, new object[] { point }); } catch { } }
        private bool CheckAmmo(WTPlayer p) { try { var rightHand = p.GetEquippedRightHand(); if (!rightHand.HasValue || rightHand.Value.data == null) return true; WTWeaponItem weapon = rightHand.Value.data as WTWeaponItem; if (weapon == null || weapon.requiredAmmo == null) return true; int count = p.InventoryCountGroup(weapon.requiredAmmo); return count > 0; } catch { return true; } }
        private bool IsValidTarget(WTMob mob) { if (mob == null) return false; if (!mob.gameObject.activeInHierarchy) return false; if (!mob.isActiveAndEnabled) return false; if (mob.health <= 0) return false; if (mob.state == "DEAD") return false; return true; }
        private string GetMobTypeKey(WTMob mob) { if (mob == null || _fiEntityType == null) return null; var et = _fiEntityType.GetValue(mob) as WTEntityType; return et != null ? et.name : null; }
        private bool IsMobType(WTMob mob, string typeKey) { string key = GetMobTypeKey(mob); return key != null && string.Equals(key, typeKey, StringComparison.OrdinalIgnoreCase); }
        private WTMob BuscarMobPorTipo(WTPlayer me, string typeKey) { WTMob best = null; float menorDist = 9999f; foreach (var mob in FindObjectsOfType<WTMob>()) { if (!IsValidTarget(mob)) continue; if (IsMobType(mob, typeKey)) { float d = Vector3.Distance(me.transform.position, mob.transform.position); if (d < menorDist && d < 80) { menorDist = d; best = mob; } } } return best; }
        private void TrySkinCorpse(WTPlayer player, WTObject corpse) { if (corpse == null) return; var gather = corpse.worldType?.gatherSettings; if (gather == null || gather.skill == null) return; if (gather.bonusRequired != null) { bool tem = false; var mao = player.GetEquippedRightHand(); if (mao.HasValue && ItemTemBonus(mao.Value, gather.bonusRequired.name)) tem = true; if (!tem && player.inventory != null) foreach (var slot in player.inventory) if (slot.amount > 0 && ItemTemBonus(slot.item, gather.bonusRequired.name)) { tem = true; break; } if (!tem) return; } WTSocketBot.PublicLogger.LogInfo($"[HUNTER] Esfolando {corpse.name}..."); player.WorldObjectTryAction(corpse, gather.skill); }
        private bool RunCombatLogic(WTPlayer me, bool isHunting) { if (_combatTarget == null) return false; float dist = Vector3.Distance(me.transform.position, _combatTarget.transform.position); bool isFleeing = (_combatTarget.state == "RUNNING"); if (dist > 2.5f) { if (!isHunting && !isFleeing) { if (Time.time - _lastDamageTime > 5.0f) return false; me.transform.LookAt(_combatTarget.transform); SafeStopAgent(me); return true; } if (isHunting || isFleeing) { Vector3 targetPos = _combatTarget.transform.position; if (isFleeing && _combatTarget.agent != null) targetPos += _combatTarget.agent.velocity * 0.5f; MoveToXZ(me, targetPos.x, targetPos.z); return true; } } SafeStopAgent(me); if (CheckIsMounted(me)) { if (Time.time >= _nextAttackPulse) { ToggleMount(me); _nextAttackPulse = Time.time + 0.5f; } return true; } try { var nid = _combatTarget.netIdentity ?? _combatTarget.GetComponent<NetworkIdentity>(); if (me.target != _combatTarget && nid != null) CmdSetTarget(me, nid); } catch { } if (Time.time >= _nextAttackPulse) { me.transform.LookAt(_combatTarget.transform); me.transform.eulerAngles = new Vector3(0, me.transform.eulerAngles.y, 0); Vector3 miraFinal; var col = _combatTarget.GetComponent<Collider>(); if (col != null) miraFinal = col.bounds.center; else miraFinal = _combatTarget.transform.position + (Vector3.up * 1.2f); CmdSkillToPoint(me, miraFinal); _nextAttackPulse = Time.time + ATTACK_PULSE; } return true; }
        private WTMob PickAggressorSmart(WTPlayer me, float radius) { Vector3 myPos = me.transform.position; float bestSqr = radius * radius; WTMob best = null; int bestAggroVal = -1; int myId = me.worldId; if (me.target is WTMob tMob && IsValidTarget(tMob)) { float sqr = (tMob.transform.position - myPos).sqrMagnitude; if (sqr <= bestSqr) return tMob; } var mobs = FindObjectsOfType<WTMob>(); foreach (var mob in mobs) { if (!IsValidTarget(mob)) continue; float sqr = (mob.transform.position - myPos).sqrMagnitude; if (sqr > bestSqr) continue; bool isTargetingMe = (mob.target == me); int currentAggro = -1; if (_aggroByIdField != null) { try { var dict = _aggroByIdField.GetValue(mob) as IDictionary; if (dict != null && dict.Contains(myId)) { object aggroObj = dict[myId]; FieldInfo valField = aggroObj.GetType().GetField("value"); if (valField != null) currentAggro = (int)valField.GetValue(aggroObj); } } catch { } } if (currentAggro > bestAggroVal) { bestAggroVal = currentAggro; best = mob; } else if (currentAggro == bestAggroVal) { if (isTargetingMe || (sqr < 9.0f && mob.entityType?.mobBehaviour?.type == WTMobBehaviour.Mode.Aggressive)) { if (best == null || sqr < (best.transform.position - myPos).sqrMagnitude) { best = mob; } } } } return best; }
        void EnviarCombateStatus(bool emCombate) { try { string msg = $"COMBAT_FLAG;{(emCombate ? "ON" : "OFF")}"; byte[] dados = Encoding.ASCII.GetBytes(msg); udpSender.Send(dados, dados.Length, "127.0.0.1", 8888); } catch { } }
        IEnumerator MountSequence(WTPlayer p) { _isMountingRoutineActive = true; SafeStopAgent(p); yield return new WaitForSeconds(0.25f); ToggleMount(p); _pauseMovementUntil = Time.time + MOUNT_ANIMATION_TIME; _isMountingRoutineActive = false; }
        void SafeStopAgent(WTPlayer p) { if (p.agent != null && p.agent.enabled) { if (p.agent.isOnNavMesh) p.agent.isStopped = true; p.agent.velocity = Vector3.zero; p.agent.ResetPath(); } }
        void SafeResumeAgent(WTPlayer p) { if (p.agent != null && p.agent.enabled) { if (p.agent.isOnNavMesh) p.agent.isStopped = false; } }
        bool CheckInCombat(WTPlayer p) { if (_inCombatMethod == null) return false; try { return (bool)_inCombatMethod.Invoke(p, null); } catch { return false; } }
        bool CheckIsMounted(WTPlayer p) { if (_isMountedMethod == null) return false; try { return (bool)_isMountedMethod.Invoke(p, null); } catch { return false; } }
        void ToggleMount(WTPlayer p) { if (_toggleMountMethod == null) return; try { _toggleMountMethod.Invoke(p, null); } catch { } }
        void CmdSetTarget(WTPlayer p, NetworkIdentity target) { if (_setTargetMethod != null) try { _setTargetMethod.Invoke(p, new object[] { target }); } catch { } }
        void MoveToXZ(WTPlayer p, float x, float z) { _lastMoveTarget = new Vector3(x, p.transform.position.y, z); var agent = p.agent; if (agent != null && agent.enabled) { if (agent.isOnNavMesh) { agent.isStopped = false; Vector3 dest = agent.NearestValidDestination(agent.BestPlaceY(_lastMoveTarget.Value)); if (Vector3.Distance(agent.destination, dest) > 0.5f) { agent.stoppingDistance = 0f; agent.destination = dest; } } else { NavMeshHit hit; if (NavMesh.SamplePosition(p.transform.position, out hit, 10.0f, NavMesh.AllAreas)) { agent.Warp(hit.position); agent.isStopped = false; agent.destination = _lastMoveTarget.Value; } } } }
        private int ParseFoodFromTooltip(WTPlayer p, string localeKey) { try { string tt = p.TopBarToolTip(localeKey); if (string.IsNullOrEmpty(tt)) return -1; var m = _bTagNumber.Match(tt); if (!m.Success) return -1; return int.Parse(m.Groups[1].Value); } catch { return -1; } }
        private int[] GetFoodLevelsNative(WTPlayer p) { int meat = ParseFoodFromTooltip(p, "FoodToolTip.Meat"); int bread = ParseFoodFromTooltip(p, "FoodToolTip.Bread"); int mixed = ParseFoodFromTooltip(p, "FoodToolTip.Mixed"); int veg = ParseFoodFromTooltip(p, "FoodToolTip.Vegetable"); int fruit = ParseFoodFromTooltip(p, "FoodToolTip.Fruit"); if (meat < 0 || bread < 0 || mixed < 0 || veg < 0 || fruit < 0) return null; return new[] { meat, bread, mixed, veg, fruit }; }
        private float ScoreFoodCandidate(WTUsableItem usable, int[] cur) { float score = 0f; foreach (var fv in usable.foods) { int t = (int)fv.type; int val = fv.value; if (t < 0 || t >= cur.Length || val <= 0) continue; int deficit = 100 - cur[t]; int gain = Math.Min(deficit, val); int overflow = Math.Max(0, val - deficit); score += gain; if (cur[t] == 0 && val > 0) score += 15f; score -= overflow * 1.5f; } if (usable.rawFoodType) score -= 2f; return score; }
        public void TryAutoEat(WTPlayer wtPlayer) { if (Time.time < nextEatCheck) return; nextEatCheck = Time.time + AUTO_EAT_COOLDOWN; if (itensComer.Count == 0 || _useMethod == null || _foodArrayField == null) return; int[] currentFoodLevels = (int[])_foodArrayField.GetValue(wtPlayer); if (currentFoodLevels == null) return; if (wtPlayer.inventory == null) return; for (int i = 0; i < wtPlayer.inventory.Count; i++) { var slot = wtPlayer.inventory[i]; if (slot.amount <= 0 || slot.item.data == null) continue; bool permitido = false; foreach (var permitidoNome in itensComer) { if (slot.item.data.name.IndexOf(permitidoNome, StringComparison.OrdinalIgnoreCase) >= 0) { permitido = true; break; } } if (!permitido) continue; if (!(slot.item.data is WTUsableItem usable)) continue; if (usable.foods == null || usable.foods.Length == 0) continue; bool precisaComer = false; foreach (var foodVal in usable.foods) { int typeIndex = (int)foodVal.type; if (typeIndex >= 0 && typeIndex < currentFoodLevels.Length && foodVal.value > 0) { int nivelAtual = currentFoodLevels[typeIndex]; if (nivelAtual < _eatThreshold) { precisaComer = true; break; } } } if (!precisaComer) continue; if (wtPlayer.IsFull(usable.foods, usable.rawFoodType)) continue; try { _useMethod.Invoke(wtPlayer, new object[] { i }); return; } catch { } } }
        void ProcessarDrops(WTPlayer wtPlayer) { if (itensDropar.Count == 0 || wtPlayer.inventory == null || _dropMethod == null) return; for (int i = 0; i < wtPlayer.inventory.Count; i++) { var slot = wtPlayer.inventory[i]; if (slot.amount > 0 && slot.item.data != null) { string nome = slot.item.data.name; foreach (var lixo in itensDropar) { if (nome.IndexOf(lixo, StringComparison.OrdinalIgnoreCase) >= 0) { try { _dropMethod.Invoke(wtPlayer, new object[] { i }); } catch { } return; } } } } }
        void CheckBagFull() { try { var method = typeof(Entity).GetMethod("InventorySlotsFree", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic); if (method != null) { int slotsFree = (int)method.Invoke(MeuPersonagem, null); if (slotsFree == 0) EnviarMensagem("BAG_FULL"); } } catch { } }
        void IniciarDepositoProximo(string nomeAlvo = "") { if (_depositando) return; WTPlayer wtPlayer = MeuPersonagem as WTPlayer; if (wtPlayer == null) return; WTStructure bau = WTBankingUtils.FindBestBankChest(wtPlayer.transform.position, 15.0f, nomeAlvo); if (bau != null) { WTSocketBot.PublicLogger.LogInfo($"[BANK] Alvo: {bau.name}. Iniciando..."); StartCoroutine(RotinaDeposito(wtPlayer, bau)); } else { WTSocketBot.PublicLogger.LogError($"[BANK] Erro: Baú '{nomeAlvo}' não encontrado."); EnviarMensagem("BANK_FINISH"); } }
        IEnumerator RotinaDeposito(WTPlayer wtPlayer, WTStructure bau) { _depositando = true; WTSocketBot.PublicLogger.LogInfo("[BANK] Iniciando Movimento..."); var agent = wtPlayer.agent; if (agent != null && agent.enabled && agent.isOnNavMesh) { agent.isStopped = false; agent.stoppingDistance = 0f; agent.destination = bau.transform.position; } float timeout = Time.time + 10f; while (Vector3.Distance(wtPlayer.transform.position, bau.transform.position) > 2.8f && Time.time < timeout) yield return new WaitForSeconds(0.2f); SafeStopAgent(wtPlayer); ScriptableSkill openSkill = null; if (bau.worldType?.actions != null) foreach (var act in bau.worldType.actions) if (act?.actionSkill != null && act.actionSkill.name.Contains("OpenContainer")) { openSkill = act.actionSkill; break; } if (openSkill == null && bau.actionSkills != null) foreach (var s in bau.actionSkills) { if (s.name == "OpenContainer") openSkill = s; } if (openSkill == null) { _depositando = false; EnviarMensagem("BANK_FINISH"); yield break; } wtPlayer.WorldObjectTryAction(bau, openSkill); yield return new WaitForSeconds(1.0f); int openedTab = 0; if (GameManager.instance != null && GameManager.instance.IsContainerWindowOpen(out int t)) openedTab = t; int tabsFromType = 0; try { if (bau != null && bau.worldType != null) tabsFromType = bau.worldType.containerTabs; } catch { } int totalTabs = (tabsFromType > 0) ? tabsFromType : 1; int maxTabs = Math.Min(totalTabs, 6); int baseCmdTab = openedTab; if (tabsFromType > 0 && baseCmdTab <= 0) baseCmdTab = 1; for (int tabOffset = 0; tabOffset < maxTabs; tabOffset++) { int cmdTab; if (tabsFromType > 0) { cmdTab = baseCmdTab + tabOffset; if (cmdTab > totalTabs) break; } else { cmdTab = baseCmdTab; } if (wtPlayer.inventory != null) { for (int i = wtPlayer.inventory.Count - 1; i >= 0; i--) { if (i >= wtPlayer.inventory.Count) continue; var slot = wtPlayer.inventory[i]; if (slot.amount <= 0 || slot.item.data == null) continue; if (MotivoParaManter(slot.item) != null) continue; wtPlayer.CmdInventoryItemAction(i, (ItemActionType)2, cmdTab); yield return new WaitForSeconds(0.12f); } } if (tabsFromType > 0) yield return new WaitForSeconds(0.20f); } yield return new WaitForSeconds(0.5f); wtPlayer.CloseContainer(); _depositando = false; EnviarMensagem("BANK_FINISH"); }
        bool ShouldKeepItem(Item item) { if (item.data == null) return true; foreach (var s in itensSeguros) if (item.data.name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true; return false; }
        string MotivoParaManter(Item item) { if (item.data == null) return "Nulo"; foreach (var s in itensSeguros) if (item.data.name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return "Safe"; return null; }
        void EnviarMensagem(string msg) { try { udpSender.Send(Encoding.ASCII.GetBytes(msg), msg.Length, "127.0.0.1", 8888); } catch { } }
        void EnviarErroHarvest(string n, string b) { if (!string.IsNullOrEmpty(b)) bonusPendentes.Add(b); EnviarMensagem($"ERRO;HARVEST;{n};{b}"); }
        void EnviarRequisitoOk(string b) { EnviarMensagem($"REQ_OK;{b}"); }
        void EnviarStats() { try { int hp = MeuPersonagem.health; int sp = MeuPersonagem.stamina; Vector3 pos = MeuPersonagem.transform.position; string x = pos.x.ToString("F0", CultureInfo.InvariantCulture); string y = pos.y.ToString("F0", CultureInfo.InvariantCulture); string z = pos.z.ToString("F0", CultureInfo.InvariantCulture); byte[] dados = Encoding.ASCII.GetBytes($"STATS;{hp};{sp};{x};{y};{z}"); udpSender.Send(dados, dados.Length, "127.0.0.1", 8888); } catch { } }
        void EnviarBag() { try { if (MeuPersonagem.inventory == null) return; List<string> b = new List<string>(); foreach (var s in MeuPersonagem.inventory) if (s.amount > 0 && !string.IsNullOrEmpty(s.item.name)) b.Add($"{s.item.name.Replace(";", "")}:{s.amount}"); if (b.Count > 0) EnviarMensagem("BAG;" + string.Join("~", b.ToArray())); } catch { } }
        void EnviarRadar() { try { Vector3 minhaPos = MeuPersonagem.transform.position; List<string> encontrados = new List<string>(); foreach (var mob in FindObjectsOfType<WTMob>()) { if (mob != null && mob.health > 0) { float dist = Vector3.Distance(minhaPos, mob.transform.position); if (dist <= 60) { float dx = mob.transform.position.x - minhaPos.x; float dz = mob.transform.position.z - minhaPos.z; string nome = mob.name.Replace("(Clone)", "").Replace(";", "").Trim(); string isAggro = mob.entityType?.mobBehaviour?.type == WTMobBehaviour.Mode.Aggressive ? "1" : "0"; encontrados.Add($"M:{nome}:{dist:F1}:{dx:F1}:{dz:F1}:{isAggro}:{mob.level}"); } } } foreach (var player in FindObjectsOfType<Player>()) { if (player.isLocalPlayer) continue; if (player.health <= 0) continue; float dist = Vector3.Distance(minhaPos, player.transform.position); if (dist <= 100) { float dx = player.transform.position.x - minhaPos.x; float dz = player.transform.position.z - minhaPos.z; string nome = player.name.Replace(";", "").Trim(); encontrados.Add($"P:{nome}:{dist:F1}:{dx:F1}:{dz:F1}:0:{player.level}"); } } foreach (var drop in FindObjectsOfType<WTDroppedItem>()) { if (drop != null) { float dist = Vector3.Distance(minhaPos, drop.transform.position); if (dist <= 40) { float dx = drop.transform.position.x - minhaPos.x; float dz = drop.transform.position.z - minhaPos.z; string nome = drop.itemSlot.item.name.Replace("(Clone)", "").Trim(); encontrados.Add($"D:{nome}:{dist:F1}:{dx:F1}:{dz:F1}:0:0"); } } } foreach (var obj in FindObjectsOfType<WTObject>()) { if (obj == null) continue; if (obj.GetComponent<WTDroppedItem>() != null) continue; var compMob = obj.GetComponent<WTMob>(); if (compMob != null && compMob.health > 0) continue; if (obj.GetComponent<Player>() != null) continue; float dist = Vector3.Distance(minhaPos, obj.transform.position); if (dist <= 40) { float dx = obj.transform.position.x - minhaPos.x; float dz = obj.transform.position.z - minhaPos.z; string nome = obj.name.Replace("(Clone)", "").Replace(";", "").Trim(); nome = nome.Replace("Harvestable", "").Replace("Deposit", ""); string tipo = "R"; bool isContainer = (obj.worldType != null && obj.worldType.containerSlots > 0); bool isBuilding = (obj is WTStructure); if (isContainer && !isBuilding) tipo = "C"; encontrados.Add($"{tipo}:{nome}:{dist:F1}:{dx:F1}:{dz:F1}:0:0"); } } if (encontrados.Count > 0) { string pacote = "RADAR;" + string.Join("~", encontrados.ToArray()); byte[] dados = Encoding.ASCII.GetBytes(pacote); udpSender.Send(dados, dados.Length, "127.0.0.1", 8888); } } catch { } }
        void VerificarRequisitosPendentes(WTPlayer wtPlayer) { if (bonusPendentes.Count == 0 || wtPlayer.inventory == null) return; List<string> atendidos = new List<string>(); foreach (var bonus in bonusPendentes) { bool tem = false; var mao = wtPlayer.GetEquippedRightHand(); if (mao.HasValue && ItemTemBonus(mao.Value, bonus)) tem = true; if (!tem) { foreach (var slot in wtPlayer.inventory) { if (slot.amount > 0 && ItemTemBonus(slot.item, bonus)) { tem = true; break; } } } if (tem) { atendidos.Add(bonus); EnviarRequisitoOk(bonus); } } foreach (var ok in atendidos) bonusPendentes.Remove(ok); }
        void TryHarvestObjectNative(WTPlayer wtPlayer, string partialName)
        {
            WTObject alvo = null; float menorDist = 999f;
            foreach (var obj in FindObjectsOfType<WTObject>())
            {
                if (obj == null || !obj.isActiveAndEnabled) continue;
                if (obj.worldType == null || obj.worldType.gatherSettings == null) continue;
                if (obj.name.IndexOf(partialName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    float dist = Vector3.Distance(wtPlayer.transform.position, obj.transform.position);
                    if (dist < menorDist && dist < 50) { menorDist = dist; alvo = obj; }
                }
            }
            if (alvo == null) return;
            if (menorDist > 3.0f) { MoveToXZ(wtPlayer, alvo.transform.position.x, alvo.transform.position.z); return; }
            var gather = alvo.worldType?.gatherSettings;
            if (gather == null || gather.skill == null) return;
            if (gather.bonusRequired != null)
            {
                bool tem = false; var mao = wtPlayer.GetEquippedRightHand();
                if (mao.HasValue && ItemTemBonus(mao.Value, gather.bonusRequired.name)) tem = true;
                if (!tem && wtPlayer.inventory != null) foreach (var slot in wtPlayer.inventory) if (slot.amount > 0 && ItemTemBonus(slot.item, gather.bonusRequired.name)) { tem = true; break; }
                if (!tem) { EnviarErroHarvest(partialName, gather.bonusRequired.name); return; }
            }
            wtPlayer.WorldObjectTryAction(alvo, gather.skill);
        }
        bool ItemTemBonus(Item item, string bonusName) { if (item.data is WTEquipmentItem eq && eq.bonuses != null) { if (eq.maxDurability > 0 && item.GetDurability() <= 0) return false; foreach (var b in eq.bonuses) if (b.bonusType?.name == bonusName) return true; } return false; }
        private bool CheckStamina(WTPlayer wtPlayer) { if (wtPlayer.stamina < 20) { if (!_resting) { _resting = true; try { wtPlayer.CmdCancelAction(); } catch { } SafeStopAgent(wtPlayer); } return false; } if (_resting) { if (wtPlayer.stamina < 90) { SafeStopAgent(wtPlayer); return false; } _resting = false; SafeResumeAgent(wtPlayer); } return true; }
    }
}