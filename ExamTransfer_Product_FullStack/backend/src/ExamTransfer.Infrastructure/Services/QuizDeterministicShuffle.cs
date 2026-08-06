using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ExamTransfer.Domain;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Infrastructure.Services;

internal static class QuizDeterministicShuffle
{
    internal const string AlgorithmVersion = "quiz-shuffle-v1";

    internal static IReadOnlyList<QuizQuestionDto> BuildSnapshot(
        IReadOnlyList<QuizQuestion> canonicalQuestions,
        Guid sessionId,
        Guid participantId,
        int examVersion)
    {
        return canonicalQuestions
            .OrderBy(question => QuestionSortKey(sessionId, participantId, examVersion, question.Id), StringComparer.Ordinal)
            .ThenBy(question => question.Id.ToString("D"), StringComparer.Ordinal)
            .Select((question, questionIndex) => new QuizQuestionDto(
                question.Id,
                question.Text,
                questionIndex + 1,
                question.Points,
                question.Multiple,
                question.Choices
                    .OrderBy(choice => ChoiceSortKey(sessionId, participantId, examVersion, question.Id, choice.Id), StringComparer.Ordinal)
                    .ThenBy(choice => choice.Id.ToString("D"), StringComparer.Ordinal)
                    .Select((choice, choiceIndex) => new QuizChoiceDto(
                        choice.Id,
                        choice.Text,
                        choiceIndex + 1))
                    .ToList()))
            .ToList();
    }

    internal static string QuestionSortKey(
        Guid sessionId,
        Guid participantId,
        int examVersion,
        Guid questionId) =>
        Hash($"{AlgorithmVersion}|question|{sessionId:D}|{participantId:D}|{examVersion.ToString(CultureInfo.InvariantCulture)}|{questionId:D}");

    internal static string ChoiceSortKey(
        Guid sessionId,
        Guid participantId,
        int examVersion,
        Guid questionId,
        Guid choiceId) =>
        Hash($"{AlgorithmVersion}|choice|{sessionId:D}|{participantId:D}|{examVersion.ToString(CultureInfo.InvariantCulture)}|{questionId:D}|{choiceId:D}");

    private static string Hash(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
}
