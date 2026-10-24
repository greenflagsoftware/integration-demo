namespace Inc.Base;

public interface IShape
{
    double Area();
}

public class Circle : IShape
{
    public double Radius { get; init; } = 1;
    public double Area() => Math.PI * Radius * Radius;
}
