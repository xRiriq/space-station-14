﻿using System.Collections.Generic;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.FixedPoint;
using JetBrains.Annotations;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Shared.Chemistry.Reagent
{
    /// <summary>
    ///     Reagent effects describe behavior that occurs when a reagent is ingested and metabolized by some
    ///     organ. They only trigger when all of <see cref="Conditions"/> are satisfied.
    /// </summary>
    [ImplicitDataDefinitionForInheritors]
    [MeansImplicitUse]
    public abstract class ReagentEffect
    {
        /// <summary>
        ///     The list of conditions required for the effect to activate. Not required.
        /// </summary>
        [DataField("conditions")]
        public ReagentEffectCondition[]? Conditions;

        public abstract void Metabolize(ReagentEffectArgs args);
    }

    public enum ReactionMethod
    {
        Touch,
        Injection,
        Ingestion,
    }

    public readonly record struct ReagentEffectArgs(
        EntityUid SolutionEntity,
        EntityUid? OrganEntity,
        Solution? Source,
        ReagentPrototype Reagent,
        FixedPoint2 Metabolizing,
        IEntityManager EntityManager,
        ReactionMethod? Method
    );
}
