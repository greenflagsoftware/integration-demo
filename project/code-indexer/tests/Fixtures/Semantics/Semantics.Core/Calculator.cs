namespace Semantics.Core;

// Overloads, a generic method, params, and an extension method.
public class Calculator
{
    public int Add(int a, int b) => a + b; // @add-int
    public double Add(double a, double b) => a + b; // @add-double
    public string Add(string a, string b) => a + b; // @add-string

    public T Echo<T>(T value) => value; // @echo

    public int Total(params int[] values) // @total
    {
        int sum = 0;
        foreach (var v in values)
            sum = Add(sum, v); // @total-calls-add-int
        return sum;
    }
}

public static class CalculatorExtensions
{
    public static int Twice(this Calculator c, int x) => c.Add(x, x); // @twice
}
