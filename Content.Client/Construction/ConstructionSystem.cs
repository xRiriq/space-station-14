﻿using System;
using System.Collections.Generic;
using Content.Shared.Construction;
using Content.Shared.Construction.Prototypes;
using Content.Shared.Examine;
using Content.Shared.Input;
using Content.Shared.Interaction.Helpers;
using JetBrains.Annotations;
using Robust.Client.GameObjects;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.Input.Binding;
using Robust.Shared.IoC;
using Robust.Shared.Localization;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;


namespace Content.Client.Construction
{
    /// <summary>
    /// The client-side implementation of the construction system, which is used for constructing entities in game.
    /// </summary>
    [UsedImplicitly]
    public class ConstructionSystem : SharedConstructionSystem
    {
        [Dependency] private readonly IPlayerManager _playerManager = default!;
        [Dependency] private readonly IPrototypeManager _prototypeManager = default!;

        private readonly Dictionary<int, ConstructionGhostComponent> _ghosts = new();
        private readonly Dictionary<string, ConstructionGuide> _guideCache = new();

        private int _nextId;

        public bool CraftingEnabled { get; private set; }

        /// <inheritdoc />
        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<PlayerAttachSysMessage>(HandlePlayerAttached);
            SubscribeNetworkEvent<AckStructureConstructionMessage>(HandleAckStructure);
            SubscribeNetworkEvent<ResponseConstructionGuide>(OnConstructionGuideReceived);

            CommandBinds.Builder
                .Bind(ContentKeyFunctions.OpenCraftingMenu,
                    new PointerInputCmdHandler(HandleOpenCraftingMenu))
                .Bind(EngineKeyFunctions.Use,
                    new PointerInputCmdHandler(HandleUse))
                .Register<ConstructionSystem>();

            SubscribeLocalEvent<ConstructionGhostComponent, ExaminedEvent>(HandleConstructionGhostExamined);
        }

        private void OnConstructionGuideReceived(ResponseConstructionGuide ev)
        {
            _guideCache[ev.ConstructionId] = ev.Guide;
            ConstructionGuideAvailable?.Invoke(this, ev.ConstructionId);
        }

        /// <inheritdoc />
        public override void Shutdown()
        {
            base.Shutdown();

            CommandBinds.Unregister<ConstructionSystem>();
        }

        public ConstructionGuide? GetGuide(ConstructionPrototype prototype)
        {
            if (_guideCache.TryGetValue(prototype.ID, out var guide))
                return guide;

            RaiseNetworkEvent(new RequestConstructionGuide(prototype.ID));
            return null;
        }

        private void HandleConstructionGhostExamined(EntityUid uid, ConstructionGhostComponent component, ExaminedEvent args)
        {
            if (component.Prototype == null) return;

            args.PushMarkup(Loc.GetString(
                "construction-ghost-examine-message",
                ("name", component.Prototype.Name)));

            if (!_prototypeManager.TryIndex(component.Prototype.Graph, out ConstructionGraphPrototype? graph))
                return;

            var startNode = graph.Nodes[component.Prototype.StartNode];

            if (!graph.TryPath(component.Prototype.StartNode, component.Prototype.TargetNode, out var path) ||
                !startNode.TryGetEdge(path[0].Name, out var edge))
            {
                return;
            }

            edge.Steps[0].DoExamine(args);
        }

        public event EventHandler<CraftingAvailabilityChangedArgs>? CraftingAvailabilityChanged;
        public event EventHandler<string>? ConstructionGuideAvailable;
        public event EventHandler? ToggleCraftingWindow;

        private void HandleAckStructure(AckStructureConstructionMessage msg)
        {
            ClearGhost(msg.GhostId);
        }

        private void HandlePlayerAttached(PlayerAttachSysMessage msg)
        {
            var available = IsCrafingAvailable(msg.AttachedEntity);
            UpdateCraftingAvailability(available);
        }

        private bool HandleOpenCraftingMenu(in PointerInputCmdHandler.PointerInputCmdArgs args)
        {
            if (args.State == BoundKeyState.Down)
                ToggleCraftingWindow?.Invoke(this, EventArgs.Empty);
            return true;
        }

        private void UpdateCraftingAvailability(bool available)
        {
            if (CraftingEnabled == available)
                return;

            CraftingAvailabilityChanged?.Invoke(this, new CraftingAvailabilityChangedArgs(available));
            CraftingEnabled = available;
        }

        private static bool IsCrafingAvailable(IEntity? entity)
        {
            if (entity == null)
                return false;

            // TODO: Decide if entity can craft, using capabilities or something
            return true;
        }

        private bool HandleUse(in PointerInputCmdHandler.PointerInputCmdArgs args)
        {
            if (!args.EntityUid.IsValid() || !args.EntityUid.IsClientSide())
                return false;

            var entity = EntityManager.GetEntity(args.EntityUid);

            if (!entity.TryGetComponent<ConstructionGhostComponent>(out var ghostComp))
                return false;

            TryStartConstruction(ghostComp.GhostId);
            return true;
        }

        /// <summary>
        /// Creates a construction ghost at the given location.
        /// </summary>
        public void SpawnGhost(ConstructionPrototype prototype, EntityCoordinates loc, Direction dir)
        {
            var user = _playerManager.LocalPlayer?.ControlledEntity;

            // This InRangeUnobstructed should probably be replaced with "is there something blocking us in that tile?"
            if (user == null || GhostPresent(loc) || !user.InRangeUnobstructed(loc, 20f, ignoreInsideBlocker: prototype.CanBuildInImpassable)) return;

            foreach (var condition in prototype.Conditions)
            {
                if (!condition.Condition(user, loc, dir))
                    return;
            }

            var ghost = EntityManager.SpawnEntity("constructionghost", loc);
            var comp = ghost.GetComponent<ConstructionGhostComponent>();
            comp.Prototype = prototype;
            comp.GhostId = _nextId++;
            ghost.Transform.LocalRotation = dir.ToAngle();
            _ghosts.Add(comp.GhostId, comp);
            var sprite = ghost.GetComponent<SpriteComponent>();
            sprite.Color = new Color(48, 255, 48, 128);
            sprite.AddBlankLayer(0); // There is no way to actually check if this already exists, so we blindly insert a new one
            sprite.LayerSetSprite(0, prototype.Icon);
            sprite.LayerSetShader(0, "unshaded");
            sprite.LayerSetVisible(0, true);
        }

        /// <summary>
        /// Checks if any construction ghosts are present at the given position
        /// </summary>
        private bool GhostPresent(EntityCoordinates loc)
        {
            foreach (var ghost in _ghosts)
            {
                if (ghost.Value.Owner.Transform.Coordinates.Equals(loc)) return true;
            }

            return false;
        }

        private void TryStartConstruction(int ghostId)
        {
            var ghost = _ghosts[ghostId];

            if (ghost.Prototype == null)
            {
                throw new ArgumentException($"Can't start construction for a ghost with no prototype. Ghost id: {ghostId}");
            }

            var transform = ghost.Owner.Transform;
            var msg = new TryStartStructureConstructionMessage(transform.Coordinates, ghost.Prototype.ID, transform.LocalRotation, ghostId);
            RaiseNetworkEvent(msg);
        }

        /// <summary>
        /// Starts constructing an item underneath the attached entity.
        /// </summary>
        public void TryStartItemConstruction(string prototypeName)
        {
            RaiseNetworkEvent(new TryStartItemConstructionMessage(prototypeName));
        }

        /// <summary>
        /// Removes a construction ghost entity with the given ID.
        /// </summary>
        public void ClearGhost(int ghostId)
        {
            if (_ghosts.TryGetValue(ghostId, out var ghost))
            {
                ghost.Owner.QueueDelete();
                _ghosts.Remove(ghostId);
            }
        }

        /// <summary>
        /// Removes all construction ghosts.
        /// </summary>
        public void ClearAllGhosts()
        {
            foreach (var (_, ghost) in _ghosts)
            {
                ghost.Owner.QueueDelete();
            }

            _ghosts.Clear();
        }
    }

    public class CraftingAvailabilityChangedArgs : EventArgs
    {
        public bool Available { get; }

        public CraftingAvailabilityChangedArgs(bool available)
        {
            Available = available;
        }
    }
}
