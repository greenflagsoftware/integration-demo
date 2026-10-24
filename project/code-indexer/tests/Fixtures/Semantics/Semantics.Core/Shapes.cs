namespace Semantics.Core;

// Interface, abstract and virtual members, overrides, and an explicit interface implementation.
public interface IShape
{
    double Area(); // @ishape-area
    string Name { get; } // @ishape-name
}

public abstract class ShapeBase : IShape
{
    public abstract double Area(); // @shapebase-area
    public virtual string Name => "shape"; // @shapebase-name
    public string Describe() => $"{Name}: {Area()}"; // @describe
}

public class Circle : ShapeBase
{
    public double Radius { get; init; } // @circle-radius
    public override double Area() => Math.PI * Radius * Radius; // @circle-area
    public override string Name => "circle"; // @circle-name
}

public class Square : ShapeBase
{
    public double Side { get; init; }
    public override double Area() => Side * Side; // @square-area
}

public class Triangle : IShape
{
    double IShape.Area() => 0.5; // @triangle-area
    public string Name => "triangle"; // @triangle-name
}

public static class ShapeUser
{
    public static double SumAreas(IEnumerable<IShape> shapes) // @sumareas
    {
        double total = 0;
        foreach (var s in shapes)
            total += s.Area(); // @call-ishape-area
        return total;
    }

    public static string Label(ShapeBase s) => s.Describe(); // @call-describe

    public static double CircleArea() => new Circle().Area(); // @call-circle-area
}
