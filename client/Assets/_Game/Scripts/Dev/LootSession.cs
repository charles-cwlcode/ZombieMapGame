using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using ZombieMap.Client.Net;
using ZombieMap.Core.Geo;
using ZombieMap.Core.Items;
using ZombieMap.Core.Protocol;

namespace ZombieMap.Client.Dev
{
    /// <summary>
    /// Client-side state for looting: the player's inventory, the containers searched in the current
    /// building, and searches in progress. Every change goes through the server; this only mirrors
    /// what the server last said. Shared by the Building and Inventory tabs.
    /// </summary>
    public sealed class LootSession
    {
        public sealed class ContainerState
        {
            public SearchStatus Status;
            public float FinishesAt;
            public float Duration;
            public ItemContainer Contents;
            public bool Requesting;
        }

        private readonly GameClient _client;
        private readonly Func<LatLng> _position;
        private readonly Dictionary<string, ContainerState> _containers = new Dictionary<string, ContainerState>();

        public PlayerInventory Inventory { get; private set; }
        public string BuildingId { get; private set; }
        /// <summary>The container the player last opened (for "Put" in the Inventory tab).</summary>
        public string OpenContainerId { get; private set; }
        public string Message { get; private set; } = "";
        public bool Busy { get; private set; }

        public LootSession(GameClient client, Func<LatLng> position)
        {
            _client = client;
            _position = position;
            _client.Welcomed += welcome => { _containers.Clear(); _ = RefreshInventory(); };
        }

        public ItemContainer OpenContainer =>
            OpenContainerId != null && _containers.TryGetValue(OpenContainerId, out var c) ? c.Contents : null;

        public ContainerState Container(string containerId) =>
            _containers.TryGetValue(containerId, out var c) ? c : null;

        /// <summary>Switch to another building: forget the old one's containers (the server keeps them).</summary>
        public void EnterBuilding(string buildingId)
        {
            if (buildingId == BuildingId) return;
            BuildingId = buildingId;
            OpenContainerId = null;
            _containers.Clear();
            Message = "";
        }

        /// <summary>Call every frame: finishes searches whose time is up.</summary>
        public void Tick()
        {
            foreach (var pair in _containers)
            {
                var c = pair.Value;
                if (c.Status == SearchStatus.Searching && !c.Requesting && Time.unscaledTime >= c.FinishesAt)
                    _ = Search(pair.Key);
            }
        }

        public async Task RefreshInventory()
        {
            if (!_client.IsConnected) return;
            await Run(async () =>
                Inventory = (await _client.Connection.RequestAsync<InventoryMessage>(MessageTypes.InventoryGet, new { })).Inventory);
        }

        public async Task Search(string containerId)
        {
            if (BuildingId == null) return;
            if (!_containers.TryGetValue(containerId, out var state)) _containers[containerId] = state = new ContainerState();
            state.Requesting = true;
            OpenContainerId = containerId;
            bool ok = await Run(async () =>
            {
                var reply = await _client.Connection.RequestAsync<LootMessage>(MessageTypes.LootSearch,
                    new LootSearchRequest { BuildingId = BuildingId, ContainerId = containerId, PlayerPosition = _position() });
                Apply(reply);
            }, trackBusy: false);
            state.Requesting = false;
            // A failed search (e.g. walked out of range) stops, instead of retrying every frame.
            if (!ok && state.Contents == null) _containers.Remove(containerId);
        }

        public Task Take(string containerId, string stackId, int count = 0, string to = PlayerInventory.MainId) =>
            Run(async () => Apply(await _client.Connection.RequestAsync<LootMessage>(MessageTypes.LootTake, new LootTakeRequest
            {
                BuildingId = BuildingId, ContainerId = containerId, StackId = stackId, Count = count, To = to, PlayerPosition = _position(),
            })));

        public Task Put(string stackId, int count = 0) =>
            OpenContainerId == null ? Task.CompletedTask :
            Run(async () => Apply(await _client.Connection.RequestAsync<LootMessage>(MessageTypes.LootPut, new LootPutRequest
            {
                BuildingId = BuildingId, ContainerId = OpenContainerId, StackId = stackId, Count = count, PlayerPosition = _position(),
            })));

        public Task Move(string stackId, string to) =>
            Run(async () => Inventory = (await _client.Connection.RequestAsync<InventoryMessage>(MessageTypes.InventoryMove,
                new InventoryMoveRequest { StackId = stackId, To = to })).Inventory);

        public Task Equip(string stackId) =>
            Run(async () => Inventory = (await _client.Connection.RequestAsync<InventoryMessage>(MessageTypes.InventoryEquip,
                new InventoryEquipRequest { StackId = stackId })).Inventory);

        public Task Unequip() =>
            Run(async () => Inventory = (await _client.Connection.RequestAsync<InventoryMessage>(MessageTypes.InventoryUnequip, new { })).Inventory);

        public Task Drop(string stackId, int count = 0) =>
            Run(async () => Inventory = (await _client.Connection.RequestAsync<InventoryMessage>(MessageTypes.InventoryDrop,
                new InventoryDropRequest { StackId = stackId, Count = count })).Inventory);

        private void Apply(LootMessage reply)
        {
            if (reply.Inventory != null) Inventory = reply.Inventory;
            if (reply.BuildingId != BuildingId) return;
            if (!_containers.TryGetValue(reply.ContainerId, out var state)) _containers[reply.ContainerId] = state = new ContainerState();
            state.Status = reply.Status;
            if (reply.Status == SearchStatus.Searching)
            {
                if (state.Duration <= 0) state.Duration = (float)reply.SecondsLeft;
                state.FinishesAt = Time.unscaledTime + (float)reply.SecondsLeft;
            }
            else
            {
                state.Contents = reply.Contents;
            }
        }

        private async Task<bool> Run(Func<Task> action, bool trackBusy = true)
        {
            if (!_client.IsConnected) { Message = "Not connected."; return false; }
            if (trackBusy) Busy = true;
            try
            {
                await action();
                Message = "";
                return true;
            }
            catch (Exception ex)
            {
                Message = ex.Message;
                return false;
            }
            finally
            {
                if (trackBusy) Busy = false;
            }
        }
    }
}
