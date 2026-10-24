using Inc.Base;

namespace Inc.Mid;

public class Service
{
    public double Total(IEnumerable<IShape> shapes)
    {
        var areas = new List<double>();
        foreach (var shape in shapes)
            areas.Add(shape.Area());
        return areas.Sum();
    }

    public int Calc(int x) => MathUtil.Twice(x);
}
