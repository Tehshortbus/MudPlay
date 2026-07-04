namespace FujinTerm.Services;

// Contract every dialog ViewModel implements so DialogService can wait for its
// result. The VM raises CloseRequested when the user commits (OK / Save / Apply →
// non-null payload) or cancels (Cancel / X / Escape → default); the service
// completes the awaited task and closes the window. TResult is the payload type
// the dialog produces on commit; dialogs that only need to signal
// commit-vs-cancel use bool.
public interface IDialogViewModel<TResult>
{
    // Fired by the ViewModel when it wants the hosting window torn down. Payload
    // is the user's chosen value, or default on cancel / implicit close.
    event Action<TResult?>? CloseRequested;
}
