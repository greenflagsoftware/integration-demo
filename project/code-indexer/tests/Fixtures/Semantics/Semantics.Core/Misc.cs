namespace Semantics.Core;

// Attributes, lambdas, local functions, typeof/nameof, and target-typed new.
public class Misc
{
    [Obsolete("use something else")] public void Old() { } // @old

    public int UseLambda(Calculator c) // @uselambda
    {
        Func<int, int> f = x => c.Add(x, 1); // @lambda-add-int
        int Local(int y) => c.Add(y, 2); // @local-add-int
        return f(1) + Local(2);
    }

    public string TypeNames() => nameof(Calculator) + typeof(Account).Name; // @typenames

    public Calculator Make() => new(); // @target-typed-new
}
