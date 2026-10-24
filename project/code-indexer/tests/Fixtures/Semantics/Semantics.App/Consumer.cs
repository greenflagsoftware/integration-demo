using Semantics.Core;

namespace Semantics.App;

// Cross-project use of Semantics.Core, plus a call into the base class library.
public class Consumer
{
    private readonly Calculator _calc = new Calculator(); // @app-new-calc

    public int Run() => _calc.Add(1, 2); // @app-add-int

    public double RunDouble() => _calc.Add(1.5, 2.5); // @app-add-double

    public int RunTwice() => _calc.Twice(4); // @app-twice

    public string RunEcho() => _calc.Echo("x"); // @app-echo

    public async Task<string> Fetch(HttpClient http) => await http.GetStringAsync("https://example.test"); // @app-http
}
