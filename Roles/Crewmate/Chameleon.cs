using HarmonyLib;
using Hazel;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static TOHE.Options;
using static TOHE.Translator;

namespace TOHE.Roles.Crewmate;

public static class Chameleon
{
    private static readonly int Id = 6300;
    private static List<byte> playerIdList = new();

    private static OptionItem ChameleonCooldown;
    private static OptionItem ChameleonDuration;

    private static Dictionary<byte, long> InvisTime = new();
    private static Dictionary<byte, long> lastTime = new();
    private static Dictionary<byte, int> ventedId = new();

    public static void SetupCustomOption()
    {
        SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.Chameleon);        
        ChameleonCooldown = FloatOptionItem.Create(Id + 2, "ChameleonCooldown", new(1f, 999f, 1f), 30f, TabGroup.CrewmateRoles, false).SetParent(CustomRoleSpawnChances[CustomRoles.Chameleon])
            .SetValueFormat(OptionFormat.Seconds);
        ChameleonDuration = FloatOptionItem.Create(Id + 4, "ChameleonDuration", new(1f, 999f, 1f), 15f, TabGroup.CrewmateRoles, false).SetParent(CustomRoleSpawnChances[CustomRoles.Chameleon])
            .SetValueFormat(OptionFormat.Seconds);
    }
    public static void Init()
    {
        playerIdList = new();
        InvisTime = new();
        lastTime = new();
        ventedId = new();
    }
    public static void Add(byte playerId)
    {
        playerIdList.Add(playerId);
    }
    public static bool IsEnable => playerIdList.Any();
    private static void SendRPC(PlayerControl pc)
    {
        if (pc.AmOwner) return;
        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.SetChameleonTimer, SendOption.Reliable, pc.GetClientId());
        writer.Write((InvisTime.TryGetValue(pc.PlayerId, out var x) ? x : -1).ToString());
        writer.Write((lastTime.TryGetValue(pc.PlayerId, out var y) ? y : -1).ToString());
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }
    public static void ReceiveRPC(MessageReader reader)
    {
        InvisTime = new();
        lastTime = new();
        long invis = long.Parse(reader.ReadString());
        long last = long.Parse(reader.ReadString());
        if (invis > 0) InvisTime.Add(PlayerControl.LocalPlayer.PlayerId, invis);
        if (last > 0) lastTime.Add(PlayerControl.LocalPlayer.PlayerId, last);
    }
    public static bool CanGoInvis(byte id)
        => GameStates.IsInTask && !InvisTime.ContainsKey(id) && !lastTime.ContainsKey(id);
    public static bool IsInvis(byte id) => InvisTime.ContainsKey(id);

    private static long lastFixedTime = 0;
    public static void AfterMeetingTasks()
    {
        lastTime = new();
        InvisTime = new();
        foreach (var pc in Main.AllAlivePlayerControls.Where(x => playerIdList.Contains(x.PlayerId)))
        {
            lastTime.Add(pc.PlayerId, Utils.GetTimeStamp());
            SendRPC(pc);
        }
    }
    public static void OnFixedUpdate(PlayerControl player)
    {
        if (!GameStates.IsInTask || !IsEnable) return;

        var now = Utils.GetTimeStamp();

        if (lastTime.TryGetValue(player.PlayerId, out var time) && time + (long)ChameleonCooldown.GetFloat() < now)
        {
            lastTime.Remove(player.PlayerId);
            if (!player.IsModClient()) player.Notify(GetString("ChameleonCanVent"));
            SendRPC(player);
        }

        if (lastFixedTime != now)
        {
            lastFixedTime = now;
            Dictionary<byte, long> newList = new();
            List<byte> refreshList = new();
            foreach (var it in InvisTime)
            {
                var pc = Utils.GetPlayerById(it.Key);
                if (pc == null) continue;
                var remainTime = it.Value + (long)ChameleonDuration.GetFloat() - now;
                if (remainTime < 0)
                {
                    lastTime.Add(pc.PlayerId, now);
                    pc?.MyPhysics?.RpcBootFromVent(ventedId.TryGetValue(pc.PlayerId, out var id) ? id : Main.LastEnteredVent[pc.PlayerId].Id);
                    NameNotifyManager.Notify(pc, GetString("ChameleonInvisStateOut"));
                    SendRPC(pc);
                    continue;
                }
                else if (remainTime <= 10)
                {
                    if (!pc.IsModClient()) pc.Notify(string.Format(GetString("ChameleonInvisStateCountdown"), remainTime + 1));
                }
                newList.Add(it.Key, it.Value);
            }
            InvisTime.Where(x => !newList.ContainsKey(x.Key)).Do(x => refreshList.Add(x.Key));
            InvisTime = newList;
            refreshList.Do(x => SendRPC(Utils.GetPlayerById(x)));
        }
    }
    public static void OnCoEnterVent(PlayerPhysics __instance, int ventId)
    {
        var pc = __instance.myPlayer;
        if (!AmongUsClient.Instance.AmHost || IsInvis(pc.PlayerId)) return;
        new LateTask(() =>
        {
            if (CanGoInvis(pc.PlayerId))
            {
                ventedId.Remove(pc.PlayerId);
                ventedId.Add(pc.PlayerId, ventId);

                MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(__instance.NetId, 34, SendOption.Reliable, pc.GetClientId());
                writer.WritePacked(ventId);
                AmongUsClient.Instance.FinishRpcImmediately(writer);

                InvisTime.Add(pc.PlayerId, Utils.GetTimeStamp());
                SendRPC(pc);
                NameNotifyManager.Notify(pc, GetString("ChameleonInvisState"), ChameleonDuration.GetFloat());
            }
            else
            {
                __instance.myPlayer.MyPhysics.RpcBootFromVent(ventId);
                NameNotifyManager.Notify(pc, GetString("ChameleonInvisInCooldown"));
            }
        }, 0.5f, "Chameleon Vent");
    }
    public static void OnEnterVent(PlayerControl pc, Vent vent)
    {
        if (!pc.Is(CustomRoles.Chameleon) || !IsInvis(pc.PlayerId)) return;

        InvisTime.Remove(pc.PlayerId);
        lastTime.Add(pc.PlayerId, Utils.GetTimeStamp());
        SendRPC(pc);

        pc?.MyPhysics?.RpcBootFromVent(vent.Id);
        NameNotifyManager.Notify(pc, GetString("ChameleonInvisStateOut"));
    }
    public static string GetHudText(PlayerControl pc)
    {
        if (pc == null || !GameStates.IsInTask || !PlayerControl.LocalPlayer.IsAlive()) return "";
        var str = new StringBuilder();
        if (IsInvis(pc.PlayerId))
        {
            var remainTime = InvisTime[pc.PlayerId] + (long)ChameleonDuration.GetFloat() - Utils.GetTimeStamp();
            str.Append(string.Format(GetString("ChameleonInvisStateCountdown"), remainTime + 1));
        }
        else if (lastTime.TryGetValue(pc.PlayerId, out var time))
        {
            var cooldown = time + (long)ChameleonCooldown.GetFloat() - Utils.GetTimeStamp();
            str.Append(string.Format(GetString("ChameleonInvisCooldownRemain"), cooldown));
        }
        else
        {
            str.Append(GetString("ChameleonCanVent"));
        }
        return str.ToString();
    }

    public static bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        if (!IsInvis(killer.PlayerId)) return true;
        killer.SetKillCooldown();
        target.RpcCheckAndMurder(target);
        target.SetRealKiller(killer);
        return false;
    }
}