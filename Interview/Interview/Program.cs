//Console.WriteLine("Hello, World!");





var states = new[]
{
    "FL","FL","MD","NJ"
};

var lookup = states.CountStateAppearances();

foreach (var kvp in lookup)
{
    Console.WriteLine($"State {kvp.Key} has {kvp.Value} number of states");
}



var orders = new List<Order>()
{
    new Order() { Name = "Cus 1", Price = 5, IsRush = true },
    new Order() { Name = "Cus 2", Price = 25, IsRush = false },
    new Order() { Name = "Cus 3", Price = 57, IsRush = true },
    new Order() { Name = "Cus 1", Price = 5, IsRush = false },
    new Order() { Name = "Cus 6", Price = 59, IsRush = true },
    new Order() { Name = "Cus 78", Price = 5, IsRush = false },
    new Order() { Name = "Cus 90", Price = 5, IsRush = false },
    new Order() { Name = "Cus 178", Price = 5, IsRush = true },
};

//orders.PrintKitchenNumber();


public static class Extensions
{
    private static List<string> States { get; } = new List<string>()
    {
        "NY","IL","MA","PA","FL","MD","NJ"
    };

    public static Dictionary<string, int> CountStateAppearances(this IEnumerable<string> givenStates)
    {
        var stateLookup = States.ToDictionary(s => s, s => 0);

        foreach (var givenState in givenStates)
        {
            stateLookup[givenState] = stateLookup[givenState] + 1;
        }

        return stateLookup;
    }

    public static void PrintKitchenNumber(this IReadOnlyList<Order> orders)
    {
        foreach (var order in orders)
        {
            if(order?.IsRush == true)
            {
                Console.WriteLine("Kitchen 1");
            }
            else
            {
                Console.WriteLine("Kitchen 2");
            }
        }
    }
}


public class Order
{
    public required string Name { get; init; }
    public decimal Price { get; init; }
    public bool IsRush { get; init; }
}
