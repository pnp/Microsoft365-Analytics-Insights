namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// How embedded Copilot is in one licensed user's working week. Ordered from worst to best so a
    /// distribution reads left to right as a maturity curve.
    ///
    /// The split between <see cref="NeverUsed"/> and <see cref="Dormant"/> is the one that pays for
    /// itself: a user who never started needs onboarding, a user who tried Copilot and stopped needs
    /// either a conversation or their seat reclaiming, and averaging the two together hides both.
    /// </summary>
    public enum AdoptionBand
    {
        /// <summary>Holds a seat and has no recorded Copilot activity at all within the history window.</summary>
        NeverUsed = 0,

        /// <summary>Used Copilot before the reporting window but not once inside it.</summary>
        Dormant = 1,

        /// <summary>Some activity in the window, but well below a working habit.</summary>
        Trialling = 2,

        /// <summary>Using Copilot regularly enough to be building a habit.</summary>
        Developing = 3,

        /// <summary>Copilot is part of the working week.</summary>
        Established = 4,

        /// <summary>Deep, frequent, multi-app use - the people to recruit as internal advocates.</summary>
        Champion = 5,
    }
}
