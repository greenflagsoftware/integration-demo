namespace SimpleLib;

/// <summary>
/// A calculator service with async and sync operations.
/// </summary>
public interface ICalculator
{
    int Add(int a, int b);
    Task<int> MultiplyAsync(int a, int b);
}

public class Calculator : ICalculator
{
    public int Add(int a, int b) => a + b;

    public async Task<int> MultiplyAsync(int a, int b)
    {
        await Task.Delay(1);
        return a * b;
    }

    public int Subtract(int a, int b) => a - b;
}

public static class MathHelper
{
    public static double Pi => 3.14159;

    public static long Factorial(int n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        long result = 1;
        for (int i = 2; i <= n; i++)
            result *= i;
        return result;
    }
}

public class DataService
{
    private readonly HttpClient _httpClient;

    public DataService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> GetDataAsync(string url)
    {
        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
