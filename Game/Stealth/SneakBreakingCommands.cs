using System;
using System.Collections.Generic;

namespace MudPlay.Game.Stealth;

// Which of the client's own commands end a sneak, by verb (GAME_MECHANICS "Sneaking —
// commands, equip order, and the sneak state machine" → "What ends a sneak"). Attacks
// and spell casts are left to the combat engine and the cast path, which already reset
// stealth; `sn` re-sneaks rather than ends it. An item command (`use`, `read`, `eat`,
// `drink`, `light`) ends one only when the item's spell is cast, which
// ItemUseStealthRule answers.
public static class SneakBreakingCommands
{
    private static readonly HashSet<string> BreakingVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "wear", "eq", "equip", "ready", "rem", "remove",
        "sea", "search", "open", "close", "lock", "pick", "picklock", "bash", "disarm",
        "give", "rob", "aid", "drag", "move", "follow",
        "buy", "sell", "dep", "deposit", "with", "withdraw", "stock", "unstock", "markup",
        "invite", "share", "say", "yell",
        "rest", "med", "medi", "meditate", "quit",
    };

    private static readonly HashSet<string> RestVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "rest", "med", "medi", "meditate",
    };

    // On the sneak list, but the engine has no clear of the hidden state beside them
    // (GAME_MECHANICS "Hiding — sneak vs hide, the hide state machine, and search
    // reveals" → "What ends a hide on Stock").
    private static readonly HashSet<string> SneakOnlyVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "disarm", "follow", "move",
    };

    // Say-channel relays that carry a walk step or a flight. They go out even while a
    // sneak is being kept: a step relay belongs to a step that ends the sneak anyway
    // (teleport / boat keyword, trap, token use), @panic tells the party to hang up,
    // which we do right after, and @heal is a follower too hurt to wait (user,
    // 2026-10-30: it fires even if it breaks the sneak).
    private static readonly string[] MustSendRelays = { ".@party ", ".@trap ", ".@panic", ".@heal" };

    // True when sending this command ends a sneak. A ShadowRest character's rest
    // keeps it (Paradigm; GAME_MECHANICS "ShadowRest"). With no item rule to ask, an
    // item command counts as ending it, as the rule does wherever it can't tell.
    public static bool EndsSneak(string command, bool shadowRest = false, ItemUseStealthRule? items = null)
    {
        string c = command.Trim();
        if (c.Length == 0) return false;
        if (IsSayForm(c)) return true;
        string verb = FirstWord(c);
        if (shadowRest && RestVerbs.Contains(verb)) return false;
        if (ItemUseStealthRule.VerbOf(verb) is { } itemVerb)
            return items?.EndsStealth(itemVerb, c[verb.Length..].Trim()) ?? true;
        return BreakingVerbs.Contains(verb);
    }

    // True for the bare word that hides the character (`hid`, `hide`). With an object
    // after it the command stashes that object instead.
    public static bool IsHideAttempt(string command)
    {
        string c = command.Trim();
        return c.Equals("hid", StringComparison.OrdinalIgnoreCase)
            || c.Equals("hide", StringComparison.OrdinalIgnoreCase);
    }

    // For a command that ends a sneak: whether it ends a hide as well.
    public static bool AlsoEndsHide(string command)
    {
        string c = command.Trim();
        return c.Length > 0 && (IsSayForm(c) || !SneakOnlyVerbs.Contains(FirstWord(c)));
    }

    // True when the command can simply wait while a sneak is being kept, and go out
    // unchanged later: party invites and say-channel chatter (level-up, ailment and
    // hazard calls, @-command replies), but not the relays a walk step or a flight
    // needs.
    public static bool CanWait(string command)
    {
        string c = command.Trim();
        if (c.Length == 0) return false;
        if (IsSayForm(c))
        {
            foreach (string relay in MustSendRelays)
                if (c.StartsWith(relay, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c, relay.Trim(), StringComparison.OrdinalIgnoreCase))
                    return false;
            return true;
        }
        string verb = FirstWord(c);
        return verb.Equals("invite", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("yell", StringComparison.OrdinalIgnoreCase);
    }

    // `.msg` (room say), `>name msg` (directed say), `"msg` (yell), `say msg`.
    private static bool IsSayForm(string c) =>
        c[0] is '.' or '>' or '"' || c.StartsWith("say ", StringComparison.OrdinalIgnoreCase);

    private static string FirstWord(string c)
    {
        int space = c.IndexOf(' ');
        return space < 0 ? c : c[..space];
    }
}
