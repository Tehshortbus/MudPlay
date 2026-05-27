namespace FujinTerm.Services;

/// <summary>
/// Contract every dialog ViewModel implements so <see cref="DialogService"/>
/// can wait for its result. The VM raises <see cref="CloseRequested"/> when the
/// user commits (OK / Save / Apply → non-null payload) or cancels
/// (Cancel / X / Escape → <c>default</c>); the service completes the awaited
/// task and closes the window.
/// </summary>
/// <typeparam name="TResult">
/// The payload type the dialog produces on commit. For dialogs that only need
/// to signal commit-vs-cancel use <see cref="bool"/>.
/// </typeparam>
public interface IDialogViewModel<TResult>
{
    /// <summary>
    /// Fired by the ViewModel when it wants the hosting window torn down.
    /// Payload is the user's chosen value, or <c>default</c> on cancel /
    /// implicit close.
    /// </summary>
    event Action<TResult?>? CloseRequested;
}
