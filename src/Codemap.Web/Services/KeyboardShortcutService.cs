using Microsoft.JSInterop;

namespace Codemap.Web.Services;

/// <summary>
/// Registers one global keydown listener (via a small hand-rolled interop script) and raises mapped
/// <see cref="ShortcutAction"/>s. Components subscribe to <see cref="ShortcutPressed"/> instead of
/// scattering @onkeydown handlers (spec §11.1 implementation note).
/// </summary>
public sealed class KeyboardShortcutService(IJSRuntime js, ILogger<KeyboardShortcutService> logger) : IAsyncDisposable
{
    private DotNetObjectReference<KeyboardShortcutService>? _selfReference;
    private bool _initialized;

    public event Func<ShortcutAction, Task>? ShortcutPressed;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        _selfReference = DotNetObjectReference.Create(this);
        try
        {
            await js.InvokeVoidAsync("codemap.registerShortcuts", _selfReference);
        }
        catch (JSException ex)
        {
            // codemap.js missing or stale (e.g. a tab left open across a rebuild references the old
            // fingerprinted script URL) — run without shortcuts instead of failing the circuit.
            logger.LogWarning(ex, "Keyboard shortcut registration failed; shortcuts disabled for this circuit.");
        }
    }

    [JSInvokable]
    public async Task OnKeyDown(string key, bool ctrlOrMeta, bool shift, bool inTextInput)
    {
        if (ShortcutMapper.Map(key, ctrlOrMeta, shift, inTextInput) is not { } action || ShortcutPressed is not { } handlers)
            return;
        try
        {
            await handlers.Invoke(action);
        }
        catch (JSDisconnectedException)
        {
            // circuit tearing down mid-shortcut — nothing to do
        }
        catch (Exception ex)
        {
            // Never let a shortcut handler fault propagate back through the interop promise: it
            // surfaces as an opaque "exception invoking 'OnKeyDown'" in the browser console.
            logger.LogError(ex, "Keyboard shortcut {Action} failed.", action.Kind);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_selfReference is not null)
        {
            try
            {
                await js.InvokeVoidAsync("codemap.unregisterShortcuts");
            }
            catch (JSDisconnectedException)
            {
                // circuit already gone — nothing to clean up client-side
            }
            _selfReference.Dispose();
        }
    }
}
