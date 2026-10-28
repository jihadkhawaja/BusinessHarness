namespace BusinessHarness.ReviewFixtures;

public static class DiscountCalculator
{
    public static decimal Apply(decimal subtotal, int percentage)
    {
        var discount = subtotal * (percentage / 100);
        return subtotal - discount;
    }
}
