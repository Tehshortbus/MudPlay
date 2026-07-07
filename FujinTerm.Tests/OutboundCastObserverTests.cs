using System;
using System.Collections.Generic;
using System.Text;
using FujinTerm.Game.Combat;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Coverage for <see cref="OutboundCastObserver"/> — the wire sniffer that arms
/// the combat engine's between-round-cast resume when the player hand-types a
/// spell cast-code. A recognised cast-code (bare or with a target) fires the
/// signal; anything else stays silent.
/// </summary>
public sealed class OutboundCastObserverTests
{
    // Stand-in cast-code table: the real observer resolves through
    // Spellbook.FindByCastCode; here a small set keeps the test self-contained.
    private static readonly HashSet<string> CastCodes =
        new(StringComparer.OrdinalIgnoreCase) { "swan", "heal", "cure" };

    private static (OutboundCastObserver obs, Func<int> fires) New()
    {
        int fires = 0;
        OutboundCastObserver obs = new(
            isCastCode: c => CastCodes.Contains(c),
            onManualCast: () => fires++);
        return (obs, () => fires);
    }

    private static void Send(OutboundCastObserver obs, string command)
        => obs.ObserveOutbound(Encoding.Latin1.GetBytes(command + "\r"));

    [Fact]
    public void BareCastCode_ArmsResume()
    {
        (OutboundCastObserver obs, Func<int> fires) = New();
        Send(obs, "swan");
        Assert.Equal(1, fires());
    }

    [Fact]
    public void CastCodeWithTarget_ArmsResume()
    {
        (OutboundCastObserver obs, Func<int> fires) = New();
        Send(obs, "swan rat"); // first token is the cast-code, rest is the target
        Assert.Equal(1, fires());
    }

    [Fact]
    public void CastCode_IsCaseInsensitive()
    {
        (OutboundCastObserver obs, Func<int> fires) = New();
        Send(obs, "  SWAN  ");
        Assert.Equal(1, fires());
    }

    [Fact]
    public void NonCastCommand_StaysSilent()
    {
        (OutboundCastObserver obs, Func<int> fires) = New();
        Send(obs, "a rat");   // attack verb, not a cast-code
        Send(obs, "n");       // cardinal move
        Send(obs, "look n");  // peek
        Send(obs, "get all"); // loot
        Assert.Equal(0, fires());
    }

    [Fact]
    public void EmptyOrOversized_Ignored()
    {
        (OutboundCastObserver obs, Func<int> fires) = New();
        obs.ObserveOutbound(ReadOnlySpan<byte>.Empty);
        obs.ObserveOutbound(Encoding.Latin1.GetBytes("\r\n"));
        obs.ObserveOutbound(Encoding.Latin1.GetBytes(new string('x', 200)));
        Assert.Equal(0, fires());
    }
}
