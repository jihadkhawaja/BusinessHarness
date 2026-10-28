namespace BusinessHarness.ReviewFixtures;

public static class PaginationCalculator
{
    public static int PageCount(int itemCount, int pageSize)
    {
        return itemCount / pageSize;
    }
}
