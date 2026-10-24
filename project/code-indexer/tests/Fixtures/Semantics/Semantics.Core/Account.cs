namespace Semantics.Core;

// Constructor chaining, initializers, properties, events, delegates, records, structs, and enums.
public class Account
{
    public decimal Balance { get; private set; } = DefaultBalance(); // @balance

    private static decimal DefaultBalance() => 0m; // @default-balance

    public Account() : this(DefaultBalance()) { } // @ctor-default

    public Account(decimal start) // @ctor-start
    {
        Balance = start; // @write-balance
    }

    public event EventHandler? Changed; // @changed

    public void Deposit(decimal amount) // @deposit
    {
        Balance += amount; // @deposit-balance
        Changed?.Invoke(this, EventArgs.Empty); // @raise-changed
    }
}

public record Person(string Name, int Age); // @person

public struct Point // @point
{
    public int X;
    public int Y;
}

public enum Color { Red, Green } // @color

public delegate int Transformer(int x); // @transformer
