using SutRules.Language;

namespace SutRules.Tests.Language;

/// <summary>
/// Approval tests for compiler diagnostics: each <c>broken/*.rule</c> has a <c>.expected</c> file with every
/// diagnostic the compiler must produce (position, code, message). Error messages are part of the product for rule
/// authors, so a change to one is a reviewed diff, not an accident. Set SUTRULES_APPROVE=1 to rewrite the files.
/// </summary>
public sealed class DiagnosticsApprovalTests
{
    private static string BrokenDirectory => Path.Combine(AppContext.BaseDirectory, "Language", "broken");

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(BrokenDirectory, "*.rule").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Diagnostics_match_the_approved_file(string file)
    {
        var bag = new DiagnosticBag(file);
        var rule = RuleCompiler.Compile(File.ReadAllText(Path.Combine(BrokenDirectory, file)), TestData.Bundle.Catalog, bag);
        var actual = string.Join("\n", bag.Items.Select(d => d.ToString())) + "\n";

        var expectedPath = Path.Combine(TestData.RepositoryRoot, "tests", "SutRules.Tests", "Language", "broken", Path.ChangeExtension(file, ".expected"));
        if (Environment.GetEnvironmentVariable("SUTRULES_APPROVE") == "1")
        {
            File.WriteAllText(expectedPath, actual);
        }

        rule.Should().BeNull("a broken rule must not compile");
        bag.Items.Should().NotBeEmpty();
        actual.Should().Be(File.ReadAllText(expectedPath).Replace("\r\n", "\n", StringComparison.Ordinal));
    }
}
