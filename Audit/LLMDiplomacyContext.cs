// LLM Diplomacy Context — C# struct definition
// Copy from ECS World for LLM evaluation. Immutable snapshot.
// See: Audit/Design_LLM_Diplomacy_Engine.md

namespace StarDrive.ECS.Diplomacy
{
    /// <summary>
    /// Context data copied from ECS World and Relationship state for LLM evaluation.
    /// All fields serializable (primitive or DTO) for JSON/HTTP.
    /// </summary>
    public readonly struct LLMDiplomacyContext
    {
        // ---- Identity ----
        public int UsEmpireId { get; init; }
        public int ThemEmpireId { get; init; }
        public string UsEmpireName { get; init; }
        public string ThemEmpireName { get; init; }
        public string UsPersonality { get; init; }
        public string ThemPersonality { get; init; }

        // ---- Relationship State ----
        public float Trust { get; init; }
        public float Threat { get; init; }
        public float TotalAnger { get; init; }
        public float Anger_Territorial { get; init; }
        public float Anger_Military { get; init; }
        public float Anger_Diplomatic { get; init; }
        public float Anger_ShipsInBorders { get; init; }

        public bool Treaty_Alliance { get; init; }
        public bool Treaty_NAPact { get; init; }
        public bool Treaty_Trade { get; init; }
        public bool Treaty_OpenBorders { get; init; }
        public bool Treaty_Peace { get; init; }
        public int PeaceTurnsRemaining { get; init; }
        public int TurnsKnown { get; init; }
        public int TurnsAllied { get; init; }
        public int TurnsInOpenBorders { get; init; }
        public int TurnsAbove95 { get; init; }

        public bool AtWar { get; init; }
        public string WarType { get; init; }
        public string WarState { get; init; }

        public bool HaveRejected_Alliance { get; init; }
        public bool HaveRejected_NAPact { get; init; }
        public bool HaveRejected_TRADE { get; init; }
        public bool HaveRejected_OpenBorders { get; init; }

        public float AvailableTrust { get; init; }
        public int NumTechsWeGave { get; init; }

        // ---- Offer ----
        public bool TheirOffer_Alliance { get; init; }
        public bool TheirOffer_Peace { get; init; }
        public bool TheirOffer_NAPact { get; init; }
        public bool TheirOffer_Trade { get; init; }
        public bool TheirOffer_OpenBorders { get; init; }
        public string[] TheirOffer_Technologies { get; init; }
        public string[] TheirOffer_Colonies { get; init; }
        public string[] OurOffer_Technologies { get; init; }
        public string[] OurOffer_Colonies { get; init; }
        public string Attitude { get; init; }

        // ---- Strength / Threat ----
        public float OurMilitaryStrength { get; init; }
        public float TheirMilitaryStrength { get; init; }
        public bool TheyAreAlliedWithOurEnemies { get; init; }
        public int NumEmpiresAlliedWithThem { get; init; }

        // ---- History ----
        public LLMDiplomaticEvent[] RecentEvents { get; init; }
    }

    public readonly struct LLMDiplomaticEvent
    {
        public string Type { get; init; }
        public float StarDate { get; init; }
        public int ActorEmpireId { get; init; }
        public int TargetEmpireId { get; init; }
        public string LlmNarrative { get; init; }
    }
}
