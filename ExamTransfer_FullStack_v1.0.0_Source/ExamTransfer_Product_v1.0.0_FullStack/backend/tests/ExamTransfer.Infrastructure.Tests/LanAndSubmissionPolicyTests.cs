using System.Net;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Security;
using ExamTransfer.Infrastructure.Storage;
using ExamTransfer.Shared.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class LanAndSubmissionPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.168.50.10", true)]
    [InlineData("::ffff:192.168.50.11", true)]
    [InlineData("192.168.51.10", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("not-an-ip", false)]
    public void LanAccessPolicy_OnlyAllowsLoopbackOrConfiguredLocalSubnet(string address, bool expected)
    {
        var options = new ExamTransferOptions();
        options.Discovery.AdditionalAllowedCidrs.Add("192.168.50.0/24");
        var policy = new LanAccessPolicy(Options.Create(options));

        Assert.Equal(expected, policy.IsAllowed(address));
    }

    [Theory]
    [InlineData("bailam.zip", true)]
    [InlineData("BAILAM.RAR", true)]
    [InlineData("bai lam.7z", true)]
    [InlineData("bailam.pdf", false)]
    [InlineData("bailam.zip.exe", false)]
    public void StudentSubmissionPolicy_AllowsExactlyTheArchiveExtensions(string fileName, bool expected) =>
        Assert.Equal(expected, StudentSubmissionPolicy.IsAllowedExtension(fileName));

    [Fact]
    public void StudentSubmissionPolicy_UsesExactFixedLimits()
    {
        Assert.Equal(1, StudentSubmissionPolicy.MaxFileCount);
        Assert.Equal(10L * 1024 * 1024, StudentSubmissionPolicy.MaxBytes);
    }

    [Fact]
    public async Task ArchiveSignatureValidator_RejectsRenamedExecutableAndAcceptsMatchingArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), "ExamTransfer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var valid = Path.Combine(root, "valid.zip");
            var invalid = Path.Combine(root, "renamed.zip");
            await File.WriteAllBytesAsync(valid, [0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0]);
            await File.WriteAllBytesAsync(invalid, [0x4D, 0x5A, 0x90, 0, 0, 0, 0, 0]);

            Assert.True(await ArchiveSignatureValidator.MatchesExtensionAsync(valid, "valid.zip"));
            Assert.False(await ArchiveSignatureValidator.MatchesExtensionAsync(invalid, "renamed.zip"));

            foreach (var sample in new[]
            {
                (Name: "valid.rar", Signature: new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0, 0 }),
                (Name: "valid.7z", Signature: new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 0 })
            })
            {
                var path = Path.Combine(root, sample.Name);
                await File.WriteAllBytesAsync(path, sample.Signature);
                Assert.True(await ArchiveSignatureValidator.MatchesExtensionAsync(path, sample.Name));
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
