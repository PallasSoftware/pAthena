
namespace Athena.Net.MapServer.World;

// Why a requested base-stat increase may not proceed - the server derives which reason
// applies; a client (or any caller) never supplies or trusts a target stat value or Status
// Point balance directly, matching CharacterSkillService's SkillUpgradeRejectionReason
// convention.
public enum StatIncreaseRejectionReason
{
    UnknownStat,
    InvalidIncreaseAmount,
    MaxValueReached,
    InsufficientStatusPoints,
}

// Immutable outcome of CharacterStatService.ValidateIncrease. Exactly one of Valid/Rejected is
// meaningful, discriminated by IsValid - mirrors SkillUpgradeValidationResult's own contract.
public readonly record struct StatIncreaseValidationResult(
    bool IsValid,
    StatIncreaseRejectionReason? RejectionReason,
    ushort PreviousValue,
    ushort NewValue,
    uint StatusPointsSpent,
    uint RemainingStatusPoints)
{
    public static StatIncreaseValidationResult Valid(ushort previousValue, ushort newValue, uint statusPointsSpent, uint remainingStatusPoints) =>
        new(true, null, previousValue, newValue, statusPointsSpent, remainingStatusPoints);

    public static StatIncreaseValidationResult Rejected(StatIncreaseRejectionReason reason) =>
        new(false, reason, 0, 0, 0, 0);
}
