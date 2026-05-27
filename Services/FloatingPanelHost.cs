using Avalonia;
using Avalonia.Controls;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

/// <summary>
/// In-house docking/floating panel framework. Each registered panel owns one
/// <see cref="UserControl"/> instance that the host reparents between a dock
/// container in the main window and a separate top-level <see cref="Window"/>.
/// The UserControl is never recreated, never duplicated — every reference
/// (data bindings, event subscriptions) survives state changes.
/// </summary>
/// <remarks>
/// <para>
/// State is one of <see cref="PanelState.Docked"/>, <see cref="PanelState.Floating"/>,
/// or <see cref="PanelState.Hidden"/>. Position / size apply only to
/// <c>Floating</c>; they are captured live from the floating window and
/// snapshotted on demand via <see cref="SnapshotLayouts"/>.
/// </para>
/// <para>
/// Persistence: the host does not write to disk itself. The wiring layer
/// (typically <see cref="AppServices"/>) hands persisted layouts in via
/// <see cref="ApplyLayouts"/> on profile load, and grabs them back via
/// <see cref="SnapshotLayouts"/> before save / profile swap.
/// </para>
/// <para>
/// Phase 0 ships infrastructure only. The first consumers are Phase 1's
/// LogPaneWindow and BackscrollWindow.
/// </para>
/// </remarks>
public sealed class FloatingPanelHost
{
    private sealed class Registration
    {
        public required string PanelId { get; init; }
        public required UserControl Content { get; init; }
        public required ContentControl DockContainer { get; init; }
        public required string FloatingTitle { get; init; }
        public Window? FloatingWindow { get; set; }
        public PanelLayout Layout { get; set; } = new();
    }

    private readonly Dictionary<string, Registration> _panels = new(StringComparer.OrdinalIgnoreCase);

    // Layouts pre-loaded via ApplyLayouts before the panels they describe are
    // registered. Looked up at Register time so a profile-loaded layout is
    // applied immediately, even if registration happens later.
    private readonly Dictionary<string, PanelLayout> _pendingLayouts = new(StringComparer.OrdinalIgnoreCase);

    private Window? _ownerWindow;

    /// <summary>Fired whenever a panel's state transitions (Docked / Floating / Hidden).</summary>
    public event Action? LayoutsChanged;

    /// <summary>
    /// Set the parent window for floating panels (the main app window).
    /// Called once during startup, alongside <see cref="DialogService.SetMainWindow"/>.
    /// </summary>
    public void SetOwnerWindow(Window owner) => _ownerWindow = owner;

    /// <summary>
    /// Register a panel. Idempotent on <paramref name="panelId"/> — re-registering
    /// with a different content control throws. Applies any persisted layout
    /// pulled in by an earlier <see cref="ApplyLayouts"/> call.
    /// </summary>
    public void Register(string panelId, UserControl content, ContentControl dockContainer, string floatingTitle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(panelId);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(dockContainer);

        if (_panels.TryGetValue(panelId, out Registration? existing))
        {
            if (!ReferenceEquals(existing.Content, content) ||
                !ReferenceEquals(existing.DockContainer, dockContainer))
            {
                throw new InvalidOperationException(
                    $"Panel '{panelId}' is already registered with different content / dock controls.");
            }
            return;
        }

        Registration reg = new()
        {
            PanelId = panelId,
            Content = content,
            DockContainer = dockContainer,
            FloatingTitle = floatingTitle ?? panelId,
        };

        if (_pendingLayouts.Remove(panelId, out PanelLayout? persisted))
        {
            reg.Layout = persisted;
        }

        _panels[panelId] = reg;
        ApplyState(reg, raiseEvent: false);
    }

    /// <summary>Move the panel into the dock container.</summary>
    public void Dock(string panelId) => Transition(panelId, PanelState.Docked);

    /// <summary>Move the panel into its own floating window.</summary>
    public void Float(string panelId) => Transition(panelId, PanelState.Floating);

    /// <summary>Hide the panel (closes the floating window if any; clears the dock slot).</summary>
    public void Hide(string panelId) => Transition(panelId, PanelState.Hidden);

    /// <summary>
    /// Reset every registered panel to its default state (<see cref="PanelState.Docked"/>)
    /// and forget any pending layouts. Bound to the View → Reset layout
    /// menu entry.
    /// </summary>
    public void ResetToDefault()
    {
        _pendingLayouts.Clear();
        foreach (Registration reg in _panels.Values)
        {
            reg.Layout = new PanelLayout();
            ApplyState(reg, raiseEvent: false);
        }
        LayoutsChanged?.Invoke();
    }

    /// <summary>Current state of <paramref name="panelId"/> (throws if not registered).</summary>
    public PanelState GetState(string panelId) => GetRegistration(panelId).Layout.State;

    /// <summary>Returns true if a panel with this id has been registered.</summary>
    public bool IsRegistered(string panelId) => _panels.ContainsKey(panelId);

    /// <summary>
    /// Replace all in-memory layouts with the supplied dictionary. Called by
    /// the wiring layer on <c>ProfileService.ProfileLoaded</c>. Layouts for
    /// already-registered panels are applied immediately; the rest are stashed
    /// and applied when their panels register.
    /// </summary>
    public void ApplyLayouts(IReadOnlyDictionary<string, PanelLayout>? layouts)
    {
        _pendingLayouts.Clear();
        if (layouts is null || layouts.Count == 0)
        {
            // No persisted state — reset every live panel to its default.
            foreach (Registration reg in _panels.Values)
            {
                reg.Layout = new PanelLayout();
                ApplyState(reg, raiseEvent: false);
            }
            LayoutsChanged?.Invoke();
            return;
        }

        foreach ((string id, PanelLayout layout) in layouts)
        {
            if (_panels.TryGetValue(id, out Registration? reg))
            {
                reg.Layout = layout;
                ApplyState(reg, raiseEvent: false);
            }
            else
            {
                _pendingLayouts[id] = layout;
            }
        }
        LayoutsChanged?.Invoke();
    }

    /// <summary>
    /// Capture every panel's current state into a fresh dictionary suitable
    /// for persisting onto <see cref="CharacterProfile.PanelLayouts"/>.
    /// Includes pending layouts for panels that haven't registered yet so a
    /// load-save-reload round-trip doesn't lose state.
    /// </summary>
    public Dictionary<string, PanelLayout> SnapshotLayouts()
    {
        Dictionary<string, PanelLayout> snapshot = new(StringComparer.OrdinalIgnoreCase);

        foreach (Registration reg in _panels.Values)
        {
            CaptureFloatingBounds(reg);
            snapshot[reg.PanelId] = new PanelLayout
            {
                State = reg.Layout.State,
                X = reg.Layout.X,
                Y = reg.Layout.Y,
                Width = reg.Layout.Width,
                Height = reg.Layout.Height,
                Z = reg.Layout.Z,
            };
        }

        foreach ((string id, PanelLayout pending) in _pendingLayouts)
        {
            if (!snapshot.ContainsKey(id)) snapshot[id] = pending;
        }

        return snapshot;
    }

    // ----- Internals -----------------------------------------------------

    private Registration GetRegistration(string panelId)
    {
        if (!_panels.TryGetValue(panelId, out Registration? reg))
            throw new InvalidOperationException($"Panel '{panelId}' is not registered.");
        return reg;
    }

    private void Transition(string panelId, PanelState target)
    {
        Registration reg = GetRegistration(panelId);
        if (reg.Layout.State == target && reg.Layout.State != PanelState.Floating) return;

        CaptureFloatingBounds(reg);
        reg.Layout.State = target;
        ApplyState(reg, raiseEvent: true);
    }

    private void ApplyState(Registration reg, bool raiseEvent)
    {
        switch (reg.Layout.State)
        {
            case PanelState.Docked:
                DetachFromFloating(reg);
                if (reg.DockContainer.Content != reg.Content)
                {
                    DetachFromAnyParent(reg.Content);
                    reg.DockContainer.Content = reg.Content;
                }
                reg.Content.IsVisible = true;
                break;

            case PanelState.Floating:
                if (reg.DockContainer.Content == reg.Content)
                {
                    reg.DockContainer.Content = null;
                }
                AttachToFloating(reg);
                break;

            case PanelState.Hidden:
                DetachFromFloating(reg);
                if (reg.DockContainer.Content == reg.Content)
                {
                    reg.DockContainer.Content = null;
                }
                break;
        }

        if (raiseEvent) LayoutsChanged?.Invoke();
    }

    private void AttachToFloating(Registration reg)
    {
        if (reg.FloatingWindow is null)
        {
            Window window = new()
            {
                Title = reg.FloatingTitle,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
            };

            // Closing the floating window collapses the panel to Hidden so the
            // user gets a clean "X means hide" experience rather than crashing
            // on a missing window next time something asks for state.
            window.Closing += (_, e) =>
            {
                // Detach content before the window disposes its visual tree.
                DetachFromAnyParent(reg.Content);
                reg.FloatingWindow = null;
                reg.Layout.State = PanelState.Hidden;
                LayoutsChanged?.Invoke();
            };

            reg.FloatingWindow = window;
        }

        DetachFromAnyParent(reg.Content);
        reg.FloatingWindow.Content = reg.Content;

        if (reg.Layout.Width > 0 && reg.Layout.Height > 0)
        {
            reg.FloatingWindow.Width = reg.Layout.Width;
            reg.FloatingWindow.Height = reg.Layout.Height;
        }
        if (reg.Layout.X != 0 || reg.Layout.Y != 0)
        {
            reg.FloatingWindow.Position = new PixelPoint(
                (int)reg.Layout.X, (int)reg.Layout.Y);
        }

        if (_ownerWindow is not null && reg.FloatingWindow.Owner is null)
        {
            reg.FloatingWindow.Show(_ownerWindow);
        }
        else
        {
            reg.FloatingWindow.Show();
        }
    }

    private static void DetachFromFloating(Registration reg)
    {
        if (reg.FloatingWindow is null) return;

        CaptureFloatingBounds(reg);
        Window w = reg.FloatingWindow;
        reg.FloatingWindow = null;
        w.Content = null;
        w.Close();
    }

    private static void CaptureFloatingBounds(Registration reg)
    {
        if (reg.FloatingWindow is null) return;
        reg.Layout.X = reg.FloatingWindow.Position.X;
        reg.Layout.Y = reg.FloatingWindow.Position.Y;
        reg.Layout.Width = reg.FloatingWindow.Width;
        reg.Layout.Height = reg.FloatingWindow.Height;
    }

    private static void DetachFromAnyParent(Control content)
    {
        switch (content.Parent)
        {
            case ContentControl cc when cc.Content == content:
                cc.Content = null;
                break;
            case Panel panel:
                panel.Children.Remove(content);
                break;
            case Decorator decorator when decorator.Child == content:
                decorator.Child = null;
                break;
        }
    }
}
