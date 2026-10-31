using System.Windows.Threading;

namespace CamRemote;

public sealed class WpfUi(Dispatcher dispatcher) : IUi
{
    public void Post(Action action) => dispatcher.BeginInvoke(action);

    public T Invoke<T>(Func<T> action) => dispatcher.Invoke(action);

    public IDisposable Every(TimeSpan interval, Action action)
    {
        var timer = new DispatcherTimer(interval, DispatcherPriority.Background, (_, _) => action(), dispatcher);
        timer.Start();
        return new Stopper(timer);
    }

    private sealed class Stopper(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
