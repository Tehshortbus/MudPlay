using System;
using FujinTerm.Game.Inventory;
using FujinTerm.Models.GameData;

namespace FujinTerm.Game.Remote;

/// <summary>
/// Consumer of <see cref="RemoteCommandManager"/> for
/// <c>@equip-&lt;setname&gt;</c> — a permitted party member asks us to swap to
/// one of our saved gear sets. The set keyword is the suffix after
/// <c>@equip-</c> (e.g. <c>@equip-fighting</c>); the engine's prefix router
/// folds it in as <see cref="RemoteCommandContext.Args"/>[0]. Resolves the set
/// by keyword (then name) and drives <see cref="EquipmentManager.ApplyByKeyword"/>.
/// </summary>
/// <remarks>
/// <see cref="PlayerRemoteControls.ExecuteCommands"/>-gated per the catalog —
/// it's a "do something on my behalf" action, like <c>@do</c> / <c>@train</c>.
/// Failure replies (unknown set, busy) obey
/// <see cref="RemoteCommandManager.WarnOnDenial"/>; the success acknowledgement
/// is sent unconditionally.
/// </remarks>
public sealed class EquipHandler : IDisposable
{
    // Bare key the catalog/@help/tooltips show; Prefix is the wire-match form.
    private const string CatalogKey = "@equip";
    private const string Prefix = "@equip-";

    private readonly RemoteCommandManager _engine;
    private readonly EquipmentManager _equipment;
    private bool _disposed;

    public EquipHandler(RemoteCommandManager engine, EquipmentManager equipment)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(equipment);
        _engine = engine;
        _equipment = equipment;

        if (!RemoteCommandCatalog.TryGetCategory(CatalogKey, out PlayerRemoteControls category))
            throw new InvalidOperationException($"RemoteCommandCatalog missing entry for '{CatalogKey}'.");
        _engine.RegisterPrefixHandler(Prefix, category, OnEquip);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterPrefixHandler(Prefix);
    }

    private void OnEquip(RemoteCommandContext ctx)
    {
        // Prefix routing folds the set keyword in as the leading arg:
        // @equip-fighting → Args[0] == "fighting". An empty suffix can't
        // reach here — the engine only prefix-matches a non-empty remainder.
        if (ctx.Args.Count == 0)
        {
            if (_engine.WarnOnDenial) ctx.Reply("usage: @equip-<set>");
            return;
        }

        string keyword = ctx.Args[0];
        switch (_equipment.ApplyByKeyword(keyword))
        {
            case EquipResult.Applied:
                ctx.Reply($"equipping gear set '{keyword}'");
                break;
            case EquipResult.NoChange:
                ctx.Reply($"gear set '{keyword}' already worn");
                break;
            case EquipResult.NotFound:
                if (_engine.WarnOnDenial) ctx.Reply($"no gear set '{keyword}'");
                break;
            case EquipResult.Busy:
                if (_engine.WarnOnDenial) ctx.Reply("busy equipping");
                break;
        }
    }
}
