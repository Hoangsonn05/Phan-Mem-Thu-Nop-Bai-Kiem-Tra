namespace ExamTransfer.Infrastructure.Services;

internal static class QuizScoreAllocator
{
    private const int TotalUnits = 1000;

    public static IReadOnlyList<decimal> Allocate(int questionCount)
    {
        if (questionCount is < 1 or > 500)
            throw new ArgumentOutOfRangeException(
                nameof(questionCount),
                questionCount,
                "Số câu hỏi phải nằm trong khoảng 1 đến 500.");

        var baseUnits = TotalUnits / questionCount;
        var remainder = TotalUnits % questionCount;
        var result = new decimal[questionCount];
        for (var index = 0; index < questionCount; index++)
            result[index] = (baseUnits + (index < remainder ? 1 : 0)) / 100m;
        return result;
    }
}
