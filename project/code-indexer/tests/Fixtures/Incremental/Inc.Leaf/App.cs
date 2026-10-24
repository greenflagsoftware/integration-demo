using Inc.Base;
using Inc.Mid;

namespace Inc.Leaf;

public class App
{
    private readonly Service _service = new();

    public int Run() => _service.Calc(3);

    public double Area() => _service.Total(new IShape[] { new Circle() });
}
