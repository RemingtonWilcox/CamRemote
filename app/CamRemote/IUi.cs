namespace CamRemote;

/// <summary>The window toolkit's UI thread, so the camera code doesn't depend on any one toolkit.</summary>
public interface IUi
{
    void Post(Action action);
    T Invoke<T>(Func<T> action);
    IDisposable Every(TimeSpan interval, Action action);
}

/// <summary>One camera setting as the phone page sees it.</summary>
public interface IPropRow
{
    PropCode Code { get; }
    bool Allows(long value);
    void Pick(long value);
    object? Snapshot();
}
