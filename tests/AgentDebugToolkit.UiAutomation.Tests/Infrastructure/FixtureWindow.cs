using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AgentDebugToolkit.UiAutomation.Tests.Infrastructure;

/// <summary>
/// Hosts a WPF window on a dedicated STA thread so the out-of-process CLI can automate it while
/// the test thread stays free. The window is shown without activation and has no title.
/// </summary>
internal sealed class FixtureWindow : IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);

    private readonly Thread _thread;
    private Window? _window;
    private Dispatcher? _dispatcher;

    private FixtureWindow(Func<UIElement> buildContent)
    {
        using var ready = new ManualResetEventSlim();
        Exception? startupError = null;
        _thread = new Thread(() =>
        {
            try
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                _window = new Window
                {
                    Width = 420,
                    Height = 320,
                    Left = 40,
                    Top = 40,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Content = buildContent(),
                };
                _window.Show();
                Hwnd = new WindowInteropHelper(_window).Handle;
            }
            catch (Exception ex)
            {
                startupError = ex;
            }
            finally
            {
                ready.Set();
            }

            if (startupError is null)
            {
                Dispatcher.Run();
            }
        })
        {
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!ready.Wait(StartupTimeout))
        {
            throw new TimeoutException("The fixture window did not start in time.");
        }

        if (startupError is not null)
        {
            throw new InvalidOperationException("The fixture window failed to start.", startupError);
        }

        // Wait until layout and rendering have run so UI Automation sees the realized content.
        Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    public IntPtr Hwnd { get; private set; }

    public string HwndArgument => $"0x{Hwnd.ToInt64():X}";

    public static FixtureWindow Create(Func<UIElement> buildContent) => new(buildContent);

    public void Invoke(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        _dispatcher!.Invoke(action, priority, CancellationToken.None, StartupTimeout);
    }

    public T Invoke<T>(Func<T> func)
    {
        return _dispatcher!.Invoke(func, DispatcherPriority.Normal, CancellationToken.None, StartupTimeout);
    }

    public void Dispose()
    {
        if (_dispatcher is null)
        {
            return;
        }

        try
        {
            _dispatcher.Invoke(() => _window?.Close(), DispatcherPriority.Normal, CancellationToken.None, StartupTimeout);
        }
        catch (Exception ex) when (ex is TimeoutException or TaskCanceledException or InvalidOperationException)
        {
            // Best effort; the dispatcher is shut down below either way.
        }

        _dispatcher.InvokeShutdown();
        _thread.Join(StartupTimeout);
    }
}
