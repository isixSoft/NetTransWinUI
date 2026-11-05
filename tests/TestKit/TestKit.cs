/// <summary>
/// Shared throw-style assertion helpers for the console test harnesses
/// (zero NuGet — each harness counts failures around Run wrappers that
/// catch Exception). Test Programs inherit this class so call sites stay
/// bare That(...) / Equal(...) with zero call-site churn.
/// </summary>
public abstract class TestKit
{
    protected static void That(bool condition, string what)
    {
        if (!condition) throw new Exception(what);
    }

    protected static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what}：期望 [{expected}]，实际 [{actual}]");
    }
}
