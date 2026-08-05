using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class PublicCloudExamFileDownloadContractTests
{
    [Fact]
    public void EdgeFunction_StripsExamArchiveBucketBeforeStorageSigning()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "backend",
            "supabase",
            "functions",
            "get-public-exam-file-url",
            "index.ts"));

        Assert.Contains(
            "normalizeExamArchiveObjectPath(file.object_path)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "file.object_path.split(\"/\")",
            source,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(
                    current.FullName,
                    "backend",
                    "supabase",
                    "functions")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
