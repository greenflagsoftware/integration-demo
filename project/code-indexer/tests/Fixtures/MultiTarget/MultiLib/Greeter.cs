namespace MultiLib;

public class Greeter
{
    public string Greet(string name) => $"Hello, {name}";

#if NET10_0
    public string GreetModern(string name) => Greet(name);
#endif
}
