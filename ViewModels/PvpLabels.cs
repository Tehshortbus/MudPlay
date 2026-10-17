using MudPlay.Models.GameData;

namespace MudPlay.ViewModels;

// The wording for the PvP choices, shared by the player edit dialog, the Players
// table and the PvP settings tab so the three never drift apart.
public static class PvpLabels
{
    public static readonly IReadOnlyList<PlayerRelationship> Relationships = new[]
    {
        PlayerRelationship.Neutral, PlayerRelationship.Friend, PlayerRelationship.Enemy,
    };

    public static readonly IReadOnlyList<PvpAction> Actions = new[]
    {
        PvpAction.HangUp, PvpAction.FleeThenHangUp, PvpAction.Flee,
        PvpAction.Attack, PvpAction.ChaseAttack,
    };

    public static string Of(PlayerRelationship relationship) => relationship switch
    {
        PlayerRelationship.Friend => "Friend",
        PlayerRelationship.Enemy  => "Enemy",
        _                         => "Neutral",
    };

    public static string Of(PvpAction action) => action switch
    {
        PvpAction.HangUp         => "Hang up immediately",
        PvpAction.FleeThenHangUp => "Flee, then hang up",
        PvpAction.Flee           => "Flee (come back later)",
        PvpAction.Attack         => "Attack",
        _                        => "Chase and attack",
    };

    // A player's own response, where null means the PvP settings' general one.
    public const string UseGeneral = "Use the PvP settings";
}
