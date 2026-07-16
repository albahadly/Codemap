using Microsoft.JSInterop;

namespace Codemap.Web.Services;

/// <summary>
/// Registers one global keydown listener (via a small hand-rolled interop script) and raises mapped
/// <see cref="ShortcutAction"/>s. Components subscribe to <see cref="ShortcutPressed"/> instead of
/// scattering @onkeydown handlers (spec §11.1 implementation note).
/// </summary>
public sealed class KeyboardShortcutService(IJSRuntime js) : IAsyncDisposable
{
    private DotNetObjectReference<KeyboardShortcutService>? _selfReference;
    private bool _initialized;

    public event Func<ShortcutAction, Task>? ShortcutPressed;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        _selfReference = DotNetObjectReference.Create(this);
        await js.InvokeVoidAsync("codemap.registerShortcuts", _selfReference);
    }

    [JSInvokable]
    public async Task OnKeyDown(string key, bool ctrlOrMeta, bool shift, bool inTextInput)
    {
        if (ShortcutMapper.Map(key, ctrlOrMeta, shift, inTextInput) is { } action && ShortcutPressed is { } handlers)
            await handlers.Invoke(action);
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
