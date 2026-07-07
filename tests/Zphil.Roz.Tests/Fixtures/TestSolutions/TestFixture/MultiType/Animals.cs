namespace TestFixture.MultiType;

public class Dog
{
    public string Speak() => "Woof";
    public string Name { get; } = "Dog";
}

public class Cat
{
    public string Speak() => "Meow";
    public string Name { get; } = "Cat";
}
