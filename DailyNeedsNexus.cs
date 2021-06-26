/*
DailyNeedsNexus - A Torch plugin to sync Daily Needs across Nexus servers.

The Daily Needs mods store per-player data locally on each server.
A player can transfer to another server with the Nexus Torch plugin, but
their Daily Needs data is not transferred.

Ideally to sync Daily Needs player data, we would like to know when players
change server, and relay their Daily Needs information.

Unfortunately Nexus does not expose this event, so the best we can do is to
watch the player list and see when players disconnect:
- it could be that they just left the game, which is not very interesting,
because next time they connect they'll just get back on this same server,
and the Daily Needs information remain locally the same
- or it could be that they are transferring to another server, then we can
make sure to relay the Daily Needs information to the other server

Unfortunately, we also do not know where they transfer to, so we send their
current Daily Needs data to all servers, and all of them can update.
*/
using Nexus.API;
using NLog;
using ProtoBuf;
using Sandbox.Engine.Multiplayer;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Session;
using Torch.Session;
using VRage.Collections;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.GameServices;
using VRage.ModAPI;
using VRageMath;

namespace StalkR.DailyNeedsNexus
{
    public class DailyNeedsNexus : TorchPluginBase
    {
        private const ushort DAILY_NEEDS_NEXUS_MOD_ID = 0x28a1;
        private const string DAILY_NEEDS_WORKSHOP_ID = "1608841667";

        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private TorchSessionManager _sessionManager = null;
        private NexusAPI _api = new NexusAPI(DAILY_NEEDS_NEXUS_MOD_ID);
        private object _dataStore;

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            _sessionManager = Torch.Managers.GetManager<TorchSessionManager>();
            if (_sessionManager == null)
            {
                Log.Error("Missing session manager - abort!");
                return;
            }
            _sessionManager.SessionStateChanged += SessionChanged;
        }

        public override void Dispose()
        {
            if (_sessionManager != null)
                _sessionManager.SessionStateChanged -= SessionChanged;
            _sessionManager = null;
        }

        private void SessionChanged(ITorchSession session, TorchSessionState state)
        {
            switch (state)
            {
                case TorchSessionState.Loaded:
                    MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(DAILY_NEEDS_NEXUS_MOD_ID, OnMessageReceived);
                    MyMultiplayer.Static.ClientLeft += OnClientLeft;
                    break;
                case TorchSessionState.Unloading:
                    MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(DAILY_NEEDS_NEXUS_MOD_ID, OnMessageReceived);
                    if (MyMultiplayer.Static != null)
                        MyMultiplayer.Static.ClientLeft -= OnClientLeft;
                    break;
            }
        }

        private void OnClientLeft(ulong steamID, MyChatMemberStateChangeEnum stateChange)
        {
            FindPlayerDataStore();
            if (_dataStore == null) return;
            var args = new object[] { new MyFakePlayer(steamID) };
            object playerData = _dataStore.GetType().GetMethod("get").Invoke(_dataStore, args);
            float get(string name) => (float)playerData.GetType().GetField(name).GetValue(playerData);

            DailyNeedsNexusMessage msg = new DailyNeedsNexusMessage
            {
                SteamID = steamID,
                Hunger = get("hunger"),
                Thirst = get("thirst"),
                Fatigue = get("fatigue")
            };

            NexusServerSideAPI.SendMessageToAllServers(ref _api, MyAPIGateway.Utilities.SerializeToBinary<DailyNeedsNexusMessage>(msg));
            //Log.Info("Player disconnected from this server, broadcasting: steamID=" + msg.SteamID + ", hunger=" + msg.Hunger + ", thirst=" + msg.Thirst + ", fatigue=" + msg.Fatigue);
        }

        private void OnMessageReceived(ushort HandlerId, byte[] Data, ulong SteamID, bool FromServer)
        {
            // Only consider trusted server messages, i.e. from Nexus itself, not untrusted player messages.
            if (!FromServer)
                return;

            DailyNeedsNexusMessage msg;
            try
            {
                msg = MyAPIGateway.Utilities.SerializeFromBinary<DailyNeedsNexusMessage>(Data);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Invalid Nexus cross-server message for DailyNeedsNexus");
                return;
            }

            FindPlayerDataStore();
            if (_dataStore == null) return;
            var args = new object[] { new MyFakePlayer(msg.SteamID) };
            object playerData = _dataStore.GetType().GetMethod("get").Invoke(_dataStore, args);
            void set(string name, float value) => playerData.GetType().GetField(name).SetValue(playerData, value);
            set("hunger", msg.Hunger);
            set("thirst", msg.Thirst);
            set("fatigue", msg.Fatigue);
            //Log.Info("Player disconnected from another server, setting: steamID=" + msg.SteamID + ", hunger=" + msg.Hunger + ", thirst=" + msg.Thirst + ", fatigue=" + msg.Fatigue);
        }

        private void FindPlayerDataStore()
        {
            if (_dataStore != null)
                return;
            foreach (var script in Sandbox.Game.World.MyScriptManager.Static.Scripts)
            {
                if (Path.GetFileName(script.Key.String).Split('.')[0] != DAILY_NEEDS_WORKSHOP_ID)
                    continue;
                foreach (Type type in script.Value.GetTypes())
                {
                    if (type.FullName != "Stollie.DailyNeeds.Server")
                        continue;
                    FieldInfo info = type.GetField("mPlayerDataStore", BindingFlags.NonPublic | BindingFlags.Static);
                    _dataStore = info.GetValue(null);
                    return;
                }
                Log.Error("Could not find Daily Needs player data store, plz report bug");
                return;
            }
            Log.Error("Cannot find Daily Needs mod, is it installed?");
        }

        [ProtoContract]
        private class DailyNeedsNexusMessage
        {
            [ProtoMember(1)] public ulong SteamID;
            [ProtoMember(2)] public float Hunger;
            [ProtoMember(3)] public float Thirst;
            [ProtoMember(4)] public float Fatigue;
        }

        private class MyFakePlayer : IMyPlayer
        {
            internal ulong _steamID;
            internal MyFakePlayer(ulong steamID)
            {
                _steamID = steamID;
            }
            // Compiler complains it is never used: yes, it is just here to satisfy the interface.
            #pragma warning disable 67
            public event Action<IMyPlayer, IMyIdentity> IdentityChanged;
            #pragma warning restore 67
            public IMyNetworkClient Client { get; }
            public MyRelationsBetweenPlayerAndBlock GetRelationTo(long playerId) { throw new NotImplementedException(); }
            public HashSet<long> Grids { get; }
            public void AddGrid(long gridEntityId) { throw new NotImplementedException(); }
            public void RemoveGrid(long gridEntityId) { throw new NotImplementedException(); }
            public IMyEntityController Controller { get; }
            public Vector3D GetPosition() { throw new NotImplementedException(); }
            public ulong SteamUserId { get => _steamID; }
            public string DisplayName { get; }
            public long PlayerID { get; }
            public long IdentityId { get; }
            public bool IsAdmin { get; }
            public bool IsPromoted { get; }
            public MyPromoteLevel PromoteLevel { get; }
            public IMyCharacter Character { get; }
            public bool IsBot { get; }
            public IMyIdentity Identity { get; }
            public ListReader<long> RespawnShip { get; }
            public List<Vector3> BuildColorSlots { get; set; }
            public ListReader<Vector3> DefaultBuildColorSlots { get; }
            public Vector3 SelectedBuildColor { get; set; }
            public int SelectedBuildColorSlot { get; set; }
            public void ChangeOrSwitchToColor(Vector3 color) { throw new NotImplementedException(); }
            public void SetDefaultColors() { throw new NotImplementedException(); }
            public void SpawnIntoCharacter(IMyCharacter character) { throw new NotImplementedException(); }
            public void SpawnAt(MatrixD worldMatrix, Vector3 velocity, IMyEntity spawnedBy, bool findFreePlace = true, string modelName = null, Color? color = null) { throw new NotImplementedException(); }
            public void SpawnAt(MatrixD worldMatrix, Vector3 velocity, IMyEntity spawnedBy) { throw new NotImplementedException(); }
            public bool TryGetBalanceInfo(out long balance) { throw new NotImplementedException(); }
            public string GetBalanceShortString() { throw new NotImplementedException(); }
            public void RequestChangeBalance(long amount) { throw new NotImplementedException(); }
        }
    }
}
